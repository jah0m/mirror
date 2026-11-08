using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mirror;

/// <summary>
/// Encodes the app's own processed frames (with the background effect) to an H.264 MP4.
/// Frames are queued and encoded on a separate thread; if the encoder falls behind, frames are
/// dropped rather than slowing the preview. Uses the graphics card's encoder when Windows offers one.
/// </summary>
internal sealed class FrameEncoder
{
    private const int QueueLength = 4;

    private readonly BlockingCollection<(byte[] Pixels, int Width, long Time)> _queue = new(QueueLength);
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly int _width, _height, _fps;
    private readonly bool _flip;
    private readonly string _path;
    private Exception? _error;
    private long _lastTime = -1;

    public int FramesWritten { get; private set; }
    public int FramesDropped { get; private set; }

    public FrameEncoder(string path, int width, int height, int fps, bool flip)
    {
        _path = path;
        _width = width & ~1;   // H.264 needs even dimensions.
        _height = height & ~1;
        _fps = Math.Max(1, fps);
        _flip = flip;

        _thread = new Thread(Run) { IsBackground = true, Name = "Mirror encoder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait();
        if (_error is not null)
            throw new InvalidOperationException(Loc.T("EncoderCreateFailed", _error.Message), _error);
    }

    /// <summary>Queues a copy of a BGRA frame. <paramref name="time"/> is in 100 ns units from the start.</summary>
    public void Add(byte[] bgra, int width, int height, long time)
    {
        if ((width & ~1) != _width || (height & ~1) != _height || time <= _lastTime || _queue.IsAddingCompleted)
            return;

        if (!_pool.TryTake(out var copy) || copy.Length != bgra.Length)
            copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);
        try
        {
            if (_queue.TryAdd((copy, width, time)))
            {
                _lastTime = time;
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Finish() was called while this frame was on its way.
        }
        FramesDropped++;
        _pool.Add(copy);
    }

    /// <summary>Writes the remaining frames and closes the file.</summary>
    public void Finish()
    {
        _queue.CompleteAdding();
        _thread.Join();
        if (_error is not null)
            throw new InvalidOperationException(Loc.T("EncodeFailed", _error.Message), _error);
        if (FramesWritten == 0)
            throw new InvalidOperationException(Loc.T("NoFrames"));
    }

    private void Run()
    {
        IMFSinkWriter? writer = null;
        bool started = false;
        try
        {
            MF.Check(MF.MFStartup(MF.Version, 0));
            started = true;
            uint stream;
            try
            {
                (writer, stream) = CreateWriter(hardware: true);
            }
            catch (Exception e)
            {
                // Some graphics drivers refuse this input format; the built-in software encoder always works.
                Debug.WriteLine(e);
                (writer, stream) = CreateWriter(hardware: false);
            }
            _ready.Set();

            long duration = TimeSpan.TicksPerSecond / _fps;
            int rowBytes = _width * 4;
            uint size = (uint)(rowBytes * _height);
            var row = new byte[rowBytes];
            foreach (var (pixels, sourceWidth, time) in _queue.GetConsumingEnumerable())
            {
                MF.Check(MF.MFCreateMemoryBuffer(size, out var buffer));
                buffer.Lock(out IntPtr target, out _, out _);
                try
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int sourceRow = y * sourceWidth * 4;
                        if (_flip)
                        {
                            for (int x = 0; x < _width; x++)
                                Buffer.BlockCopy(pixels, sourceRow + (_width - 1 - x) * 4, row, x * 4, 4);
                            Marshal.Copy(row, 0, target + y * rowBytes, rowBytes);
                        }
                        else
                        {
                            Marshal.Copy(pixels, sourceRow, target + y * rowBytes, rowBytes);
                        }
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.SetCurrentLength(size);
                _pool.Add(pixels);

                MF.Check(MF.MFCreateSample(out var sample));
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(duration);
                writer.WriteSample(stream, sample);
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
                FramesWritten++;
            }

            if (FramesWritten > 0)
                writer.Finish();
        }
        catch (Exception e)
        {
            _error = e;
            _queue.CompleteAdding();
            foreach (var _ in _queue.GetConsumingEnumerable())
            {
                // Drain so a waiting Add cannot block.
            }
        }
        finally
        {
            _ready.Set();
            if (writer is not null)
                Marshal.ReleaseComObject(writer);
            if (started)
                MF.MFShutdown();
        }
    }

    private (IMFSinkWriter Writer, uint Stream) CreateWriter(bool hardware)
    {
        IntPtr attributes = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            MF.Check(MF.MFCreateAttributes(out attributes, 1));
            MF.Attributes(attributes).SetUINT32(MF.EnableHardwareTransforms, hardware ? 1u : 0u);
            if (File.Exists(_path))
                File.Delete(_path);
            MF.Check(MF.MFCreateSinkWriterFromURL(_path, IntPtr.Zero, attributes, out var writer));

            MF.Check(MF.MFCreateMediaType(out output));
            var o = MF.Attributes(output);
            o.SetGUID(MF.MajorType, MF.Video);
            o.SetGUID(MF.Subtype, MF.H264);
            // About 0.2 bits per pixel: 12 Mbit/s for 1080p30, close to what the camera's own recordings use.
            o.SetUINT32(MF.AvgBitrate, (uint)Math.Min(40_000_000L, (long)_width * _height * _fps / 5));
            o.SetUINT32(MF.InterlaceMode, MF.InterlaceProgressive);
            o.SetUINT64(MF.FrameSize, MF.Pack((uint)_width, (uint)_height));
            o.SetUINT64(MF.FrameRate, MF.Pack((uint)_fps, 1));
            o.SetUINT64(MF.PixelAspectRatio, MF.Pack(1, 1));
            writer.AddStream(output, out uint stream);

            MF.Check(MF.MFCreateMediaType(out input));
            var i = MF.Attributes(input);
            i.SetGUID(MF.MajorType, MF.Video);
            i.SetGUID(MF.Subtype, MF.Rgb32);
            i.SetUINT32(MF.InterlaceMode, MF.InterlaceProgressive);
            i.SetUINT64(MF.FrameSize, MF.Pack((uint)_width, (uint)_height));
            i.SetUINT64(MF.FrameRate, MF.Pack((uint)_fps, 1));
            i.SetUINT64(MF.PixelAspectRatio, MF.Pack(1, 1));
            // Positive stride: rows are stored top to bottom, as the camera frames are.
            i.SetUINT32(MF.DefaultStride, (uint)(_width * 4));
            try
            {
                writer.SetInputMediaType(stream, input, IntPtr.Zero);
                writer.BeginWriting();
            }
            catch
            {
                Marshal.ReleaseComObject(writer);
                throw;
            }
            return (writer, stream);
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (output != IntPtr.Zero) Marshal.Release(output);
            if (input != IntPtr.Zero) Marshal.Release(input);
        }
    }
}
