using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetworkAtc.App.Services;
using NetworkAtc.App.Views;
using NetworkAtc.Core.Customization;

namespace NetworkAtc.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        var profilePath = Path.Combine(Profile.DefaultDirectory, "profiles", "default.json");
        var profile = Profile.Load(profilePath);
        ThemeApplier.Apply(profile.Theme);

        // A sector passed on the command line (e.g. a double-clicked .natc) skips the selection window.
        string? sectorPath = e.Args.FirstOrDefault(File.Exists);
        if (sectorPath == null)
        {
            if (profile.ShowSectorSelection || !File.Exists(profile.SectorFile))
            {
                var select = new SectorSelectWindow(profile, null);
                bool chosen = select.ShowDialog() == true;
                try { profile.Save(profilePath); }
                catch (IOException) { }
                if (!chosen)
                {
                    Shutdown();
                    return;
                }
                sectorPath = select.SelectedPath;
            }
            else
            {
                sectorPath = profile.SectorFile;
            }
        }

        var main = new MainWindow(profile, profilePath, sectorPath);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Network-ATC", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
