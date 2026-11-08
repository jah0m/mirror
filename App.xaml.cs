using System.Windows;

namespace Mirror;

public partial class App : Application
{
    /// <summary>
    /// Set by <c>--demo &lt;picture&gt;</c>, for README screenshots: the picture stands in for the camera,
    /// and settings are read but never saved. Optional <c>--lang zh|en|ja</c> and
    /// <c>--background &lt;key&gt;</c> override the saved choices for that run.
    /// </summary>
    public static BackgroundImage? DemoPicture { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(e.Args, name);
            return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        }

        if (Arg("--demo") is { } picture)
        {
            DemoPicture = Backgrounds.LoadFile(picture);
            AppSettings.DemoOverrides = (Arg("--lang"), Arg("--background"));
        }

        // The text resources must exist before the main window is built.
        Loc.Apply(AppSettings.Load().Language);
        base.OnStartup(e);
    }
}
