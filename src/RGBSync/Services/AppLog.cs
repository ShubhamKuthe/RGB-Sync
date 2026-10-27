using System;
using System.IO;

namespace RGBSync.Services;

/// <summary>
/// Small diagnostic logs (pc.log, room.log, strip-status.log, scan.log) kept in %AppData%\RGBSync\logs,
/// each capped at ~1 MB so they never grow forever.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RGBSync", "logs");

    public static void Append(string file, string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, file);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Write(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, file), text);
        }
        catch { }
    }
}
