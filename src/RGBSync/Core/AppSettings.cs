using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RGBSync.Core.Devices;
using RGBSync.Core.Effects;

namespace RGBSync.Core;

public sealed class DeviceLook
{
    public EffectKind Effect { get; set; } = EffectKind.Static;
    public string Color { get; set; } = "#00C8FF";
    public double Speed { get; set; } = 1.0;
    public double Brightness { get; set; } = 1.0;
    public List<string> CustomColors { get; set; } = new();
    public bool Spin { get; set; }

    public LightState ToState()
    {
        var st = new LightState
        {
            Effect = Effect, Color = Rgb.FromHex(Color), Speed = Speed, Brightness = Brightness, Spin = Spin,
        };
        if (CustomColors.Count > 0) st.Custom = CustomColors.ConvertAll(Rgb.FromHex).ToArray();
        return st;
    }

    public static DeviceLook From(LightState s) => new()
    {
        Effect = s.Effect, Color = s.Color.ToHex(), Speed = s.Speed, Brightness = s.Brightness, Spin = s.Spin,
        CustomColors = new List<string>(Array.ConvertAll(s.Custom, c => c.ToHex())),
    };
}

public sealed class AppSettings
{
    public bool Sync { get; set; } = true;
    public DeviceLook Pc { get; set; } = new();
    public DeviceLook Room { get; set; } = new();

    public List<string> SavedColors { get; set; } = new()
    {
        "#FF0000", "#FF7A00", "#FFD400", "#B6FF00", "#00FF6A", "#00C8FF", "#2F5BFF", "#9D00FF", "#FF00C8", "#FFFFFF",
    };

    // OpenRGB
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
    public List<string> ExcludedControllers { get; set; } = new();
    /// <summary>LED counts for ARGB headers, keyed "controller|zone" - re-applied every time we connect.</summary>
    public Dictionary<string, int> ZoneSizes { get; set; } = new();
    /// <summary>Optional path to OpenRGB.exe - if set, we start it in the background automatically.</summary>
    public string OpenRgbPath { get; set; } = "";

    // Room light (Tuya / Calex)
    public bool RoomEnabled { get; set; } = true;
    public string RoomName { get; set; } = "Room Light";
    public string RoomIp { get; set; } = "";
    public string RoomDeviceId { get; set; } = "";
    public string RoomLocalKey { get; set; } = "";
    public string RoomMac { get; set; } = "";
    public string RoomVersion { get; set; } = "3.4";
    public TuyaDpsProfile RoomProfile { get; set; } = TuyaDpsProfile.Standard;
    public int RoomUpdatesPerSecond { get; set; } = 8;

    // Pump ring: own colour/effect, separate from the fans
    public bool RingSeparate { get; set; }
    public bool RingConfigured { get; set; }
    public DeviceLook RingLook { get; set; } = new() { Effect = EffectKind.Clock, Color = "#2BD9FF" };

    // Pump ring clock
    public int ClockStartLed { get; set; }
    public int ClockLedCount { get; set; } = 12;
    public int ClockTopOffset { get; set; }
    public bool ClockClockwise { get; set; } = true;
    public bool ClockShowSeconds { get; set; } = true;
    public string ClockHourColor { get; set; } = "#FF2828";
    public string ClockMinuteColor { get; set; } = "#28A0FF";
    public string ClockSecondColor { get; set; } = "#FFFFFF";

    /// <summary>LEDs around each fan (and the pump ring). Patterns repeat every this many LEDs.</summary>
    public int FanLeds { get; set; } = 12;
    /// <summary>How many paintable sections the room strip has.</summary>
    public int StripSegments { get; set; } = 20;

    // App
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Turn the PC (case) lights off when Windows shuts down, restarts or goes to sleep.</summary>
    public bool PcLightsOffWhenPcOff { get; set; } = true;

    public ClockRing ToRing() => new()
    {
        StartLed = ClockStartLed, LedCount = ClockLedCount, TopOffset = ClockTopOffset, Clockwise = ClockClockwise,
        ShowSeconds = ClockShowSeconds, HourColor = Rgb.FromHex(ClockHourColor),
        MinuteColor = Rgb.FromHex(ClockMinuteColor), SecondColor = Rgb.FromHex(ClockSecondColor),
    };

    // ---- persistence ----

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RGBSync");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // corrupt file - start fresh but keep a copy
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // settings are best-effort
        }
    }
}
