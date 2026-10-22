# Leyline — Virtual Audio Router, Dual-Bus Mixer, Soundboard & VST Host

**Leyline** is a native Windows 10 & Windows 11 studio audio routing and mixing application inspired by Elgato Wave Link.

## Key Features

1. **Dual-Bus Studio Mixing (Monitor Mix & Stream Mix)**
   - Every input channel strip has independent **🎧 Monitor Mix** (personal headphones/speakers) and **📡 Stream Mix** (virtual broadcast output) faders, mute buttons, fader-link toggle, and real-time stereo L/R peak meters.
   - Built-in **Stream Mix WAV Recorder** (`My Documents\Leyline Recordings`).

2. **Virtual Audio Cables & App Routing (Windows 10 2004+ & 11)**
   - Each channel uses a Leyline virtual cable. Assigning an application (`Chrome`, `Spotify`, `Discord`, games, ...) to a channel routes its Windows output to the cable's playback side (`IAudioPolicyConfigFactory`), and Leyline records the cable's other side. Removing an app or channel restores it to the Windows default output.
   - **Process capture fallback**: channels without a cable (or without the driver) capture their apps directly with `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`.

3. **Leyline Virtual Audio Driver**
   - Leyline's own WDM/WaveRT driver (`driver\LeylineVAD\`, based on the Microsoft SysVAD sample): **one device, one virtual cable per bus**, named like Elgato Wave Link's:
     - `System`, `Game`, `Browser`, `Music`, `Voice Chat` (Leyline Virtual Audio) — channel inputs
     - `Stream Mix (Leyline Virtual Audio)` — the Stream Mix, for OBS to record
     - `Microphone (Leyline Virtual Audio)` — the Mic Rec mix (your processed mic), for Discord and other voice apps
   - Cables show as **Disconnected** in Windows until a channel uses them; Leyline connects and disconnects them as you add and remove channels.
   - 16-bit stereo at 48 or 96 kHz on either side of a cable.

4. **VST2 / VST3 Hosting + Zero-Latency**
   - **Built-in Studio DSP Rack** on every channel strip & master bus:
     - **VoidGate**: Noise Gate
     - **VoidEQ**: 3-Band Studio Equalizer (100 Hz Low Shelf, 1.1 kHz Mid Peak, 8 kHz High Shelf) 16-band soon.
     - **VoidComp**: Studio Compressor
     - **VoidLimiter**: Limiter
   - **External 64-bit VST3 (`.vst3`) & VST2 (`.dll`) Host**:
     - Automatically scans `C:\Program Files\Common Files\VST3` and VST2 folders.
     - Hosts native plugin GUI editors plus a parameter slider rack.

5. **Built-in Soundboard with Global Hotkeys** (broken)
   - ~~Pre-loaded with 8 synthesized studio sound effects (`MLG Airhorn`, `Censor Beep`, `Sub Bass Drop`, `Ba-Dum Tss`, `Victory Fanfare`, `Sci-Fi Laser`, `8-Bit Coin`, `Dramatic Hit`) and~~ supports `.wav`, `.mp3`, `.ogg`, `.flac`, and `.m4a`.
   - Per-pad volume, playback mode (`Stop Toggle`, `One-Shot`, `Overlap`, `Loop`), independent Monitor/Stream routing, and system-wide **Global Hotkeys** (`Ctrl+Shift+1..8`, `Ctrl+Shift+0` Panic Stop).

## Installing

1. Download **`Leyline-Setup.exe`** from the [latest release](https://github.com/AsterionXeity/Leyline/releases) and run it.
2. On first launch Leyline asks to install its virtual audio driver (one administrator approval). When an update ships a newer driver, Leyline asks again to update it. Settings → **Leyline Virtual Audio Driver** can install or remove it at any time.

The driver is currently **test-signed**, so Windows only loads it with test signing on. Before installing the driver, run this in an administrator terminal and reboot:

```
bcdedit /set testsigning on
```

## Building

- **App**: `dotnet build src/Leyline/Leyline.csproj -c Debug -p:Platform=x64`
- **Driver** (Visual Studio 2022/2026 with the Windows Driver Kit):
  ```
  nuget restore driver/LeylineVAD/packages.config -PackagesDirectory driver/LeylineVAD/packages
  driver/LeylineVAD/Build-Driver.ps1
  driver/LeylineVAD/Package-Driver.ps1
  ```
  The app bundles `driver/LeylineVAD/out/<Configuration>` in its `Driver\` folder. Bump `LeylineDriverVersion` in `driver/LeylineVAD/Directory.Build.props` whenever the driver changes, so installed copies are offered the update.
- **Installer**: GitHub Actions (`.github/workflows/build-release.yml`) builds the driver, the app and `Leyline-Setup.exe` (Inno Setup, `installer/Leyline.iss`) on every push to `main` and every `v*` tag.
