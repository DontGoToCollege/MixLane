using System.Windows;
using MixLane.Core;

namespace MixLane;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = AppSettings.Load();
        Terminology.Settings = Settings;
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Something went wrong:\n\n" + args.Exception.Message, "MixLane", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        try { NAudio.MediaFoundation.MediaFoundationApi.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
