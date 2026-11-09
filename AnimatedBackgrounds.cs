using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mirror;

/// <summary>A background shown behind the person, supplied already scaled to the frame.</summary>
public interface IReplacementBackground
{
    /// <summary>
    /// Runs <paramref name="use"/> with BGRA pixels that cover <paramref name="width"/> × <paramref name="height"/>.
    /// Returns false if no picture is ready yet (an animated background still starting up).
    /// </summary>
    bool Use(int width, int height, bool flip, Action<byte[]> use);
}

/// <summary>Scales a picture to cover a frame, centred and cropped.</summary>
public static class ImageFit
{
    public static void Cover(BackgroundImage image, bool flip, int width, int height, byte[] pixels)
    {
        float scale = Math.Max((float)width / image.Width, (float)height / image.Height);
        float offsetX = (image.Width * scale - width) / 2, offsetY = (image.Height * scale - height) / 2;
        int iw = image.Width, ih = image.Height;
        byte[] src = image.Pixels;

        // Column positions are the same for every row, so work them out once.
        var x0s = new int[width];
        var x1s = new int[width];
        var wxs = new float[width];
        for (int x = 0; x < width; x++)
        {
            int tx = flip ? width - 1 - x : x;
            float sx = Math.Clamp((tx + 0.5f + offsetX) / scale - 0.5f, 0, iw - 1.001f);
            x0s[x] = (int)sx;
            x1s[x] = Math.Min(x0s[x] + 1, iw - 1);
            wxs[x] = sx - x0s[x];
        }

        Parallel.For(0, height, y =>
        {
            float sy = Math.Clamp((y + 0.5f + offsetY) / scale - 0.5f, 0, ih - 1.001f);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, ih - 1);
            float wy = sy - y0;
            int row0 = y0 * iw, row1 = y1 * iw, p = y * width * 4;
            for (int x = 0; x < width; x++, p += 4)
            {
                int a = (row0 + x0s[x]) * 4, b = (row0 + x1s[x]) * 4, c = (row1 + x0s[x]) * 4, d = (row1 + x1s[x]) * 4;
                float wx = wxs[x];
                for (int k = 0; k < 3; k++)
                {
                    float top = src[a + k] + (src[b + k] - src[a + k]) * wx;
                    float bottom = src[c + k] + (src[d + k] - src[c + k]) * wx;
                    pixels[p + k] = (byte)(top + (bottom - top) * wy + 0.5f);
                }
                pixels[p + 3] = 255;
            }
        });
    }
}

/// <summary>A still picture, scaled once per frame size.</summary>
public sealed class StillBackground(BackgroundImage image) : IReplacementBackground
{
    private (bool Flip, int Width, int Height, byte[] Pixels)? _fitted;

    public bool Use(int width, int height, bool flip, Action<byte[]> use)
    {
        var fitted = _fitted;
        if (fitted is not { } f || f.Flip != flip || f.Width != width || f.Height != height)
        {
            var pixels = new byte[width * height * 4];
            ImageFit.Cover(image, flip, width, height, pixels);
            _fitted = fitted = (flip, width, height, pixels);
        }
        use(fitted.Value.Pixels);
        return true;
    }
}

/// <summary>A sequence of pictures with presentation times, looping forever.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>The next picture and how long to show the previous one before it.</summary>
    BackgroundImage Next(out TimeSpan wait);
}

/// <summary>
/// Plays an <see cref="IFrameSource"/> on its own thread and keeps the current picture scaled to the
/// camera frame, so the frame thread only has to blend it in.
/// </summary>
public sealed class AnimatedBackground : IReplacementBackground, IDisposable
{
    private readonly Func<IFrameSource> _open;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private volatile bool _stopping;

    // Front: what the frame thread blends in. Back: being prepared by the player thread.
    private byte[]? _front, _back;
    private (int Width, int Height, bool Flip) _frontShape, _target = (0, 0, false);
    private BackgroundImage? _latest;

    public AnimatedBackground(Func<IFrameSource> open)
    {
        _open = open;
        _thread = new Thread(Run) { IsBackground = true, Name = "Mirror background player" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool Use(int width, int height, bool flip, Action<byte[]> use)
    {
        lock (_lock)
        {
            _target = (width, height, flip);
            if (_front is null || _frontShape != _target)
            {
                // The size or direction changed (or nothing is ready): scale the latest picture now.
                if (_latest is null)
                    return false;
                _front = new byte[width * height * 4];
                ImageFit.Cover(_latest, flip, width, height, _front);
                _frontShape = _target;
            }
            use(_front);
            return true;
        }
    }

    private void Run()
    {
        IFrameSource? source = null;
        try
        {
            source = _open();
            var clock = Stopwatch.StartNew();
            TimeSpan due = TimeSpan.Zero;
            while (!_stopping)
            {
                var picture = source.Next(out var wait);
                due += wait;
                var ahead = due - clock.Elapsed;
                if (ahead > TimeSpan.Zero)
                    Thread.Sleep(ahead);
                else if (ahead < TimeSpan.FromSeconds(-0.5))
                    due = clock.Elapsed; // Fell far behind (e.g. the PC was busy): carry on from now.

                (int width, int height, bool flip) target;
                lock (_lock)
                    target = _target;
                if (target.width > 0)
                {
                    int size = target.width * target.height * 4;
                    if (_back?.Length != size)
                        _back = new byte[size];
                    ImageFit.Cover(picture, target.flip, target.width, target.height, _back);
                }

                lock (_lock)
                {
                    _latest = picture;
                    if (target.width > 0 && _target == target)
                    {
                        (_front, _back) = (_back, _front);
                        _frontShape = target;
                    }
                }
            }
        }
        catch (Exception e)
        {
            // An unreadable file leaves the last picture (or the blur) in place.
            Debug.WriteLine(e);
        }
        finally
        {
            source?.Dispose();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>Decodes a video file with Media Foundation, looping at the end. Sound is ignored.</summary>
public sealed class VideoFrames : IFrameSource
{
    private readonly string _path;
    private IMFSourceReader _reader = null!;
    private int _width, _height, _stride;
    private readonly BackgroundImage[] _buffers = new BackgroundImage[2];
    private int _next;
    private long _lastTime = -1;
    private bool _started;

    public VideoFrames(string path)
    {
        _path = path;
        MF.Check(MF.MFStartup(MF.Version, 0));
        _started = true;
        Open();
    }

    private void Open()
    {
        IntPtr attributes = IntPtr.Zero, type = IntPtr.Zero, current = IntPtr.Zero;
        try
        {
            // Video processing lets the reader convert any codec output to plain 32-bit RGB.
            MF.Check(MF.MFCreateAttributes(out attributes, 1));
            MF.Attributes(attributes).SetUINT32(MF.EnableVideoProcessing, 1);
            MF.Check(MF.MFCreateSourceReaderFromURL(_path, attributes, out _reader));
            _reader.SetStreamSelection(MF.AllStreams, 0);
            _reader.SetStreamSelection(MF.FirstVideoStream, 1);

            MF.Check(MF.MFCreateMediaType(out type));
            var t = MF.Attributes(type);
            t.SetGUID(MF.MajorType, MF.Video);
            t.SetGUID(MF.Subtype, MF.Rgb32);
            _reader.SetCurrentMediaType(MF.FirstVideoStream, IntPtr.Zero, type);

            _reader.GetCurrentMediaType(MF.FirstVideoStream, out current);
            var c = MF.Attributes(current);
            MF.Check(c.GetUINT64(MF.FrameSize, out ulong size));
            _width = (int)(size >> 32);
            _height = (int)(size & 0xFFFFFFFF);
            // RGB in Media Foundation is bottom-up unless the type says otherwise.
            _stride = c.GetUINT32(MF.DefaultStride, out uint stride) == 0 ? unchecked((int)stride) : -_width * 4;
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (type != IntPtr.Zero) Marshal.Release(type);
            if (current != IntPtr.Zero) Marshal.Release(current);
        }
    }

    public BackgroundImage Next(out TimeSpan wait)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _reader.ReadSample(MF.FirstVideoStream, 0, out _, out uint flags, out long time, out var sample);
            if (sample is null)
            {
                if ((flags & MF.EndOfStream) != 0)
                {
                    // Loop: start the file again.
                    Marshal.ReleaseComObject(_reader);
                    Open();
                }
                continue;
            }

            try
            {
                var image = Copy(sample);
                wait = _lastTime < 0 || time <= _lastTime
                    ? TimeSpan.FromMilliseconds(33)
                    : TimeSpan.FromTicks(Math.Min(time - _lastTime, TimeSpan.TicksPerSecond));
                _lastTime = time;
                return image;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        throw new InvalidDataException("No video frames");
    }

    private BackgroundImage Copy(IMFSample sample)
    {
        // Two buffers in turn, so the picture just handed out is not overwritten straight away.
        var image = _buffers[_next];
        if (image is null || image.Width != _width || image.Height != _height)
            image = _buffers[_next] = new BackgroundImage(new byte[_width * _height * 4], _width, _height);
        _next ^= 1;

        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            buffer.Lock(out IntPtr data, out _, out _);
            try
            {
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    int sourceRow = _stride >= 0 ? y : _height - 1 - y;
                    Marshal.Copy(data + sourceRow * Math.Abs(_stride), image.Pixels, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
        return image;
    }

    public void Dispose()
    {
        if (_reader is not null)
            Marshal.ReleaseComObject(_reader);
        if (_started)
            MF.MFShutdown();
        _started = false;
    }
}

/// <summary>
/// An animated GIF, decoded once into memory with Windows Imaging Component. GIF frames are often
/// small patches drawn over the previous picture, so each one is composed onto a full canvas following
/// the frame's position, transparency and disposal rules.
/// </summary>
public sealed class GifFrames : IFrameSource
{
    private const long MaxPixels = 120_000_000; // About 480 MB of frames at most.
    private readonly List<(BackgroundImage Image, TimeSpan Delay)> _frames = [];
    private int _index = -1;

    public GifFrames(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new System.Windows.Media.Imaging.GifBitmapDecoder(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("No frames");

        int width = Read<ushort>(decoder.Metadata, "/logscrdesc/Width") is ushort lw and > 0 ? lw : decoder.Frames[0].PixelWidth;
        int height = Read<ushort>(decoder.Metadata, "/logscrdesc/Height") is ushort lh and > 0 ? lh : decoder.Frames[0].PixelHeight;

        // Very large GIFs are stored smaller so they fit in memory.
        double scale = Math.Min(1.0, Math.Sqrt((double)MaxPixels / ((long)width * height * decoder.Frames.Count)));
        int storeWidth = Math.Max(1, (int)(width * scale)), storeHeight = Math.Max(1, (int)(height * scale));

        var canvas = new byte[width * height * 4];
        for (int i = 3; i < canvas.Length; i += 4)
            canvas[i] = 255; // Start from opaque black.

        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as System.Windows.Media.Imaging.BitmapMetadata;
            int left = Read<ushort>(meta, "/imgdesc/Left") ?? 0;
            int top = Read<ushort>(meta, "/imgdesc/Top") ?? 0;
            int delay = Read<ushort>(meta, "/grctlext/Delay") ?? 0;
            int disposal = Read<byte>(meta, "/grctlext/Disposal") ?? 0;
            byte[]? before = disposal == 3 ? (byte[])canvas.Clone() : null;

            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
            var patch = new byte[fw * fh * 4];
            bgra.CopyPixels(patch, fw * 4, 0);
            Blend(canvas, width, height, patch, fw, fh, left, top);

            BackgroundImage stored;
            if (storeWidth == width && storeHeight == height)
                stored = new BackgroundImage((byte[])canvas.Clone(), width, height);
            else
            {
                stored = new BackgroundImage(new byte[storeWidth * storeHeight * 4], storeWidth, storeHeight);
                ImageFit.Cover(new BackgroundImage(canvas, width, height), false, storeWidth, storeHeight, stored.Pixels);
            }
            // Browsers treat very short delays as 0.1 s; doing the same keeps GIFs at their usual speed.
            _frames.Add((stored, TimeSpan.FromMilliseconds((delay < 2 ? 10 : delay) * 10)));

            if (disposal == 2)
                Clear(canvas, width, height, left, top, fw, fh);
            else if (before is not null)
                canvas = before;
        }
    }

    private static T? Read<T>(System.Windows.Media.Imaging.BitmapMetadata? meta, string query) where T : struct
    {
        try
        {
            return meta?.ContainsQuery(query) == true && meta.GetQuery(query) is T value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Blend(byte[] canvas, int width, int height, byte[] patch, int fw, int fh, int left, int top)
    {
        for (int y = 0; y < fh; y++)
        {
            int cy = top + y;
            if (cy < 0 || cy >= height)
                continue;
            for (int x = 0; x < fw; x++)
            {
                int cx = left + x;
                int s = (y * fw + x) * 4;
                if (cx < 0 || cx >= width || patch[s + 3] < 128)
                    continue; // GIF transparency is all-or-nothing.
                int d = (cy * width + cx) * 4;
                canvas[d] = patch[s];
                canvas[d + 1] = patch[s + 1];
                canvas[d + 2] = patch[s + 2];
                canvas[d + 3] = 255;
            }
        }
    }

    private static void Clear(byte[] canvas, int width, int height, int left, int top, int fw, int fh)
    {
        for (int y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
            for (int x = Math.Max(0, left); x < Math.Min(width, left + fw); x++)
            {
                int d = (y * width + x) * 4;
                canvas[d] = canvas[d + 1] = canvas[d + 2] = 0;
                canvas[d + 3] = 255;
            }
    }

    public BackgroundImage Next(out TimeSpan wait)
    {
        wait = _index < 0 ? TimeSpan.Zero : _frames[_index].Delay;
        _index = (_index + 1) % _frames.Count;
        return _frames[_index].Image;
    }

    public void Dispose()
    {
    }
}

/// <summary>"Aurora": slowly flowing colour bands, drawn by code at a low resolution.</summary>
public sealed class AuroraFrames : IFrameSource
{
    private const int Width = 384, Height = 216;
    private readonly BackgroundImage[] _buffers =
        [new(new byte[Width * Height * 4], Width, Height), new(new byte[Width * Height * 4], Width, Height)];
    private int _next;
    private double _time;

    // Deep blue night sky, teal and violet light.
    private static readonly (float R, float G, float B)[] Palette =
        [(8, 14, 38), (16, 92, 120), (40, 190, 160), (110, 70, 190), (12, 20, 52)];

    public BackgroundImage Next(out TimeSpan wait)
    {
        wait = _time == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(33);
        var image = Render(_buffers[_next], _time);
        _next ^= 1;
        _time += 1 / 30.0;
        return image;
    }

    /// <summary>Draws the scene at <paramref name="t"/> seconds.</summary>
    public static BackgroundImage Render(BackgroundImage target, double t)
    {
        byte[] px = target.Pixels;
        Parallel.For(0, Height, y =>
        {
            double v = y / (double)Height;
            for (int x = 0; x < Width; x++)
            {
                double u = x / (double)Width;
                // Speeds are chosen so the light visibly drifts within a second or two without flickering.
                double wave = Math.Sin(u * 5.0 + t * 1.1) * 0.5
                              + Math.Sin(u * 11.0 - t * 0.8 + Math.Sin(v * 3.0 + t * 0.6) * 2.0) * 0.25
                              + Math.Sin((u + v) * 7.0 + t * 0.7) * 0.25;
                // Brightest band across the upper middle, swaying up and down, fading towards the bottom.
                double band = Math.Exp(-Math.Pow((v - 0.38 - wave * 0.22) * 4.2, 2));
                double hue = 0.5 + 0.5 * Math.Sin(u * 3.0 - t * 0.45 + wave);
                (float r, float g, float b) glow = Mix(Palette[2], Palette[3], (float)hue);
                (float r, float g, float b) sky = Mix(Palette[0], Palette[4], (float)v);
                (float r, float g, float b) c = Mix(sky, Mix(Palette[1], glow, (float)band), (float)(band * 0.9));
                int p = (y * Width + x) * 4;
                px[p] = (byte)c.b;
                px[p + 1] = (byte)c.g;
                px[p + 2] = (byte)c.r;
                px[p + 3] = 255;
            }
        });
        return target;
    }

    private static (float, float, float) Mix((float R, float G, float B) a, (float R, float G, float B) b, float k) =>
        (a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);

    public static BackgroundImage NewImage() => new(new byte[Width * Height * 4], Width, Height);

    public void Dispose()
    {
    }
}
