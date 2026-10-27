using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace RGBSync;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "RGBSync.SingleInstance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "RGBSync.ShowWindow");
        if (!isFirst)
        {
            // already running: just pop the existing window up
            showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
            }
        }) { IsBackground = true };
        listener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Something went wrong:\n\n" + args.Exception.Message, "RGB Sync",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;
        if (!(minimized || window.Settings.StartMinimized))
            window.Show();
    }

    /// <summary>Windows is shutting down / restarting / signing out: turn the case lights off first.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { (MainWindow as MainWindow)?.PcLightsOff(); } catch { }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
