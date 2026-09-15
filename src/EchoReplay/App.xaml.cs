using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace EchoReplay;

public partial class App : Application
{
    private Mutex? mutex;
    private Mutex? installerGuard;
    private EventWaitHandle? activation;
    private DispatcherTimer? activationTimer;
    internal static string[] Arguments { get; private set; } = [];
    internal static string? ArgumentValue(string option)
    {
        int index = Array.IndexOf(Arguments, option);
        return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : null;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Arguments = e.Args;
        if (ArgumentValue("--data-dir") is string dataDirectory) SettingsStore.DataDirectory = Path.GetFullPath(dataDirectory);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsStore.DataDirectory.ToUpperInvariant())))[..16];
        mutex = new Mutex(true, @"Local\EchoReplay." + identity, out bool created);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\EchoReplay.Activate." + identity);
        if (!created) { activation.Set(); Shutdown(); return; }
        // Inno Setup checks this shared name before install/uninstall. Keeping a
        // handle alive protects every running profile from being replaced.
        installerGuard = new Mutex(false, @"Local\EchoReplay.Setup");
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("發生錯誤：" + args.Exception.Message, "EchoReplay", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var settings = SettingsStore.Load();
        if (ArgumentValue("--data-dir") is null) settings.RunAtLogin = LoginStartup.IsEnabled();
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
        if (Arguments.Contains("--background") || settings.StartHidden) window.Hide();
        activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) window.ShowAndActivate(); };
        activationTimer.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationTimer?.Stop();
        activation?.Dispose();
        installerGuard?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
