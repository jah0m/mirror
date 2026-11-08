using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mirror;

/// <summary>One choice in the background picker.</summary>
public sealed record BackgroundChoice(string Key, string Name, bool IsCustom);

/// <summary>Built-in and user-added background pictures.</summary>
public static class Backgrounds
{
    public const string None = "none";
    public const string Blur = "blur";
    private const int MaxWidth = 1920;

    // Shipped inside the exe as bg.<id>.jpg; names come from Loc as "bg.<id>".
    private static readonly string[] Presets = ["office", "japan", "cafe", "city", "nature", "studio"];

    public static string CustomFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirror", "backgrounds");

    public static List<BackgroundChoice> List()
    {
        var list = Presets.Select(id => new BackgroundChoice("preset:" + id, Loc.T("bg." + id), false)).ToList();
        if (Directory.Exists(CustomFolder))
        {
            list.AddRange(new DirectoryInfo(CustomFolder).EnumerateFiles("*.jpg")
                .OrderBy(f => f.CreationTimeUtc)
                .Select((f, i) => new BackgroundChoice("custom:" + f.Name, Loc.T("MyPicture", i + 1), true)));
        }
        return list;
    }

    public static bool IsImage(string key) => key.StartsWith("preset:") || key.StartsWith("custom:");

    public static string DisplayName(string key) =>
        key == Blur ? Loc.T("Blur") : List().FirstOrDefault(c => c.Key == key)?.Name ?? Loc.T("Background");

    /// <summary>Opens the picture for <paramref name="key"/>, or returns null if it no longer exists.</summary>
    private static Stream? Open(string key)
    {
        if (key.StartsWith("preset:"))
            return typeof(Backgrounds).Assembly.GetManifestResourceStream($"bg.{key[7..]}.jpg");
        if (key.StartsWith("custom:"))
        {
            string path = Path.Combine(CustomFolder, Path.GetFileName(key[7..]));
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
        return null;
    }

    public static BitmapSource? LoadThumbnail(string key)
    {
        using var stream = Open(key);
        return stream is null ? null : Decode(stream, 192);
    }

    public static BackgroundImage? LoadPixels(string key)
    {
        using var stream = Open(key);
        return stream is null ? null : ToPixels(stream);
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

    /// <summary>Copies a user's picture into the app's own folder, so it keeps working if the original moves.</summary>
    public static string AddCustom(string sourcePath)
    {
        BitmapSource image;
        using (var stream = File.OpenRead(sourcePath))
        {
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            image = Decode(buffer, MaxWidth);
        }

        Directory.CreateDirectory(CustomFolder);
        string name = $"custom_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0)));
        using (var output = File.Create(Path.Combine(CustomFolder, name)))
            encoder.Save(output);
        return "custom:" + name;
    }

    public static void DeleteCustom(string key)
    {
        if (!key.StartsWith("custom:"))
            return;
        string path = Path.Combine(CustomFolder, Path.GetFileName(key[7..]));
        if (File.Exists(path))
            File.Delete(path);
    }
}
