using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mirror;

/// <summary>One choice in the background picker.</summary>
public sealed record BackgroundChoice(string Key, string Name, bool IsCustom, bool IsAnimated);

/// <summary>Built-in and user-added backgrounds: still pictures, GIFs, videos and the drawn "aurora".</summary>
public static class Backgrounds
{
    public const string None = "none";
    public const string Blur = "blur";
    private const int MaxWidth = 1920;

    // Shipped inside the exe as bg.<id>.jpg; names come from Loc as "bg.<id>".
    private static readonly string[] Presets = ["office", "japan", "cafe", "city", "nature", "studio"];
    private const string Aurora = "preset:aurora";

    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv", ".webm"];
    private static readonly string[] PictureExtensions = [".jpg"];

    public static string CustomFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirror", "backgrounds");

    /// <summary>File types offered when adding a background, for the file dialog filter.</summary>
    public static string UploadPattern =>
        "*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.gif;" + string.Join(";", VideoExtensions.Select(e => "*" + e));

    public static List<BackgroundChoice> List()
    {
        var list = Presets.Select(id => new BackgroundChoice("preset:" + id, Loc.T("bg." + id), false, false)).ToList();
        list.Add(new BackgroundChoice(Aurora, Loc.T("bg.aurora"), false, true));
        if (Directory.Exists(CustomFolder))
        {
            int pictures = 0, animations = 0;
            foreach (var file in new DirectoryInfo(CustomFolder).EnumerateFiles().OrderBy(f => f.CreationTimeUtc))
            {
                string ext = file.Extension.ToLowerInvariant();
                if (PictureExtensions.Contains(ext))
                    list.Add(new BackgroundChoice("custom:" + file.Name, Loc.T("MyPicture", ++pictures), true, false));
                else if (ext == ".gif" || VideoExtensions.Contains(ext))
                    list.Add(new BackgroundChoice("custom:" + file.Name, Loc.T("MyAnimation", ++animations), true, true));
            }
        }
        return list;
    }

    public static bool IsImage(string key) => key.StartsWith("preset:") || key.StartsWith("custom:");

    public static string DisplayName(string key) =>
        key == Blur ? Loc.T("Blur") : List().FirstOrDefault(c => c.Key == key)?.Name ?? Loc.T("Background");

    private static string? CustomPath(string key)
    {
        if (!key.StartsWith("custom:"))
            return null;
        string path = Path.Combine(CustomFolder, Path.GetFileName(key[7..]));
        return File.Exists(path) ? path : null;
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens the background for <paramref name="key"/>, or returns null if it no longer exists.
    /// Animated ones start playing on their own thread; dispose them when no longer shown.
    /// </summary>
    public static IReplacementBackground? Open(string key)
    {
        if (key == Aurora)
            return new AnimatedBackground(() => new AuroraFrames());
        if (key.StartsWith("preset:"))
        {
            using var stream = typeof(Backgrounds).Assembly.GetManifestResourceStream($"bg.{key[7..]}.jpg");
            return stream is null ? null : new StillBackground(ToPixels(stream));
        }
        if (CustomPath(key) is not { } path)
            return null;
        if (IsVideo(path))
            return new AnimatedBackground(() => new VideoFrames(path));
        if (IsGif(path))
            return new AnimatedBackground(() => new GifFrames(path));
        using (var stream = File.OpenRead(path))
            return new StillBackground(ToPixels(stream));
    }

    public static BitmapSource? LoadThumbnail(string key)
    {
        if (key == Aurora)
            return ToBitmap(AuroraFrames.Render(AuroraFrames.NewImage(), 12));
        if (key.StartsWith("preset:"))
        {
            using var stream = typeof(Backgrounds).Assembly.GetManifestResourceStream($"bg.{key[7..]}.jpg");
            return stream is null ? null : Decode(stream, 192);
        }
        if (CustomPath(key) is not { } path)
            return null;
        if (IsVideo(path))
        {
            using var video = new VideoFrames(path);
            return Shrink(ToBitmap(video.Next(out _)), 192);
        }
        using (var stream = File.OpenRead(path))
            return Decode(stream, 192);
    }

    private static BitmapSource ToBitmap(BackgroundImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Shrink(BitmapSource image, int width)
    {
        if (image.PixelWidth <= width)
            return image;
        double scale = (double)width / image.PixelWidth;
        var small = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }

    /// <summary>Loads any picture file as BGRA pixels (at most 1920 wide).</summary>
    public static BackgroundImage LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return ToPixels(stream);
    }

    private static BackgroundImage ToPixels(Stream stream)
    {
        var bitmap = new FormatConvertedBitmap(Decode(stream, MaxWidth), PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new BackgroundImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static BitmapSource Decode(Stream stream, int maxWidth)
    {
        var probe = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = probe.PixelWidth;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        if (width > maxWidth)
            image.DecodePixelWidth = maxWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Copies a user's picture, GIF or video into the app's own folder, so it keeps working if the
    /// original moves. Pictures are re-saved as JPEG; GIFs and videos are copied as they are, after
    /// checking that they can be played.
    /// </summary>
    public static string AddCustom(string sourcePath)
    {
        Directory.CreateDirectory(CustomFolder);
        string stamp = $"custom_{DateTime.Now:yyyyMMdd_HHmmss_fff}";

        if (IsVideo(sourcePath) || IsGif(sourcePath))
        {
            using (IFrameSource probe = IsVideo(sourcePath) ? new VideoFrames(sourcePath) : new GifFrames(sourcePath))
                probe.Next(out _);
            string name = stamp + Path.GetExtension(sourcePath).ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(CustomFolder, name));
            return "custom:" + name;
        }

        BitmapSource image;
        using (var stream = File.OpenRead(sourcePath))
        {
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            image = Decode(buffer, MaxWidth);
        }
        string jpg = stamp + ".jpg";
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0)));
        using (var output = File.Create(Path.Combine(CustomFolder, jpg)))
            encoder.Save(output);
        return "custom:" + jpg;
    }

    public static void DeleteCustom(string key)
    {
        if (CustomPath(key) is { } path)
            File.Delete(path);
    }
}
