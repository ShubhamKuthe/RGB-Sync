using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RGBSync.Services;

public static class SystemHelpers
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "RGBSync";

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(RunName, $"\"{ExePath}\" --minimized");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
        catch
        {
            // not fatal
        }
    }

    public static bool IsOpenRgbRunning() =>
        Process.GetProcessesByName("OpenRGB").Any();

    /// <summary>Look for OpenRGB.exe in the usual install places (winget, Program Files, Downloads...).</summary>
    public static string? FindOpenRgb()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(local, "Programs", "OpenRGB", "OpenRGB.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var root in new[]
                 {
                     Path.Combine(local, "Microsoft", "WinGet", "Packages"),
                     Path.Combine(home, "Downloads"),
                     Path.Combine(home, "Documents", "RGBSync", "tools"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var hit = Directory.EnumerateFiles(root, "OpenRGB.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
                }).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Start OpenRGB hidden with its SDK server on, if we know where it is and it's not already running.</summary>
    public static string? TryStartOpenRgb(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (IsOpenRgbRunning()) return null;
        if (!File.Exists(path)) return "OpenRGB.exe not found at the saved path.";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "--server --startminimized",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't start OpenRGB: " + ex.Message;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
