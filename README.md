# RGB Sync

A small Windows app that controls your **PC case lighting** and a **Wi-Fi room LED strip** from one place, and can keep them in sync.
It handles colours only; fan speed stays with your motherboard software.

Built with C# / .NET 8 / WPF.

## Features

- **Two devices, one app**
  - **My PC:** motherboard ARGB headers, fans and pump ring, via the [OpenRGB](https://openrgb.org) SDK server.
  - **Room Light:** Tuya-based strips (e.g. Calex Smart RGBIC), controlled directly over your local Wi-Fi with protocol 3.3, 3.4 and 3.5. It doesn't go through the cloud.
- **Effects:** Static, Multi-colour (paint your own pattern), Comet, Rainbow Comet, Rainbow Wave, Breathing, Colour Cycle, Music Pulse, Music Rainbow, Ring Clock and Off.
- **Music sync:** reacts to whatever the PC is playing (YouTube, Spotify, games) through WASAPI loopback. It doesn't use a microphone. Beat detection runs on the kick-drum band.
- **Paint mode:** tap or drag on the fan, or on the 20 strip sections, to paint them. Patterns can spin.
- **Sync:** the room strip follows the PC effect in real time.
  - It uses the strip's own music channel for instant colour changes.
  - It uses section painting (DP 61) for multi-colour patterns.
- **Ring Clock:** turns the pump's LED ring into a clock.
- **Reliable:** reconnects on its own and finds the strip by MAC address if its IP changes.
- **Case lights:** turn off when Windows shuts down or sleeps.
- **Tray icon**, start with Windows and auto-saved settings.

## Run it

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Double-click **`BUILD.bat`**. It builds a single self-contained `RGB Sync.exe` and starts it.

## One-time setup

**PC lights.** Install OpenRGB. You can also run `SETUP-OPENRGB.bat`, which installs it with winget and starts the SDK server. RGB Sync starts OpenRGB in the background automatically. Turn off any other RGB software that writes to the same controller, for example:
- Windows Dynamic Lighting
- Gigabyte Dynamic Light
- RGB Fusion

Otherwise the lights will flicker.

**Room strip.** Open **Settings → Room light**, then:
1. Use **Find my strip** or **Deep search** (by MAC address) to get its IP address.
2. Use **Get key** with a Tuya IoT project's Access ID and Secret to fetch the local key. The secret is used once and is never saved.

Settings are stored in `%AppData%\RGBSync\settings.json`, outside the repo.

## Project layout

```
src/RGBSync/
  Core/
    Devices/     OpenRGB SDK client, Tuya LAN protocol, PC + room devices, discovery
    Effects/     effect renderer + ring clock
    LightEngine  50 fps render loop, crossfades, sync
  Controls/      colour wheel, fan preview, strip preview (custom-drawn)
  Services/      audio meter (WASAPI loopback), tray icon, Windows helpers
  Assets/        icon + bundled fonts (Archivo, JetBrains Mono - SIL OFL 1.1)
BUILD.bat          build + publish a single exe
SETUP-OPENRGB.bat  install / start OpenRGB
```

## Credits

- [OpenRGB](https://openrgb.org) for PC lighting control.
- [NAudio](https://github.com/naudio/NAudio) for audio capture.
- Fonts [Archivo](https://github.com/Omnibus-Type/Archivo) and [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono), licensed under the SIL Open Font License.
