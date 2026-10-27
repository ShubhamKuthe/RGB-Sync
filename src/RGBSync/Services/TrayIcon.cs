using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace RGBSync.Services;

/// <summary>System-tray icon so the app keeps the lights running when the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? ToggleSyncRequested;
    public event Action? LightsOffRequested;

    public TrayIcon()
    {
        _icon = new WinForms.NotifyIcon
        {
            Text = "RGB Sync",
            Visible = true,
        };
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null) _icon.Icon = new System.Drawing.Icon(res.Stream);
        }
        catch
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open RGB Sync", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Sync PC + Room on/off", null, (_, _) => ToggleSyncRequested?.Invoke());
        menu.Items.Add("All lights off", null, (_, _) => LightsOffRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowTip(string title, string text)
    {
        try { _icon.ShowBalloonTip(2500, title, text, WinForms.ToolTipIcon.Info); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
