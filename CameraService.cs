using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Mirror;

public sealed record CameraDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record VideoMode(uint Width, uint Height, int Fps)
{
    public string Key => $"{Width}x{Height}@{Fps}";
    public override string ToString() => $"{Width} × {Height} · {Fps} fps";
}

/// <summary>Wraps one open camera: live BGRA frames, recording and picture controls.</summary>
public sealed class CameraService : IAsyncDisposable
{
    private static readonly string WorkFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirror", "recording");

    private readonly MediaCapture _capture = new();
    private readonly object _frameLock = new();
    private MediaFrameReader? _reader;
    private byte[]? _frame;
    private int _frameWidth, _frameHeight;
    private readonly object _pendingLock = new();
    private readonly Dictionary<MediaDeviceControl, double> _pendingValues = [];
    private bool _pendingWorkerRunning;
    // Read by the frame thread to feed the encoder.
    private volatile ActiveRecording? _recording;
    private byte[]? _blurInput, _blurOutput;
    private int _blurBusy;

    public event Action? FrameReady;
    public event Action<string>? Failed;

    public IReadOnlyList<VideoMode> Modes { get; private set; } = [];
    public VideoMode? CurrentMode { get; private set; }
    public bool IsRecording => _recording is not null;

    /// <summary>
    /// True while the recording's mirror effect also reaches the preview frames, which happens on
    /// cameras that feed preview and recording from one stream. The UI must not flip them again.
    /// </summary>
    public bool FramesAlreadyMirrored { get; private set; }

    /// <summary>When set, preview frames and photos have their background blurred. Recordings do not.</summary>
    public BackgroundBlur? Blur { get; set; }

    public MediaDeviceControl Brightness { get; private set; } = null!;
    public MediaDeviceControl Contrast { get; private set; } = null!;

    public static async Task<List<CameraDevice>> ListAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .Where(g => g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color))
            .Select(g => new CameraDevice(g.Id, g.DisplayName))
            .ToList();
    }

    public async Task StartAsync(string cameraId, string? modeKey)
    {
        var group = await MediaFrameSourceGroup.FromIdAsync(cameraId)
                    ?? throw new InvalidOperationException(Loc.T("CameraDisconnected"));

        _capture.Failed += (_, e) => Failed?.Invoke(e.Message);
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });
        // Each property read returns a new wrapper, so keep one per control as a stable queue key.
        Brightness = _capture.VideoDeviceController.Brightness;
        Contrast = _capture.VideoDeviceController.Contrast;

        var colorSources = _capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        var source = colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                     ?? colorSources.First();

        Modes = source.SupportedFormats
            .Where(f => f.VideoFormat is not null)
            .Select(ToMode)
            .Distinct()
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ToList();

        CurrentMode = Modes.FirstOrDefault(m => m.Key == modeKey)
                      ?? Modes.FirstOrDefault(m => m.Fps >= 24)
                      ?? Modes.FirstOrDefault();

        if (CurrentMode is not null)
        {
            // Devices with separate preview and record pins must use the same format on both,
            // otherwise the recording would not match what is on screen.
            foreach (var s in colorSources)
            {
                var format = s.SupportedFormats
                    .Where(f => f.VideoFormat is not null && ToMode(f) == CurrentMode)
                    .OrderBy(f => SubtypeCost(f.Subtype))
                    .FirstOrDefault();
                if (format is not null)
                    await s.SetFormatAsync(format);
            }
        }

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException(Loc.T("PreviewStartFailed", status));
    }

    private static VideoMode ToMode(MediaFrameFormat f) => new(
        f.VideoFormat.Width,
        f.VideoFormat.Height,
        (int)Math.Round(f.FrameRate.Numerator / (double)Math.Max(1u, f.FrameRate.Denominator)));

    // Uncompressed formats avoid a JPEG decode on every frame.
    private static int SubtypeCost(string subtype) => subtype.ToUpperInvariant() switch
    {
        "NV12" => 0,
        "YUY2" => 1,
        _ => 2,
    };

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
            return;
        // When the camera captured this frame, on the same clock as Stopwatch (QueryPerformanceCounter).
        long capturedTicks = frame!.SystemRelativeTime?.Ticks ?? QpcTicks(Stopwatch.GetTimestamp());

        SoftwareBitmap? converted = null;
        try
        {
            var bgra = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                bgra = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
            ProcessFrame(bgra.PixelWidth, bgra.PixelHeight, capturedTicks, buffer => bgra.CopyToBuffer(buffer.AsBuffer()));
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Takes one BGRA frame (written by <paramref name="fill"/>) through effects, preview and recording.</summary>
    private void ProcessFrame(int width, int height, long capturedTicks, Action<byte[]> fill)
    {
        int size = width * height * 4;
        var blur = Blur;
        if (blur is null)
        {
            lock (_frameLock)
            {
                if (_frame?.Length != size)
                    _frame = new byte[size];
                fill(_frame);
                _frameWidth = width;
                _frameHeight = height;
            }
        }
        else
        {
            // Blurring takes a while, so do it outside the lock and drop frames that arrive meanwhile.
            if (Interlocked.Exchange(ref _blurBusy, 1) == 1)
                return;
            try
            {
                if (_blurInput?.Length != size)
                {
                    _blurInput = new byte[size];
                    _blurOutput = new byte[size];
                }
                fill(_blurInput);
                blur.Apply(_blurInput, width, height, _blurOutput!);
                var composed = _blurOutput!;
                lock (_frameLock)
                {
                    (_frame, _blurOutput) = (_blurOutput, _frame?.Length == size ? _frame : new byte[size]);
                    _frameWidth = width;
                    _frameHeight = height;
                }

                // This thread only writes into the other buffer next, so reading `composed` here is safe.
                if (_recording is { Encoder: { } encoder } recording)
                    encoder.Add(composed, width, height, capturedTicks - recording.VideoStartTicks);
            }
            finally
            {
                Volatile.Write(ref _blurBusy, 0);
            }
        }
        FrameReady?.Invoke();
    }

    // ---------- Demo mode (README screenshots): a still picture stands in for the camera ----------

    private System.Threading.Timer? _demoTimer;

    /// <summary>True when frames come from a still picture instead of a camera.</summary>
    public bool IsDemo { get; private set; }

    public void StartDemo(BackgroundImage still)
    {
        IsDemo = true;
        Modes = [new VideoMode((uint)still.Width, (uint)still.Height, 30)];
        CurrentMode = Modes[0];
        _demoTimer = new System.Threading.Timer(_ => ProcessFrame(still.Width, still.Height,
            QpcTicks(Stopwatch.GetTimestamp()),
            buffer => Buffer.BlockCopy(still.Pixels, 0, buffer, 0, still.Pixels.Length)), null, 0, 33);
    }

    /// <summary>
    /// Queues a picture setting. USB cameras take a while to accept each write, so writes run off the
    /// UI thread and only the newest value per control is sent, which keeps slider drags smooth.
    /// </summary>
    public void SetControlValue(MediaDeviceControl control, double value)
    {
        lock (_pendingLock)
        {
            _pendingValues[control] = value;
            if (_pendingWorkerRunning)
                return;
            _pendingWorkerRunning = true;
        }
        _ = Task.Run(ApplyPendingValues);
    }

    private void ApplyPendingValues()
    {
        while (true)
        {
            KeyValuePair<MediaDeviceControl, double> next;
            lock (_pendingLock)
            {
                if (_pendingValues.Count == 0)
                {
                    _pendingWorkerRunning = false;
                    return;
                }
                next = _pendingValues.First();
                _pendingValues.Remove(next.Key);
            }

            try
            {
                next.Key.TrySetValue(next.Value);
            }
            catch (Exception)
            {
                // The device may have closed mid-drag; a lost adjustment is harmless.
            }
        }
    }

    /// <summary>Runs <paramref name="use"/> with the latest frame while it cannot be overwritten.</summary>
    public void UseFrame(Action<byte[], int, int> use)
    {
        lock (_frameLock)
        {
            if (_frame is not null)
                use(_frame, _frameWidth, _frameHeight);
        }
    }

    /// <summary>Starts recording to <paramref name="path"/>.</summary>
    /// <returns>False if sound was wanted but the microphone could not be opened.</returns>
    public async Task<bool> StartRecordingAsync(string path, bool mirror, bool withAudio)
    {
        var recording = new ActiveRecording(path);
        bool audioOk = true;
        try
        {
            if (withAudio)
            {
                audioOk = await TryPrepareAudioAsync(recording);
            }

            if (Blur is not null)
            {
                // With a background effect the camera's own recording would not contain it, so the
                // processed preview frames are encoded instead.
                await StartEncodingAsync(recording, path, mirror);
                return audioOk;
            }

            if (mirror)
            {
                await _capture.AddVideoEffectAsync(
                    new VideoTransformEffectDefinition { Mirror = MediaMirroringOptions.Horizontal },
                    MediaStreamType.VideoRecord);
                FramesAlreadyMirrored =
                    _capture.MediaCaptureSettings.VideoDeviceCharacteristic != VideoDeviceCharacteristic.AllStreamsIndependent;
            }

            // With a separate audio file the video goes to the work folder until the two are combined.
            recording.VideoPath = recording.AudioRecorder is null
                ? path
                : Path.Combine(WorkFolder, "video.mp4");

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Audio = null;
            var file = await CreateFileAsync(recording.VideoPath);
            recording.VideoRecorder = await _capture.PrepareLowLagRecordToStorageFileAsync(profile, file);

            // Both recorders are already prepared, so these starts are close together. Audio goes
            // first; the moments before the video began are trimmed when the files are combined.
            if (recording.AudioRecorder is not null)
            {
                await recording.AudioRecorder.StartAsync();
                recording.AudioStarted = Stopwatch.GetTimestamp();
            }
            await recording.VideoRecorder.StartAsync();
            recording.VideoStarted = Stopwatch.GetTimestamp();

            _recording = recording;
            return audioOk;
        }
        catch
        {
            await AbandonAsync(recording);
            throw;
        }
    }

    private async Task StartEncodingAsync(ActiveRecording recording, string path, bool mirror)
    {
        recording.VideoPath = recording.AudioRecorder is null ? path : Path.Combine(WorkFolder, "video.mp4");
        int width, height;
        lock (_frameLock)
        {
            width = _frameWidth > 0 ? _frameWidth : (int)(CurrentMode?.Width ?? 1280);
            height = _frameHeight > 0 ? _frameHeight : (int)(CurrentMode?.Height ?? 720);
        }
        string videoPath = recording.VideoPath;
        int fps = CurrentMode?.Fps ?? 30;
        recording.Encoder = await Task.Run(() => new FrameEncoder(videoPath, width, height, fps, mirror));

        if (recording.AudioRecorder is not null)
        {
            await recording.AudioRecorder.StartAsync();
            recording.AudioStarted = Stopwatch.GetTimestamp();
        }
        recording.VideoStarted = Stopwatch.GetTimestamp();
        recording.VideoStartTicks = QpcTicks(recording.VideoStarted);
        _recording = recording;
    }

    private static long QpcTicks(long timestamp) =>
        (long)(timestamp * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    private static async Task<bool> TryPrepareAudioAsync(ActiveRecording recording)
    {
        try
        {
            Directory.CreateDirectory(WorkFolder);
            foreach (var leftover in Directory.GetFiles(WorkFolder))
                File.Delete(leftover);

            var microphone = new MediaCapture();
            recording.Microphone = microphone;
            await microphone.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            });

            recording.AudioPath = Path.Combine(WorkFolder, "audio.m4a");
            var file = await CreateFileAsync(recording.AudioPath);
            recording.AudioRecorder = await microphone.PrepareLowLagRecordToStorageFileAsync(
                MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High), file);
            return true;
        }
        catch (Exception e)
        {
            // A missing or blocked microphone should not prevent filming.
            Debug.WriteLine(e);
            recording.Microphone?.Dispose();
            recording.Microphone = null;
            recording.AudioRecorder = null;
            return false;
        }
    }

    private static async Task<StorageFile> CreateFileAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        return await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
    }

    private async Task AbandonAsync(ActiveRecording recording)
    {
        try
        {
            await _capture.ClearEffectsAsync(MediaStreamType.VideoRecord);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        FramesAlreadyMirrored = false;
        recording.Microphone?.Dispose();
        if (recording.Encoder is { } encoder)
        {
            try
            {
                encoder.Finish();
            }
            catch (Exception e)
            {
                // Nothing was recorded yet; the error is expected.
                Debug.WriteLine(e);
            }
            if (recording.VideoPath is { } partial && File.Exists(partial))
                File.Delete(partial);
        }
    }

    /// <summary>Stops and finalizes the recording.</summary>
    /// <returns>False if the video was saved without its sound.</returns>
    public async Task<bool> StopRecordingAsync()
    {
        var recording = _recording;
        if (recording is null)
            return true;
        _recording = null;

        try
        {
            if (recording.Encoder is { } encoder)
            {
                await Task.Run(encoder.Finish);
                Debug.WriteLine($"encoded {encoder.FramesWritten} frames, dropped {encoder.FramesDropped}");
            }
            else
            {
                await recording.VideoRecorder!.StopAsync();
                await recording.VideoRecorder.FinishAsync();
            }
        }
        finally
        {
            await StopMicrophoneAsync(recording);
            await _capture.ClearEffectsAsync(MediaStreamType.VideoRecord);
            FramesAlreadyMirrored = false;
        }

        if (recording.AudioRecorder is null)
            return true;

        return await Task.Run(() => CombineFiles(recording));
    }

    private static async Task StopMicrophoneAsync(ActiveRecording recording)
    {
        try
        {
            if (recording.AudioRecorder is not null)
            {
                await recording.AudioRecorder.StopAsync();
                await recording.AudioRecorder.FinishAsync();
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        recording.Microphone?.Dispose();
    }

    private static bool CombineFiles(ActiveRecording recording)
    {
        long leadTicks = (recording.VideoStarted - recording.AudioStarted) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        try
        {
            Remuxer.Combine(recording.VideoPath!, recording.AudioPath!, leadTicks, recording.OutputPath);
            File.Delete(recording.VideoPath!);
            File.Delete(recording.AudioPath!);
            return true;
        }
        catch (Exception e)
        {
            // Keep the picture rather than losing the whole recording.
            Debug.WriteLine(e);
            File.Delete(recording.OutputPath);
            File.Move(recording.VideoPath!, recording.OutputPath);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_demoTimer is not null)
            await _demoTimer.DisposeAsync();
        try
        {
            await StopRecordingAsync();
        }
        catch (Exception e)
        {
            // The device may already be gone; still release everything below.
            Debug.WriteLine(e);
        }

        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try
            {
                await _reader.StopAsync();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
            _reader.Dispose();
        }
        _capture.Dispose();
    }

    private sealed class ActiveRecording(string outputPath)
    {
        public string OutputPath { get; } = outputPath;
        public string? VideoPath { get; set; }
        public string? AudioPath { get; set; }
        public MediaCapture? Microphone { get; set; }
        public LowLagMediaRecording? VideoRecorder { get; set; }
        public LowLagMediaRecording? AudioRecorder { get; set; }
        public FrameEncoder? Encoder { get; set; }
        public long VideoStartTicks { get; set; }
        public long AudioStarted { get; set; }
        public long VideoStarted { get; set; }
    }
}
