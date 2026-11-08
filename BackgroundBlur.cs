using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mirror;

/// <summary>A replacement background as BGRA pixels.</summary>
public sealed record BackgroundImage(byte[] Pixels, int Width, int Height);

/// <summary>
/// Blurs or replaces everything behind the person in a BGRA frame. The person is found with Robust Video Matting,
/// which produces a full-resolution alpha matte (down to strands of hair) and carries state from frame
/// to frame, so the edge stays steady in live video.
/// </summary>
public sealed class BackgroundBlur
{
    private const int LowWidth = 320;
    private static readonly string[] StateNames = ["r1", "r2", "r3", "r4"];
    private static readonly string[] OutputNames = ["pha", "r1o", "r2o", "r3o", "r4o"];

    private readonly InferenceSession _session;
    private readonly Dictionary<string, DenseTensor<float>> _state = [];
    private DenseTensor<float> _source = new([1, 3, 1, 1]);
    private int _modelWidth, _modelHeight;
    private float _downsampleRatio;

    // Full-resolution alpha of the person, 0–255.
    private byte[] _alpha = [];

    // Low-resolution background. Colors are weighted by "how much background this pixel is",
    // so the person's colors do not bleed into the blur as a halo around them.
    private int _lowHeight;
    private float[] _b = [], _g = [], _r = [], _weight = [];
    private float[][] _temp = [];
    private byte[] _lowBgr = [];
    private int[] _lowX0 = [];
    private byte[] _lowXFrac = [];

    private int _sourceWidth, _sourceHeight;

    /// <summary>Blur radius in low-resolution pixels.</summary>
    public int Strength { get; set; } = 6;

    /// <summary>When set, this image is shown behind the person instead of the blurred room.</summary>
    public BackgroundImage? Replacement { get; set; }

    /// <summary>
    /// Flips the replacement horizontally. Set while the preview is mirrored, so the picture reads the
    /// right way round on screen.
    /// </summary>
    public bool FlipReplacement { get; set; }

    private (BackgroundImage Source, bool Flip, int Width, int Height, byte[] Pixels)? _fitted;

    /// <summary>True when the model runs on the graphics card.</summary>
    public bool UsesGpu { get; }

    public BackgroundBlur()
    {
        using var stream = typeof(BackgroundBlur).Assembly.GetManifestResourceStream("rvm_mobilenetv3.onnx")
                           ?? throw new FileNotFoundException("rvm_mobilenetv3.onnx");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] model = memory.ToArray();

        try
        {
            using var gpu = new SessionOptions { EnableMemoryPattern = false };
            gpu.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(model, gpu);
            UsesGpu = true;
        }
        catch (Exception e)
        {
            // No usable DirectX 12 graphics card. The CPU works too, at a lower internal resolution.
            Debug.WriteLine(e);
            using var cpu = new SessionOptions { IntraOpNumThreads = 4 };
            cpu.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            _session = new InferenceSession(model, cpu);
        }
    }

    public void Apply(byte[] source, int width, int height, byte[] destination)
    {
        if (width != _sourceWidth || height != _sourceHeight)
            Prepare(width, height);

        UpdateAlpha(source, width, height);
        var replacement = Replacement;
        if (replacement is null)
        {
            BuildBackground(source, width, height);
            Composite(source, width, height, destination);
        }
        else
        {
            CompositeOver(source, Fit(replacement, FlipReplacement, width, height), width, height, destination);
        }
    }

    /// <summary>Scales the replacement to cover the frame, centred and cropped. Cached per size.</summary>
    private byte[] Fit(BackgroundImage image, bool flip, int width, int height)
    {
        if (_fitted is { } f && f.Source == image && f.Flip == flip && f.Width == width && f.Height == height)
            return f.Pixels;

        var pixels = new byte[width * height * 4];
        double scale = Math.Max((double)width / image.Width, (double)height / image.Height);
        double offsetX = (image.Width * scale - width) / 2, offsetY = (image.Height * scale - height) / 2;
        int iw = image.Width, ih = image.Height;
        byte[] src = image.Pixels;
        Parallel.For(0, height, y =>
        {
            double sy = Math.Clamp((y + 0.5 + offsetY) / scale - 0.5, 0, ih - 1.001);
            int y0 = (int)sy;
            double wy = sy - y0;
            int y1 = Math.Min(y0 + 1, ih - 1);
            for (int x = 0; x < width; x++)
            {
                int tx = flip ? width - 1 - x : x;
                double sx = Math.Clamp((tx + 0.5 + offsetX) / scale - 0.5, 0, iw - 1.001);
                int x0 = (int)sx;
                double wx = sx - x0;
                int x1 = Math.Min(x0 + 1, iw - 1);
                int p = (y * width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = src[(y0 * iw + x0) * 4 + c] * (1 - wx) + src[(y0 * iw + x1) * 4 + c] * wx;
                    double bottom = src[(y1 * iw + x0) * 4 + c] * (1 - wx) + src[(y1 * iw + x1) * 4 + c] * wx;
                    pixels[p + c] = (byte)(top * (1 - wy) + bottom * wy + 0.5);
                }
                pixels[p + 3] = 255;
            }
        });

        _fitted = (image, flip, width, height, pixels);
        return pixels;
    }

    private void CompositeOver(byte[] source, byte[] background, int width, int height, byte[] destination)
    {
        Parallel.For(0, height, y =>
        {
            int p = y * width * 4, i = y * width;
            for (int x = 0; x < width; x++, p += 4, i++)
            {
                int mask = _alpha[i], inverse = 255 - mask;
                destination[p] = (byte)((source[p] * mask + background[p] * inverse) / 255);
                destination[p + 1] = (byte)((source[p + 1] * mask + background[p + 1] * inverse) / 255);
                destination[p + 2] = (byte)((source[p + 2] * mask + background[p + 2] * inverse) / 255);
                destination[p + 3] = 255;
            }
        });
    }

    private void Prepare(int width, int height)
    {
        _sourceWidth = width;
        _sourceHeight = height;

        // The GPU handles the full frame; on the CPU a half-size frame keeps the preview fluid.
        int scale = UsesGpu ? 1 : 2;
        _modelWidth = width / scale;
        _modelHeight = height / scale;
        _source = new DenseTensor<float>([1, 3, _modelHeight, _modelWidth]);
        // RVM's own guide: the coarse pass should see roughly 512 px along the long side.
        _downsampleRatio = Math.Min(1f, 480f / Math.Max(_modelWidth, _modelHeight));
        _state.Clear();
        foreach (var name in StateNames)
            _state[name] = new DenseTensor<float>(new float[1], [1, 1, 1, 1]);

        _alpha = new byte[width * height];

        _lowHeight = Math.Max(2, LowWidth * height / width);
        int lowPixels = LowWidth * _lowHeight;
        _b = new float[lowPixels];
        _g = new float[lowPixels];
        _r = new float[lowPixels];
        _weight = new float[lowPixels];
        _temp = [new float[lowPixels], new float[lowPixels], new float[lowPixels], new float[lowPixels]];
        _lowBgr = new byte[lowPixels * 3];

        _lowX0 = new int[width];
        _lowXFrac = new byte[width];
        for (int x = 0; x < width; x++)
        {
            float position = Math.Max(0f, (x + 0.5f) * LowWidth / width - 0.5f);
            int i = Math.Min((int)position, LowWidth - 2);
            _lowX0[x] = i;
            _lowXFrac[x] = (byte)(Math.Min(1f, position - i) * 255);
        }
    }

    private void UpdateAlpha(byte[] source, int width, int height)
    {
        int mw = _modelWidth, mh = _modelHeight, plane = mw * mh;
        float[] input = _source.Buffer.Span.Length == 3 * plane ? GetArray(_source) : throw new InvalidOperationException();
        Parallel.For(0, mh, y =>
        {
            int row = (y * height / mh) * width;
            for (int x = 0; x < mw; x++)
            {
                int p = (row + x * width / mw) * 4, i = y * mw + x;
                input[i] = source[p + 2] / 255f;
                input[plane + i] = source[p + 1] / 255f;
                input[2 * plane + i] = source[p] / 255f;
            }
        });

        var feeds = new List<NamedOnnxValue>(6)
        {
            NamedOnnxValue.CreateFromTensor("src", _source),
            NamedOnnxValue.CreateFromTensor("downsample_ratio", new DenseTensor<float>(new[] { _downsampleRatio }, [1])),
        };
        foreach (var name in StateNames)
            feeds.Add(NamedOnnxValue.CreateFromTensor(name + "i", _state[name]));

        // Only the matte and the state are fetched; the model's full-size foreground output would
        // be a 25 MB copy back from the graphics card every frame for nothing.
        using var results = _session.Run(feeds, OutputNames);
        foreach (var result in results)
        {
            if (result.Name == "pha")
            {
                var pha = result.AsTensor<float>();
                CopyAlpha(pha is DenseTensor<float> dense ? dense.Buffer : pha.ToArray(), width, height);
            }
            else if (result.Name.Length == 3 && result.Name[0] == 'r' && result.Name[2] == 'o')
            {
                // The recurrent state feeds the next frame.
                var t = result.AsTensor<float>();
                _state[result.Name[..2]] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
            }
        }
    }

    private static float[] GetArray(DenseTensor<float> tensor) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<float>(tensor.Buffer, out var segment)
            ? segment.Array!
            : throw new InvalidOperationException();

    private void CopyAlpha(Memory<float> pha, int width, int height)
    {
        int mw = _modelWidth, mh = _modelHeight;
        if (mw == width && mh == height)
        {
            Parallel.For(0, height, y =>
            {
                var row = pha.Span.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                    _alpha[y * width + x] = (byte)(Math.Clamp(row[x], 0f, 1f) * 255);
            });
            return;
        }

        // Half-size matte on the CPU path: bilinear upscale to the frame.
        float[] matte = pha.ToArray();
        Parallel.For(0, height, y =>
        {
            float fy = Math.Max(0f, (y + 0.5f) * mh / height - 0.5f);
            int y0 = Math.Min((int)fy, mh - 2);
            float wy = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Max(0f, (x + 0.5f) * mw / width - 0.5f);
                int x0 = Math.Min((int)fx, mw - 2);
                float wx = fx - x0;
                int i = y0 * mw + x0;
                float v = (matte[i] * (1 - wx) + matte[i + 1] * wx) * (1 - wy)
                          + (matte[i + mw] * (1 - wx) + matte[i + mw + 1] * wx) * wy;
                _alpha[y * width + x] = (byte)(Math.Clamp(v, 0f, 1f) * 255);
            }
        });
    }

    private void BuildBackground(byte[] source, int width, int height)
    {
        int lowHeight = _lowHeight;
        Parallel.For(0, lowHeight, y =>
        {
            int y0 = y * height / lowHeight;
            int y1 = Math.Max(y0 + 1, (y + 1) * height / lowHeight);
            int stepY = Math.Max(1, (y1 - y0) / 2);
            for (int x = 0; x < LowWidth; x++)
            {
                int x0 = x * width / LowWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * width / LowWidth);
                int stepX = Math.Max(1, (x1 - x0) / 2);
                int b = 0, g = 0, r = 0, a = 0, count = 0;
                for (int sy = y0; sy < y1; sy += stepY)
                {
                    for (int sx = x0; sx < x1; sx += stepX)
                    {
                        int i = sy * width + sx, p = i * 4;
                        b += source[p];
                        g += source[p + 1];
                        r += source[p + 2];
                        a += _alpha[i];
                        count++;
                    }
                }

                float weight = 1f - a / (255f * count);
                int li = y * LowWidth + x;
                _b[li] = b / (float)count * weight;
                _g[li] = g / (float)count * weight;
                _r[li] = r / (float)count * weight;
                _weight[li] = weight;

                // Used when no background is close enough to blur from.
                _lowBgr[li * 3] = (byte)(b / count);
                _lowBgr[li * 3 + 1] = (byte)(g / count);
                _lowBgr[li * 3 + 2] = (byte)(r / count);
            }
        });

        int radius = Strength;
        (float[] Plane, float[] Temp)[] planes = [(_b, _temp[0]), (_g, _temp[1]), (_r, _temp[2]), (_weight, _temp[3])];
        Parallel.ForEach(planes, item =>
        {
            // Two box passes look close to a Gaussian.
            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlur(item.Plane, item.Temp, LowWidth, lowHeight, radius, horizontal: true);
                BoxBlur(item.Temp, item.Plane, LowWidth, lowHeight, radius, horizontal: false);
            }
        });

        for (int i = 0; i < _weight.Length; i++)
        {
            float w = _weight[i];
            if (w < 0.02f)
                continue;
            _lowBgr[i * 3] = (byte)Math.Min(255f, _b[i] / w);
            _lowBgr[i * 3 + 1] = (byte)Math.Min(255f, _g[i] / w);
            _lowBgr[i * 3 + 2] = (byte)Math.Min(255f, _r[i] / w);
        }
    }

    private static void BoxBlur(float[] from, float[] to, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        int step = horizontal ? 1 : width;
        int lineStep = horizontal ? width : 1;
        float scale = 1f / (2 * radius + 1);

        for (int line = 0; line < lines; line++)
        {
            int origin = line * lineStep;
            float sum = from[origin] * (radius + 1);
            for (int i = 1; i <= radius; i++)
                sum += from[origin + Math.Min(i, length - 1) * step];

            for (int i = 0; i < length; i++)
            {
                to[origin + i * step] = sum * scale;
                sum += from[origin + Math.Min(i + radius + 1, length - 1) * step]
                       - from[origin + Math.Max(i - radius, 0) * step];
            }
        }
    }

    private void Composite(byte[] source, int width, int height, byte[] destination)
    {
        int lowHeight = _lowHeight;
        Parallel.For(0, height, y =>
        {
            float lowPosition = Math.Max(0f, (y + 0.5f) * lowHeight / height - 0.5f);
            int lowY = Math.Min((int)lowPosition, lowHeight - 2);
            int lowFy = (int)(Math.Min(1f, lowPosition - lowY) * 255);
            int lowRow0 = lowY * LowWidth * 3, lowRow1 = lowRow0 + LowWidth * 3;

            int p = y * width * 4, i = y * width;
            for (int x = 0; x < width; x++, p += 4, i++)
            {
                int mask = _alpha[i];
                if (mask >= 255)
                {
                    destination[p] = source[p];
                    destination[p + 1] = source[p + 1];
                    destination[p + 2] = source[p + 2];
                    destination[p + 3] = 255;
                    continue;
                }

                int lx = _lowX0[x] * 3, lf = _lowXFrac[x], inverse = 255 - mask;
                for (int c = 0; c < 3; c++)
                {
                    int a = _lowBgr[lowRow0 + lx + c] * (255 - lf) + _lowBgr[lowRow0 + lx + 3 + c] * lf;
                    int b = _lowBgr[lowRow1 + lx + c] * (255 - lf) + _lowBgr[lowRow1 + lx + 3 + c] * lf;
                    int background = (a * (255 - lowFy) + b * lowFy) / (255 * 255);
                    destination[p + c] = (byte)((source[p + c] * mask + background * inverse) / 255);
                }
                destination[p + 3] = 255;
            }
        });
    }
}
