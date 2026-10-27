using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RGBSync.Core;
using RGBSync.Core.Devices;
using RGBSync.Core.Effects;
using RGBSync.Services;

namespace RGBSync;

public partial class MainWindow : Window
{
    private enum Page { Pc, Room, Ring, Settings }

    public AppSettings Settings { get; }

    private readonly PcDevice _pc = new();
    private readonly RoomLightDevice _room = new();
    private readonly LightEngine _engine;
    private readonly AudioMeter _audio = new();
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _slowTimer;

    private Page _page = Page.Pc;
    private bool _loading = true;
    private bool _dirty;
    private bool _exiting;
    private bool _trayTipShown;
    private volatile bool _refinding;
    private DateTime _lastRefind = DateTime.MinValue;
    private DispatcherTimer? _testTimer;

    private static readonly EffectKind[] PcEffects =
    {
        EffectKind.Static, EffectKind.Custom, EffectKind.Comet, EffectKind.RainbowComet, EffectKind.RainbowWave,
        EffectKind.Breathing, EffectKind.ColorCycle, EffectKind.MusicPulse, EffectKind.MusicRainbow,
        EffectKind.Clock, EffectKind.Off,
    };

    private static readonly EffectKind[] RoomEffects =
    {
        EffectKind.Static, EffectKind.Custom, EffectKind.Comet, EffectKind.RainbowComet, EffectKind.RainbowWave,
        EffectKind.Breathing, EffectKind.ColorCycle, EffectKind.MusicPulse, EffectKind.MusicRainbow, EffectKind.Off,
    };

    public MainWindow()
    {
        InitializeComponent();
        Settings = AppSettings.Load();

        _engine = new LightEngine(_pc, _room) { Audio = () => _audio.Current };
        ApplySettingsToDevices();
        _engine.PcState = Settings.Pc.ToState();
        _engine.RoomState = Settings.Room.ToState();
        _engine.Sync = Settings.Sync;
        _engine.Ring = Settings.ToRing();
        _engine.RoomEnabled = Settings.RoomEnabled;
        _engine.RingState = Settings.RingLook.ToState();
        _engine.RingSeparate = Settings.RingSeparate;

        _pc.StatusChanged += () => Dispatcher.BeginInvoke(RefreshStatus);
        _room.StatusChanged += () => Dispatcher.BeginInvoke(RefreshStatus);
        _room.RawStatus += json =>
        {
            try
            {
                AppLog.Append("strip-status.log", json);
            }
            catch { }
        };
        _room.RawStatus += json => Dispatcher.BeginInvoke(() =>
        {
            RoomRawStatus.Visibility = Visibility.Visible;
            RoomRawStatus.Text = json;
        });

        Wheel.ColorPicked += Wheel_ColorPicked;
        Fan.SegmentPainted += Fan_SegmentPainted;
        Strip.SegmentPainted += Fan_SegmentPainted;
        if (Settings.StripSegments == 10) Settings.StripSegments = RoomLightDevice.StripSections; // the strip really has 20 sections
        Strip.Segments = Math.Max(1, Settings.StripSegments);
        _engine.FanLeds = Math.Max(1, Settings.FanLeds);
        foreach (var st in new[] { _engine.PcState, _engine.RingState })
            st.Custom = Resample(st.Custom, _engine.FanLeds);
        _engine.RoomState.Custom = Resample(_engine.RoomState.Custom, Math.Max(1, Settings.StripSegments));
        RingCard.Visibility = Settings.RingSeparate ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => UseDarkTitleBar();

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.ToggleSyncRequested += () => { SyncToggle.IsChecked = !(SyncToggle.IsChecked == true); };
        _tray.LightsOffRequested += AllLightsOff;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
        _frameTimer.Tick += (_, _) => UpdatePreview();
        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _slowTimer.Tick += (_, _) => SlowTick();

        LoadSettingsIntoForm();
        SyncToggle.IsChecked = Settings.Sync;
        RebuildSwatches();
        ShowPage(Page.Pc);
        RefreshStatus();
        _loading = false;

        _engine.Start();
        _frameTimer.Start();
        _slowTimer.Start();
        _ = StartupConnectAsync();

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // ------------------------------------------------------------------ PC off / sleep

    /// <summary>
    /// The motherboard keeps its RGB headers powered after shutdown and just holds the last colour,
    /// so right before Windows shuts down (or sleeps) we paint the case lights black.
    /// </summary>
    public void PcLightsOff()
    {
        if (!Settings.PcLightsOffWhenPcOff) return;
        try
        {
            _engine.Stop();                                   // stop the effect loop so nothing paints over it
            var black = new Rgb[Math.Max(1, _pc.LedCount)];
            for (int i = 0; i < 3; i++)
            {
                _pc.Send(black);
                System.Threading.Thread.Sleep(60);
            }
        }
        catch { }

        // if the shutdown gets cancelled (an app blocks it), bring the lights back after a bit
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        back.Tick += (_, _) => { back.Stop(); _engine.Start(); };
        back.Start();
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            Dispatcher.Invoke(PcLightsOff);
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Dispatcher.BeginInvoke(() => _engine.Start());    // lights come back; OpenRGB reconnects by itself
    }

    // ------------------------------------------------------------------ startup / devices

    private void ApplySettingsToDevices()
    {
        _pc.Host = Settings.OpenRgbHost;
        _pc.Port = Settings.OpenRgbPort;
        _pc.ExcludedControllers.Clear();
        foreach (var n in Settings.ExcludedControllers) _pc.ExcludedControllers.Add(n);

        _room.DisplayName = string.IsNullOrWhiteSpace(Settings.RoomName) ? "Room Light" : Settings.RoomName;
        _room.Ip = Settings.RoomIp;
        _room.DeviceId = Settings.RoomDeviceId;
        _room.LocalKey = Settings.RoomLocalKey;
        _room.Version = Settings.RoomVersion;
        _room.Profile = Settings.RoomProfile;
        _room.UpdatesPerSecond = Settings.RoomUpdatesPerSecond;
    }

    private void AutoFindOpenRgb()
    {
        if (!string.IsNullOrWhiteSpace(Settings.OpenRgbPath) && System.IO.File.Exists(Settings.OpenRgbPath)) return;
        var found = SystemHelpers.FindOpenRgb();
        if (found == null) return;
        Settings.OpenRgbPath = found;
        OrgbPath.Text = found;
        SaveNow();
    }

    private async Task ApplySavedZoneSizesAsync()
    {
        bool changed = false;
        foreach (var c in _pc.AllControllers.ToList())
            foreach (var z in c.Zones.Where(z => z.Resizable))
                if (Settings.ZoneSizes.TryGetValue(c.Name + "|" + z.Name, out var n) && n != z.LedCount)
                {
                    await _pc.ResizeZoneAsync(c.Index, z.Index, n);
                    changed = true;
                    break;
                }
        if (changed) await Dispatcher.InvokeAsync(RebuildOrgbDeviceList);
    }

    private async Task StartupConnectAsync()
    {
        AutoFindOpenRgb();
        var err = SystemHelpers.TryStartOpenRgb(Settings.OpenRgbPath);
        if (err != null) OrgbStatus.Text = err;
        else if (!string.IsNullOrWhiteSpace(Settings.OpenRgbPath)) await Task.Delay(2500); // give OpenRGB time to scan

        await _pc.ConnectAsync();
        // fan LED counts are applied by SlowTick as soon as OpenRGB is connected (it can be slow right after boot)
        if (Settings.RoomEnabled && _room.IsConfigured)
        {
            await _room.ConnectAsync();
            if (_room.Status != Core.Devices.DeviceStatus.Connected && !string.IsNullOrWhiteSpace(Settings.RoomMac))
            {
                // the router may have given the strip a new address - find it again by its MAC
                var (ip, _, _) = await TuyaDiscovery.FindByMacAsync(Settings.RoomMac);
                if (ip != null && ip != Settings.RoomIp)
                {
                    Settings.RoomIp = ip;
                    _room.Ip = ip;
                    await Dispatcher.InvokeAsync(() => { RoomIpBox.Text = ip; SaveNow(); });
                    await _room.ConnectAsync();
                }
            }
        }
        await Dispatcher.InvokeAsync(() => { RebuildOrgbDeviceList(); RefreshStatus(); });
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _engine == null) return;
        if (sender == PcCard) ShowPage(Page.Pc);
        else if (sender == RoomCard) ShowPage(Page.Room);
        else if (sender == RingCard) ShowPage(Page.Ring);
        else ShowPage(Page.Settings);
    }

    private void ShowPage(Page p)
    {
        _page = p;
        bool settings = p == Page.Settings;
        DevicePage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (settings)
        {
            AnimateIn(SettingsPage);
            RebuildOrgbDeviceList();
            return;
        }
        if (_engine.FinderLed != null) { FinderCheck.IsChecked = false; }
        if (IdentifyCheck.IsChecked == true) IdentifyCheck.IsChecked = false;
        if (BlockCheck.IsChecked == true) BlockCheck.IsChecked = false;

        DeviceTitle.Text = p == Page.Pc ? "My PC" : p == Page.Ring ? "Pump Ring" : _room.DisplayName;
        ConnectBtn.Visibility = p == Page.Ring ? Visibility.Collapsed : Visibility.Visible;
        BuildEffectPills();
        LoadStateIntoControls();
        UpdateSyncBanner();
        bool room = p == Page.Room;
        Fan.Visibility = room ? Visibility.Collapsed : Visibility.Visible;
        RoomGlow.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
        AnimateIn(DevicePage);
        RefreshStatus();
    }

    private LightState CurrentState => _page switch
    {
        Page.Room => _engine.RoomState,
        Page.Ring => _engine.RingState,
        _ => _engine.PcState,
    };

    private bool RingIsSetUp => Settings.ClockLedCount > 0 && Settings.RingConfigured;

    private void UpdateSyncBanner()
    {
        bool locked = (_page == Page.Room && _engine.Sync) || (_page == Page.Ring && !_engine.RingSeparate);
        if (_page == Page.Ring)
        {
            BannerText.Text = !RingIsSetUp
                ? "Ring not set up"
                : "Following My PC";
            BannerButton.Content = !RingIsSetUp ? "Set up" : "Control separately";
        }
        else
        {
            BannerText.Text = "Following My PC";
            BannerButton.Content = "Unsync";
        }
        SyncBanner.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        DeviceControls.IsEnabled = !locked;
        DeviceControls.Opacity = locked ? 0.45 : 1;
    }

    // ------------------------------------------------------------------ effects + colour controls

    private void BuildEffectPills()
    {
        EffectsPanel.Children.Clear();
        var list = _page == Page.Room ? RoomEffects : PcEffects;
        if (_page == Page.Pc && _engine.RingSeparate) list = PcEffects.Where(k => k != EffectKind.Clock).ToArray();
        foreach (var k in list)
        {
            var rb = new RadioButton
            {
                Content = EffectInfo.DisplayName(k),
                Tag = k,
                GroupName = "fx",
                Style = (Style)FindResource("EffectPill"),
            };
            rb.Checked += EffectPill_Checked;
            EffectsPanel.Children.Add(rb);
        }
    }

    private void LoadStateIntoControls()
    {
        var wasLoading = _loading;
        _loading = true;
        var s = CurrentState;
        foreach (RadioButton rb in EffectsPanel.Children)
            rb.IsChecked = (EffectKind)rb.Tag == s.Effect;
        Wheel.SetColor(s.Color);
        BrightnessSlider.Value = Math.Round(s.Brightness * 100);
        BrightnessText.Text = $"{BrightnessSlider.Value:0}%";
        SpeedSlider.Value = s.Speed;
        SpinToggle.IsChecked = s.Spin;
        ShowColor(s.Color);
        UpdateEffectExtras();
        _loading = wasLoading;
    }

    private void UpdateEffectExtras()
    {
        var k = CurrentState.Effect;
        EffectDesc.Text = EffectInfo.Description(k);
        SpeedRow.Visibility = EffectInfo.UsesSpeed(k) ? Visibility.Visible : Visibility.Collapsed;
        MusicRow.Visibility = k is EffectKind.MusicPulse or EffectKind.MusicRainbow ? Visibility.Visible : Visibility.Collapsed;
        ClockRow.Visibility = k == EffectKind.Clock ? Visibility.Visible : Visibility.Collapsed;
        PaintTools.Visibility = EffectInfo.UsesPalette(k) ? Visibility.Visible : Visibility.Collapsed;
        ColourTitle.Text = EffectInfo.UsesPalette(k) ? "PAINT COLOUR" : "COLOUR";
        SpeedLabel.Text = EffectInfo.IsMusic(k) ? "Sensitivity" : "Speed";
        AnimateIn(PaintTools.Visibility == Visibility.Visible ? PaintTools : null);
    }

    private void EffectPill_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: EffectKind k })
        {
            CurrentState.Effect = k;
            UpdateEffectExtras();
            MarkDirty();
        }
    }

    private void Wheel_ColorPicked(double hue, double sat)
    {
        var c = Rgb.FromHsv(hue, sat, 1);
        SetCurrentColor(c, updateWheel: false);
    }

    private void SetCurrentColor(Rgb c, bool updateWheel)
    {
        CurrentState.Color = c;
        if (updateWheel) Wheel.SetColor(c);
        ShowColor(c);
        // picking a colour while on a colour-less effect switches to Static, like most RGB apps
        if (!EffectInfo.UsesColour(CurrentState.Effect))
        {
            CurrentState.Effect = EffectKind.Static;
            LoadStateIntoControls();
        }
        MarkDirty();
    }

    private void ShowColor(Rgb c)
    {
        ColorPreview.Background = ToBrush(c);
        if (!HexBox.IsKeyboardFocused) HexBox.Text = c.ToHex();
    }

    private void HexApply_Click(object sender, RoutedEventArgs e) => ApplyHex();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyHex();
    }

    private void ApplyHex()
    {
        if (Rgb.TryParseHex(HexBox.Text, out var c))
        {
            SetCurrentColor(c, updateWheel: true);
            HexBox.Text = c.ToHex();
        }
        else
        {
            HexBox.Text = CurrentState.Color.ToHex();
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText != null) BrightnessText.Text = $"{e.NewValue:0}%";
        if (_loading || _engine == null) return;
        CurrentState.Brightness = e.NewValue / 100.0;
        MarkDirty();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _engine == null) return;
        CurrentState.Speed = e.NewValue;
        MarkDirty();
    }

    // ------------------------------------------------------------------ saved colours

    private void RebuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var hex in Settings.SavedColors.ToList())
        {
            var c = Rgb.FromHex(hex);
            var sw = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 9, 9),
                Background = ToBrush(c), Cursor = Cursors.Hand, ToolTip = hex,
                BorderBrush = (Brush)FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            sw.MouseEnter += (_, _) => Pop(sw, 1.15);
            sw.MouseLeave += (_, _) => Pop(sw, 1.0);
            sw.MouseLeftButtonUp += (_, _) => SetCurrentColor(c, updateWheel: true);
            sw.MouseRightButtonUp += (_, _) =>
            {
                Settings.SavedColors.Remove(hex);
                RebuildSwatches();
                MarkDirty();
            };
            SwatchPanel.Children.Add(sw);
        }
    }

    private void SaveColor_Click(object sender, RoutedEventArgs e)
    {
        var hex = CurrentState.Color.ToHex();
        if (!Settings.SavedColors.Contains(hex, StringComparer.OrdinalIgnoreCase))
        {
            Settings.SavedColors.Add(hex);
            RebuildSwatches();
            MarkDirty();
        }
    }

    // ------------------------------------------------------------------ sync

    private void SyncToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _engine.Sync = SyncToggle.IsChecked == true;
        if (!_loading)
        {
            // when un-syncing, start the room from whatever the PC was showing
            if (!_engine.Sync)
            {
                var copy = _engine.PcState.Clone();
                if (copy.Effect is EffectKind.Clock or EffectKind.RainbowWave) copy.Effect = EffectKind.Static;
                _engine.RoomState = copy;
            }
            MarkDirty();
            if (_page == Page.Room) LoadStateIntoControls();
        }
        UpdateSyncBanner();
    }

    private void TurnOffSync_Click(object sender, RoutedEventArgs e)
    {
        if (_page != Page.Ring) { SyncToggle.IsChecked = false; return; }
        if (!RingIsSetUp) { OpenClockSetup_Click(sender, e); return; }
        SetRingSeparate(true);
    }

    private void SetRingSeparate(bool on)
    {
        _engine.RingSeparate = on;
        Settings.RingSeparate = on;
        RingSeparateCheck.IsChecked = on;
        RingCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && _engine.PcState.Effect == EffectKind.Clock)
        {
            _engine.PcState.Effect = EffectKind.Static;
            _engine.RingState.Effect = EffectKind.Clock;
        }
        SaveNow();
        if (_page != Page.Settings) ShowPage(_page);
    }

    private void AllLightsOff()
    {
        _engine.PcState.Effect = EffectKind.Off;
        _engine.RoomState.Effect = EffectKind.Off;
        if (_page != Page.Settings) LoadStateIntoControls();
        MarkDirty();
    }

    // ------------------------------------------------------------------ connect buttons

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Pc)
        {
            AutoFindOpenRgb();
            var err = SystemHelpers.TryStartOpenRgb(Settings.OpenRgbPath);
            if (err == null && !string.IsNullOrWhiteSpace(Settings.OpenRgbPath) && !SystemHelpers.IsOpenRgbRunning())
                await Task.Delay(2500);
            await _pc.ConnectAsync();
            RebuildOrgbDeviceList();
        }
        else if (_page == Page.Room)
        {
            if (!_room.IsConfigured)
            {
                SettingsCard.IsChecked = true;
                return;
            }
            await _room.ReconnectAsync();
        }
    }

    // ------------------------------------------------------------------ status + preview

    private void RefreshStatus()
    {
        SetDot(PcDot, _pc.Status);
        SetDot(RoomDot, _engine.RoomEnabled ? _room.Status : DeviceStatus.Disconnected);
        PcCardStatus.Text = _pc.Status == DeviceStatus.Connected ? _pc.StatusText : Short(_pc.Status);
        RoomCardName.Text = _room.DisplayName;
        RoomCardStatus.Text = !_engine.RoomEnabled ? "Turned off"
            : !_room.IsConfigured ? "Not set up yet"
            : _room.Status == DeviceStatus.Connected ? (_engine.Sync ? "Connected · synced" : "Connected")
            : Short(_room.Status);

        if (_page == Page.Pc)
        {
            SetDot(DeviceDot, _pc.Status);
            DeviceStatusText.Text = _pc.StatusText;
            ConnectBtn.Content = _pc.Status == Core.Devices.DeviceStatus.Connected ? "Reconnect" : "Connect";
        }
        else if (_page == Page.Room)
        {
            SetDot(DeviceDot, _room.Status);
            DeviceStatusText.Text = _room.StatusText;
            ConnectBtn.Content = !_room.IsConfigured ? "Set up" : _room.Status == Core.Devices.DeviceStatus.Connected ? "Reconnect" : "Connect";
        }
        else if (_page == Page.Ring)
        {
            SetDot(DeviceDot, _pc.Status);
            DeviceStatusText.Text = !RingIsSetUp ? "Not set up yet"
                : $"LEDs {Settings.ClockStartLed}–{Settings.ClockStartLed + Settings.ClockLedCount - 1} of My PC" +
                  (_engine.RingSeparate ? " · own colour/effect" : " · follows My PC");
        }
        RingCardStatus.Text = !RingIsSetUp ? "Not set up yet" : _engine.RingSeparate ? EffectInfo.DisplayName(_engine.RingState.Effect) : "Follows My PC";
        OrgbStatus.Text = _pc.StatusText;
        RoomStatusText.Text = _room.StatusText;
    }

    private static string Short(DeviceStatus s) => s switch
    {
        Core.Devices.DeviceStatus.Connecting => "Connecting...",
        Core.Devices.DeviceStatus.Error => "Can't connect",
        Core.Devices.DeviceStatus.Connected => "Connected",
        _ => "Not connected",
    };

    private void SetDot(System.Windows.Shapes.Ellipse dot, DeviceStatus s)
    {
        dot.Fill = s switch
        {
            Core.Devices.DeviceStatus.Connected => (Brush)FindResource("GoodBrush"),
            Core.Devices.DeviceStatus.Connecting => (Brush)FindResource("WarnBrush"),
            Core.Devices.DeviceStatus.Error => (Brush)FindResource("BadBrush"),
            _ => (Brush)FindResource("MutedBrush"),
        };
    }

    private void UpdatePreview()
    {
        if (!IsVisible) return;
        var fanFrame = _engine.FanPreview.Length > 0 ? _engine.FanPreview : _engine.PcPreview;
        PcSwatch.Fill = ToBrush(Average(fanFrame));
        RoomSwatch.Fill = ToBrush(_engine.RoomPreview);
        var ringFrame = _engine.RingSeparate && _engine.RingPreview.Length > 0 ? _engine.RingPreview : fanFrame;
        RingSwatch.Stroke = ToBrush(Average(ringFrame));

        if (_page == Page.Settings) return;

        if (_page == Page.Room)
        {
            // the room light is drawn as a strip with paintable sections
            Fan.Visibility = Visibility.Collapsed;
            RoomGlow.Visibility = Visibility.Collapsed;
            Strip.Visibility = Visibility.Visible;
            bool paint = EffectInfo.UsesPalette(CurrentState.Effect);
            Strip.Editable = paint;
            Strip.Segments = Math.Max(1, Settings.StripSegments);
            var live = _engine.RoomSegmentsPreview;
            if (paint && !(CurrentState.Spin && live != null)) Strip.SetColors(ScaledCustom(CurrentState));
            else if (live != null) Strip.SetColors(live);
            else Strip.SetColors(new[] { _engine.RoomPreview });
            LedPreviewInfo.Text = paint ? "tap or drag to paint"
                : _room.Status == Core.Devices.DeviceStatus.Connected ? "● live" : "offline · preview";
        }
        else
        {
            Strip.Visibility = Visibility.Collapsed;
            Fan.Visibility = Visibility.Visible;
            bool editing = EffectInfo.UsesPalette(CurrentState.Effect);
            Fan.Editable = editing;
            Fan.Segments = Math.Max(1, Settings.FanLeds);
            if (editing) Fan.SetColors(ScaledCustom(CurrentState));
            else Fan.SetColors(_page == Page.Ring ? ringFrame : fanFrame);
            LedPreviewInfo.Text = editing ? "tap or drag to paint"
                : _pc.Status == Core.Devices.DeviceStatus.Connected ? "● live" : "offline · preview";
        }

        if (MusicRow.Visibility == Visibility.Visible)
        {
            var a = _audio.Current;
            double w = Math.Clamp(Math.Max(a.Level, a.Bass), 0, 1);
            var parent = (FrameworkElement)MusicLevel.Parent;
            MusicLevel.Width = parent.ActualWidth * w;
            MusicStatus.Text = _audio.IsRunning
                ? (w > 0.02 ? "● listening" : "play something")
                : "starting audio";
        }
    }

    private bool _zonesApplied;

    private void SlowTick()
    {
        if (!_zonesApplied && _pc.Status == DeviceStatus.Connected)
        {
            _zonesApplied = true;
            _ = ApplySavedZoneSizesAsync();
        }

        // music: sensitivity follows the slider, and the room strip gets more updates per second
        var musicState = EffectInfo.IsMusic(_engine.PcState.Effect) ? _engine.PcState
            : EffectInfo.IsMusic(_engine.RoomState.Effect) ? _engine.RoomState : null;
        if (musicState != null) _audio.Sensitivity = musicState.Speed * 1.5;
        int wantFps = Math.Max(Settings.RoomUpdatesPerSecond, 12); // live streaming to the strip
        if (_room.UpdatesPerSecond != wantFps) _room.UpdatesPerSecond = wantFps;

        // strip keeps failing? the router probably moved it - find it again by MAC (in the background)
        if (_engine.RoomFailures >= 3 && !_refinding && !string.IsNullOrWhiteSpace(Settings.RoomMac)
            && (DateTime.UtcNow - _lastRefind).TotalSeconds > 90)
        {
            _refinding = true;
            _lastRefind = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (ip, open, _) = await TuyaDiscovery.FindByMacAsync(Settings.RoomMac);
                    if (ip != null && ip != Settings.RoomIp)
                    {
                        await Dispatcher.InvokeAsync(() => { Settings.RoomIp = ip; _room.Ip = ip; RoomIpBox.Text = ip; SaveNow(); });
                    }
                }
                catch { }
                finally { _engine.RoomFailures = 0; _refinding = false; }
            });
        }

        // only listen to audio while a music effect is active
        if (_engine.NeedsAudio) _audio.Start();
        else if (_audio.IsRunning) _audio.Stop();

        if (_dirty) SaveNow();
    }

    // ------------------------------------------------------------------ settings page: OpenRGB

    private void LoadSettingsIntoForm()
    {
        OrgbHost.Text = Settings.OpenRgbHost;
        OrgbPort.Text = Settings.OpenRgbPort.ToString();
        OrgbPath.Text = Settings.OpenRgbPath;

        RoomEnabledCheck.IsChecked = Settings.RoomEnabled;
        RoomNameBox.Text = Settings.RoomName;
        RoomIpBox.Text = Settings.RoomIp;
        RoomIdBox.Text = Settings.RoomDeviceId;
        RoomKeyBox.Text = Settings.RoomLocalKey;
        RoomMacBox.Text = Settings.RoomMac;
        RoomVersionBox.SelectedIndex = Settings.RoomVersion switch { "3.3" => 0, "3.5" => 2, _ => 1 };
        RoomProfileBox.SelectedIndex = Settings.RoomProfile == TuyaDpsProfile.Legacy ? 1 : 0;
        RoomFpsSlider.Value = Settings.RoomUpdatesPerSecond;
        RoomFpsText.Text = $"{Settings.RoomUpdatesPerSecond}/s";

        ClockStartBox.Text = Settings.ClockStartLed.ToString();
        ClockCountBox.Text = Settings.ClockLedCount.ToString();
        ClockTopBox.Text = Settings.ClockTopOffset.ToString();
        ClockHourBox.Text = Settings.ClockHourColor;
        ClockMinuteBox.Text = Settings.ClockMinuteColor;
        ClockSecondBox.Text = Settings.ClockSecondColor;
        ClockClockwiseCheck.IsChecked = Settings.ClockClockwise;
        ClockSecondsCheck.IsChecked = Settings.ClockShowSeconds;
        RingSeparateCheck.IsChecked = Settings.RingSeparate;
        FanLedsBox.Text = Settings.FanLeds.ToString();

        StartWithWindowsCheck.IsChecked = Settings.StartWithWindows;
        StartMinimizedCheck.IsChecked = Settings.StartMinimized;
        MinimizeToTrayCheck.IsChecked = Settings.MinimizeToTray;
        PcOffCheck.IsChecked = Settings.PcLightsOffWhenPcOff;
        SettingsPathText.Text = "Settings are saved in " + AppSettings.FilePath;
    }

    private void RebuildOrgbDeviceList()
    {
        OrgbDevicesPanel.Children.Clear();
        var all = _pc.AllControllers;
        if (all.Count == 0)
        {
            OrgbDevicesPanel.Children.Add(new TextBlock
            {
                Text = "No devices yet — connect to OpenRGB first.", Style = (Style)FindResource("Muted"),
            });
            return;
        }
        foreach (var c in all)
        {
            var zones = string.Join(", ", c.Zones.Select(z => $"{z.Name}: {z.LedCount}"));
            var cb = new CheckBox
            {
                Content = $"{c.Name}  —  {c.LedCount} LEDs  ({zones})",
                IsChecked = !Settings.ExcludedControllers.Contains(c.Name, StringComparer.OrdinalIgnoreCase),
                Tag = c.Name,
            };
            cb.Click += (_, _) =>
            {
                var name = (string)cb.Tag;
                Settings.ExcludedControllers.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (cb.IsChecked != true) Settings.ExcludedControllers.Add(name);
                _pc.ExcludedControllers.Clear();
                foreach (var n in Settings.ExcludedControllers) _pc.ExcludedControllers.Add(n);
                _pc.RefreshSelection();
                MarkDirty();
            };
            OrgbDevicesPanel.Children.Add(cb);

            foreach (var z in c.Zones.Where(z => z.Resizable))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 2, 0, 6) };
                row.Children.Add(new TextBlock
                {
                    Text = $"↳ {z.Name} · LEDs", Style = (Style)FindResource("Muted"),
                    VerticalAlignment = VerticalAlignment.Center, Width = 260,
                });
                var box = new TextBox { Width = 60, Text = z.LedCount.ToString(), TextAlignment = TextAlignment.Center };
                var set = new Button { Content = "Set", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
                int ctrlIndex = c.Index, zoneIndex = z.Index, max = z.LedsMax, min = z.LedsMin;
                string key = c.Name + "|" + z.Name;
                set.Click += async (_, _) =>
                {
                    if (!int.TryParse(box.Text, out var n)) return;
                    n = Math.Clamp(n, min, max);
                    Settings.ZoneSizes[key] = n;
                    SaveNow();
                    await _pc.ResizeZoneAsync(ctrlIndex, zoneIndex, n);
                    RebuildOrgbDeviceList();
                    RefreshStatus();
                };
                row.Children.Add(box);
                row.Children.Add(set);
                var isRing = new Button { Content = "This is the pump ring", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
                int zoneStartGlobal = _pc.GetControllerOffset(c.Index) + z.StartLed, zoneCount = z.LedCount;
                isRing.Click += (_, _) =>
                {
                    if (zoneStartGlobal < 0 || zoneCount <= 0) return;
                    ClockStartBox.Text = zoneStartGlobal.ToString();
                    ClockCountBox.Text = zoneCount.ToString();
                    RingSeparateCheck.IsChecked = true;
                    ReadClockForm();
                    ClockCard.BringIntoView();
                };
                row.Children.Add(isRing);
                row.Children.Add(new TextBlock
                {
                    Text = $"  (max {z.LedsMax})", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center,
                });
                OrgbDevicesPanel.Children.Add(row);
            }
        }
        if (all.All(c => c.LedCount <= 1) || all.Any(c => c.Zones.Any(z => z.Name.Contains("LED", StringComparison.OrdinalIgnoreCase) && z.LedCount == 0)))
        {
            OrgbDevicesPanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0),
                Text = "Header shows 0 LEDs? Set its LED count above.",
            });
        }
    }

    private async void OrgbConnect_Click(object sender, RoutedEventArgs e)
    {
        Settings.OpenRgbHost = string.IsNullOrWhiteSpace(OrgbHost.Text) ? "127.0.0.1" : OrgbHost.Text.Trim();
        Settings.OpenRgbPort = int.TryParse(OrgbPort.Text, out var p) ? p : 6742;
        Settings.OpenRgbPath = OrgbPath.Text.Trim().Trim('"');
        ApplySettingsToDevices();
        SaveNow();

        var err = SystemHelpers.TryStartOpenRgb(Settings.OpenRgbPath);
        if (err != null) { OrgbStatus.Text = err; }
        else if (!string.IsNullOrWhiteSpace(Settings.OpenRgbPath)) await Task.Delay(2500);
        await _pc.ConnectAsync();
        RebuildOrgbDeviceList();
        RefreshStatus();
    }

    private void BrowseOpenRgb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Find OpenRGB.exe",
            Filter = "OpenRGB|OpenRGB.exe|Programs|*.exe",
        };
        if (dlg.ShowDialog(this) == true) OrgbPath.Text = dlg.FileName;
    }

    private void DownloadOpenRgb_Click(object sender, RoutedEventArgs e) =>
        SystemHelpers.OpenUrl("https://openrgb.org/releases.html");

    // ------------------------------------------------------------------ settings page: room light

    private void RoomFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RoomFpsText != null) RoomFpsText.Text = $"{e.NewValue:0}/s";
    }

    private async void RoomConnect_Click(object sender, RoutedEventArgs e)
    {
        Settings.RoomEnabled = RoomEnabledCheck.IsChecked == true;
        Settings.RoomName = string.IsNullOrWhiteSpace(RoomNameBox.Text) ? "Room Light" : RoomNameBox.Text.Trim();
        Settings.RoomIp = RoomIpBox.Text.Trim();
        Settings.RoomDeviceId = RoomIdBox.Text.Trim();
        Settings.RoomLocalKey = RoomKeyBox.Text.Trim();
        Settings.RoomVersion = (RoomVersionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "3.4";
        Settings.RoomProfile = RoomProfileBox.SelectedIndex == 1 ? TuyaDpsProfile.Legacy : TuyaDpsProfile.Standard;
        Settings.RoomUpdatesPerSecond = (int)RoomFpsSlider.Value;
        _engine.RoomEnabled = Settings.RoomEnabled;
        ApplySettingsToDevices();
        SaveNow();

        if (Settings.RoomLocalKey.Length != 16 && Settings.RoomLocalKey.Length > 0)
        {
            RoomStatusText.Text = $"The local key should be 16 characters (yours is {Settings.RoomLocalKey.Length}).";
            return;
        }
        if (!Settings.RoomEnabled) { _room.Disconnect(); RefreshStatus(); return; }
        await _room.ConnectAsync();
        RefreshStatus();
    }

    private void RoomTest_Click(object sender, RoutedEventArgs e)
    {
        if (_room.Status != Core.Devices.DeviceStatus.Connected)
        {
            RoomStatusText.Text = "Connect first (Save & connect).";
            return;
        }
        var seq = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) };
        int step = 0;
        _testTimer?.Stop();
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _engine.RoomOverride = seq[0];
        _testTimer.Tick += (_, _) =>
        {
            step++;
            if (step < seq.Length) _engine.RoomOverride = seq[step];
            else { _engine.RoomOverride = null; _testTimer!.Stop(); }
        };
        _testTimer.Start();
    }

    private void RoomReadStatus_Click(object sender, RoutedEventArgs e)
    {
        RoomRawStatus.Visibility = Visibility.Visible;
        RoomRawStatus.Text = _room.Status == Core.Devices.DeviceStatus.Connected ? "Asking the light..." : "Not connected.";
        _room.RequestStatus();
    }

    private async void GetKey_Click(object sender, RoutedEventArgs e)
    {
        string id = CloudIdBox.Text.Trim(), secret = CloudSecretBox.Password.Trim();
        string dev = RoomIdBox.Text.Trim();
        if (id == "" || secret == "" || dev == "")
        {
            ScanStatus.Text = "Paste the Access ID and Access Secret (and make sure the Device ID is filled in).";
            return;
        }
        var btn = (Button)sender;
        btn.IsEnabled = false;
        ScanStatus.Text = "Asking Tuya for the key...";
        try
        {
            TuyaCloud.DeviceInfo? info = null;
            string lastError = "";
            foreach (var (name, host) in TuyaCloud.Regions)
            {
                try { info = await TuyaCloud.GetDeviceAsync(host, id, secret, dev); break; }
                catch (Exception ex) { lastError = ex.Message; }
            }
            if (info == null) { ScanStatus.Text = "Couldn't get the key: " + lastError; return; }

            RoomKeyBox.Text = info.LocalKey;
            Settings.RoomLocalKey = info.LocalKey;
            Settings.RoomDeviceId = dev;
            SaveNow();
            CloudSecretBox.Clear();
            ScanStatus.Text = $"Got the key for '{info.Name}'. Finding the strip and connecting...";

            // refresh the IP (re-pairing can change it), then connect
            if (!string.IsNullOrWhiteSpace(RoomMacBox.Text))
            {
                var (ip, _, _) = await TuyaDiscovery.FindByMacAsync(RoomMacBox.Text.Trim());
                if (ip != null) { RoomIpBox.Text = ip; Settings.RoomIp = ip; }
            }
            RoomConnect_Click(sender, e);
        }
        finally { btn.IsEnabled = true; }
    }

    private async void DeepSearch_Click(object sender, RoutedEventArgs e)
    {
        var mac = RoomMacBox.Text.Trim();
        if (mac.Length < 12) { ScanStatus.Text = "Type the strip's MAC first (e.g. aa:bb:cc:dd:ee:ff)."; return; }
        Settings.RoomMac = mac;
        SaveNow();
        var btn = (Button)sender;
        btn.IsEnabled = false;
        ScanStatus.Text = "Deep search: checking every address on your network (up to ~30 seconds)...";
        try
        {
            var (ip, open, report) = await TuyaDiscovery.FindByMacAsync(mac);
            try
            {
                AppLog.Write("scan.log", report);
            }
            catch { }
            if (ip != null)
            {
                RoomIpBox.Text = ip;
                Settings.RoomIp = ip;
                SaveNow();
                ScanStatus.Text = open
                    ? $"Found your strip at {ip} and the PC can talk to it! Only the Local Key is missing now."
                    : $"Found your strip at {ip}, but it isn't accepting connections from the PC (the Wi-Fi may be blocking it).";
            }
            else
            {
                ScanStatus.Text = "Couldn't reach the strip from this PC - the halls Wi-Fi is keeping devices apart. Details saved to scan.log.";
            }
        }
        finally { btn.IsEnabled = true; }
    }

    private async void FindStrip_Click(object sender, RoutedEventArgs e)
    {
        ScanResults.Children.Clear();
        ScanStatus.Text = "Listening for Tuya / Calex devices on your Wi-Fi (about 8 seconds)...";
        var btn = (Button)sender;
        btn.IsEnabled = false;
        List<TuyaFound> found;
        List<string> open;
        try
        {
            var scan = TuyaDiscovery.ScanAsync(TimeSpan.FromSeconds(8));
            var sweep = TuyaDiscovery.SweepAsync();
            await Task.WhenAll(scan, sweep);
            found = scan.Result;
            open = sweep.Result;
        }
        finally { btn.IsEnabled = true; }

        // devices answering on the Tuya port that didn't announce themselves (firewall ate the broadcast)
        foreach (var ip in open)
            if (!found.Any(f => f.Ip == ip)) found.Add(new TuyaFound(ip, "", "", ""));

        if (found.Count == 0)
        {
            ScanStatus.Text = "Nothing found. Make sure the strip is switched on and the PC is on the same Wi-Fi/router. " +
                              "If it still isn't found, the strip is probably Bluetooth-only.";
            return;
        }
        ScanStatus.Text = $"Found {found.Count} device(s). Pick your strip:";
        foreach (var d in found)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = d.DeviceId == ""
                    ? $"{d.Ip}   ·   Tuya device (ID comes with the key)"
                    : $"{d.Ip}   ·   ID {d.DeviceId}   ·   v{(d.Version == "" ? "?" : d.Version)}",
                FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            });
            var use = new Button { Content = "Use this", Padding = new Thickness(12, 4, 12, 4) };
            Grid.SetColumn(use, 1);
            use.Click += (_, _) =>
            {
                RoomIpBox.Text = d.Ip;
                if (d.DeviceId != "") RoomIdBox.Text = d.DeviceId;
                if (d.Version != "") RoomVersionBox.SelectedIndex = d.Version switch { "3.3" => 0, "3.5" => 2, _ => 1 };
                ScanStatus.Text = "Filled in. Now the Local Key is the only thing missing.";
                Settings.RoomIp = d.Ip;
                if (d.DeviceId != "") Settings.RoomDeviceId = d.DeviceId;
                if (d.Version != "") Settings.RoomVersion = d.Version is "3.3" or "3.5" ? d.Version : "3.4";
                SaveNow();
            };
            row.Children.Add(use);
            ScanResults.Children.Add(row);
        }
    }

    private void RoomHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Getting your Calex strip's details (one-time, ~15 min):\n\n" +
            "1. Make sure the strip works in the Calex Smart app.\n" +
            "2. Create a free account at iot.tuya.com (Tuya Developer Platform).\n" +
            "3. Cloud → Development → Create Cloud Project (Smart Home, data centre: Central Europe).\n" +
            "4. In the project: Devices → Link App Account → scan the QR code with the Calex Smart app (Me → scan icon).\n" +
            "5. Your strip now appears in the device list. Its Device ID is shown there.\n" +
            "6. Get the Local Key via API Explorer → Device Management → Query Device Details (the 'local_key' field).\n" +
            "7. Find the strip's IP in your router's device list, or in the Calex app (device → ⋯ → Device information).\n\n" +
            "Protocol version: newer Calex strips are usually 3.4 or 3.5. If one doesn't connect, try the next.\n\n" +
            "Tip: give the strip a fixed IP in your router so it never changes.",
            "Room light setup", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ settings page: ring clock + finder

    private int FinderValue => int.TryParse(FinderIndex.Text, out var v) ? Math.Max(0, v) : 0;

    private void Finder_Changed(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        if (FinderCheck.IsChecked == true && BlockCheck.IsChecked == true) BlockCheck.IsChecked = false;
        _engine.FinderLed = FinderCheck.IsChecked == true ? FinderValue : null;
        UpdateFinderInfo();
    }

    private void FinderMinus_Click(object sender, RoutedEventArgs e) => FinderIndex.Text = Math.Max(0, FinderValue - 1).ToString();

    private void FinderPlus_Click(object sender, RoutedEventArgs e)
    {
        int max = Math.Max(0, _pc.LedCount - 1);
        FinderIndex.Text = Math.Min(max == 0 ? FinderValue + 1 : max, FinderValue + 1).ToString();
    }

    private void FinderIndex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_engine == null) return;
        if (FinderCheck.IsChecked == true) _engine.FinderLed = FinderValue;
        if (BlockCheck.IsChecked == true) UpdateBlockTest();
        UpdateFinderInfo();
    }

    private void UpdateFinderInfo()
    {
        if (FinderInfo == null) return;
        int idx = FinderValue;
        if (_pc.Status != Core.Devices.DeviceStatus.Connected)
        {
            FinderInfo.Text = "Connect OpenRGB first so the finder can light your real LEDs.";
            return;
        }
        int off = 0;
        foreach (var c in _pc.ActiveControllers)
        {
            if (idx < off + c.LedCount)
            {
                int local = idx - off;
                var zone = c.Zones.LastOrDefault(z => local >= z.StartLed);
                FinderInfo.Text = $"LED #{idx} of {_pc.LedCount} → {c.Name}" +
                                  (zone != null ? $", zone '{zone.Name}', LED {local - zone.StartLed + 1} of {zone.LedCount}" : "");
                return;
            }
            off += c.LedCount;
        }
        FinderInfo.Text = $"There are only {_pc.LedCount} LEDs (0 to {_pc.LedCount - 1}).";
    }

    private void Identify_Changed(object sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _engine.IdentifyZones = IdentifyCheck.IsChecked == true;
        IdentifyLegend.Visibility = _engine.IdentifyZones ? Visibility.Visible : Visibility.Collapsed;
        var names = new[] { "red", "blue", "green", "yellow", "purple", "cyan", "white" };
        var parts = new List<string>();
        int n = 0;
        foreach (var c in _pc.ActiveControllers)
            foreach (var z in c.Zones)
                parts.Add($"{z.Name} = {names[n++ % names.Length]}{(z.LedCount == 0 ? " (0 LEDs)" : "")}");
        IdentifyLegend.Text = string.Join("   ·   ", parts);
    }

    private void Block_Changed(object sender, RoutedEventArgs e) => UpdateBlockTest();

    private void BlockSize_TextChanged(object sender, TextChangedEventArgs e) => UpdateBlockTest();

    private void UpdateBlockTest()
    {
        if (_engine == null || BlockLegend == null) return;
        int size = int.TryParse(BlockSizeBox.Text, out var z) ? Math.Max(1, z) : 12;
        bool on = BlockCheck.IsChecked == true;
        if (on && FinderCheck.IsChecked == true) FinderCheck.IsChecked = false;
        _engine.BlockTest = on ? (FinderValue, size) : null;
        BlockLegend.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var names = new[] { "red", "blue", "green", "yellow", "purple", "cyan", "white" };
        var parts = new List<string>();
        int start = FinderValue;
        for (int b = 0; b < names.Length * 2 && start + b * size < Math.Max(_pc.LedCount, start + 1); b++)
            parts.Add($"{start + b * size}–{start + (b + 1) * size - 1} = {names[b % names.Length]}");
        BlockLegend.Text = string.Join("   ·   ", parts);
    }

    private void FinderUseStart_Click(object sender, RoutedEventArgs e) => ClockStartBox.Text = FinderValue.ToString();

    private bool ReadClockForm()
    {
        if (!int.TryParse(ClockStartBox.Text, out var start) || start < 0 ||
            !int.TryParse(ClockCountBox.Text, out var count) || count < 1 ||
            !int.TryParse(ClockTopBox.Text, out var top) || top < 0)
        {
            MessageBox.Show(this, "Please enter whole numbers for the ring LEDs.", "Ring clock");
            return false;
        }
        Settings.ClockStartLed = start;
        Settings.ClockLedCount = count;
        Settings.ClockTopOffset = top % count;
        Settings.ClockClockwise = ClockClockwiseCheck.IsChecked == true;
        Settings.ClockShowSeconds = ClockSecondsCheck.IsChecked == true;
        Settings.RingConfigured = true;
        Settings.ClockHourColor = Rgb.TryParseHex(ClockHourBox.Text, out var h) ? h.ToHex() : Settings.ClockHourColor;
        Settings.ClockMinuteColor = Rgb.TryParseHex(ClockMinuteBox.Text, out var m) ? m.ToHex() : Settings.ClockMinuteColor;
        Settings.ClockSecondColor = Rgb.TryParseHex(ClockSecondBox.Text, out var s) ? s.ToHex() : Settings.ClockSecondColor;
        _engine.Ring = Settings.ToRing();
        SetRingSeparate(RingSeparateCheck.IsChecked == true);
        RefreshStatus();
        return true;
    }

    private void ClockSave_Click(object sender, RoutedEventArgs e) => ReadClockForm();

    private void ClockShow_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadClockForm()) return;
        FinderCheck.IsChecked = false;
        if (_engine.RingSeparate) _engine.RingState.Effect = EffectKind.Clock;
        else _engine.PcState.Effect = EffectKind.Clock;
        MarkDirty();
        if (_engine.RingSeparate) RingCard.IsChecked = true; else PcCard.IsChecked = true;
    }

    private void OpenClockSetup_Click(object sender, RoutedEventArgs e)
    {
        SettingsCard.IsChecked = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ClockCard.BringIntoView());
    }

    // ------------------------------------------------------------------ app options

    private void AppOptions_Click(object sender, RoutedEventArgs e)
    {
        Settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        Settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        Settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        Settings.PcLightsOffWhenPcOff = PcOffCheck.IsChecked == true;
        SystemHelpers.SetStartWithWindows(Settings.StartWithWindows);
        SaveNow();
    }

    // ------------------------------------------------------------------ saving / window lifetime

    private void MarkDirty() => _dirty = true;

    private void SaveNow()
    {
        Settings.Sync = _engine.Sync;
        Settings.Pc = DeviceLook.From(_engine.PcState);
        Settings.Room = DeviceLook.From(_engine.RoomState);
        Settings.RingLook = DeviceLook.From(_engine.RingState);
        Settings.Save();
        _dirty = false;
    }

    public void BringToFront() => ShowFromTray();

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && Settings.MinimizeToTray)
        {
            e.Cancel = true;
            FinderCheck.IsChecked = false;
            Hide();
            if (!_trayTipShown)
            {
                _tray.ShowTip("RGB Sync is still running", "Your lights keep going. Right-click the tray icon to exit.");
                _trayTipShown = true;
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Cleanup();
        Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void Cleanup()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _frameTimer.Stop();
        _slowTimer.Stop();
        SaveNow();
        _audio.Dispose();
        _engine.Dispose();
        _tray.Dispose();
    }

    // ------------------------------------------------------------------ multi-colour painting

    private static Rgb[] Resample(Rgb[] src, int n)
    {
        if (src.Length == n) return src;
        if (src.Length == 0) return LightState.DefaultCustom(n);
        var a = new Rgb[n];
        for (int i = 0; i < n; i++) a[i] = src[i * src.Length / n];
        return a;
    }

    private Rgb[] ScaledCustom(LightState s)
    {
        var a = new Rgb[s.Custom.Length];
        for (int i = 0; i < a.Length; i++) a[i] = s.Custom[i].Scale(s.Brightness);
        return a;
    }

    private int PaintCount => _page == Page.Room ? Math.Max(1, Settings.StripSegments) : _engine.FanLeds;

    private void Fan_SegmentPainted(int seg)
    {
        var s = CurrentState;
        if (!EffectInfo.UsesPalette(s.Effect)) return;
        s.Custom = Resample(s.Custom, PaintCount);
        if (seg < 0 || seg >= s.Custom.Length || s.Custom[seg] == s.Color) return;
        var copy = (Rgb[])s.Custom.Clone();
        copy[seg] = s.Color;
        s.Custom = copy;
        MarkDirty();
    }

    private void FillAll_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentState;
        var a = new Rgb[PaintCount];
        Array.Fill(a, s.Color);
        s.Custom = a;
        MarkDirty();
    }

    private void RainbowFill_Click(object sender, RoutedEventArgs e)
    {
        var a = new Rgb[PaintCount];
        for (int i = 0; i < a.Length; i++) a[i] = Rgb.FromHsv(i * 360.0 / a.Length, 1, 1);
        CurrentState.Custom = a;
        MarkDirty();
    }

    private void TwoTone_Click(object sender, RoutedEventArgs e)
    {
        // half the ring in the picked colour, half in a partner colour - blue gives pink, like your fans
        var s = CurrentState;
        var (h, sat, v) = s.Color.ToHsv();
        var other = Rgb.FromHsv(h + 90, Math.Max(0.6, sat), Math.Max(0.8, v));
        int n = PaintCount;
        var a = new Rgb[n];
        for (int i = 0; i < n; i++)
        {
            double t = (Math.Cos(i * 2 * Math.PI / n) + 1) / 2; // smooth blend around the ring
            a[i] = Rgb.Lerp(other, s.Color, t);
        }
        s.Custom = a;
        MarkDirty();
    }

    private void SpinToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _engine == null) return;
        CurrentState.Spin = SpinToggle.IsChecked == true;
        MarkDirty();
    }

    private void FanLedsSet_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FanLedsBox.Text, out var n)) return;
        n = Math.Clamp(n, 1, 64);
        Settings.FanLeds = n;
        _engine.FanLeds = n;
        foreach (var st in new[] { _engine.PcState, _engine.RingState })
            st.Custom = Resample(st.Custom, n);
        FanLedsBox.Text = n.ToString();
        SaveNow();
    }

    // ------------------------------------------------------------------ polish: animations + dark title bar

    private static void AnimateIn(FrameworkElement? el)
    {
        if (el == null) return;
        var ease = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var tt = new TranslateTransform(0, 18);
        el.RenderTransform = tt;
        el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = ease });
    }

    private static void Pop(FrameworkElement el, double to)
    {
        if (el.RenderTransform is not ScaleTransform st) return;
        var ease = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var a = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // dark title bar
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));       // rounded corners (Windows 11)
            int caption = 0x00000000;                                      // black, like the window
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // caption colour (Windows 11)
        }
        catch { }
    }

    // ------------------------------------------------------------------ helpers

    private static SolidColorBrush ToBrush(Rgb c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Rgb Average(Rgb[] frame)
    {
        if (frame.Length == 0) return Rgb.Black;
        long r = 0, g = 0, b = 0;
        foreach (var c in frame) { r += c.R; g += c.G; b += c.B; }
        return new Rgb((byte)(r / frame.Length), (byte)(g / frame.Length), (byte)(b / frame.Length));
    }
}
