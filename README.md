# Leyline — Virtual Audio Router, Dual-Bus Mixer, Soundboard & VST Host

**Leyline** is a native Windows 10 & Windows 11 studio audio routing and mixing application inspired by Elgato Wave Link.

## Key Features

1. **Dual-Bus Studio Mixing (Monitor Mix & Stream Mix)**
   - Every input channel strip has independent **🎧 Monitor Mix** (personal headphones/speakers) and **📡 Stream Mix** (virtual broadcast output) faders, mute buttons, fader-link toggle, and real-time stereo L/R peak meters.
   - Built-in **Stream Mix WAV Recorder** (`My Documents\Leyline Recordings`).

2. **Virtual Audio Device Routing (Primary) & Process Loopback Fallback (Windows 10 & 11)**
   - **Automatic Virtual Endpoint Routing**: Choosing an application (`Chrome`, `Spotify`, `Discord`, `Games`, `OBS`, etc.) in Leyline's menus automatically binds the channel to its corresponding `Leyline ... (Leyline Virtual Audio)` playback device and routes the application's Windows output directly to that virtual endpoint via `IAudioPolicyConfigFactory` (`PolicyConfigClient.SetApplicationOutputEndpoint`). Removing an app or channel automatically restores it to the Windows Default Output.
   - **Direct Process Capture Fallback**: Retains Windows 10 (2004+) / Windows 11 `ActivateAudioInterfaceAsync` (`VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`) as a zero-driver fallback when the virtual audio endpoints are not yet installed.

3. **Leyline Virtual Audio Driver & Endpoint Provisioner**
   - Includes the **Leyline Virtual Audio Driver Manager** (under the **⚡ VST & VIRTUAL DRIVER** tab and `driver\LeylineVAD\`) which provisions and brands your Windows CoreAudio virtual buses as:
     - `Leyline System (Leyline Virtual Audio)`
     - `Leyline Game (Leyline Virtual Audio)`
     - `Leyline Browser (Leyline Virtual Audio)`
     - `Leyline Music (Leyline Virtual Audio)`
     - `Leyline Voice Chat (Leyline Virtual Audio)`
     - `Leyline Stream Mix (Leyline Virtual Audio)`
     - `Leyline Mic FX (Leyline Virtual Audio)`
   - Includes 1-click backup & restore to original endpoint names at any time.
   - Complete WDM/WaveRT kernel driver source & INF package included in `G:\Leyline\driver\LeylineVAD\`.

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

## Running & Installing

- **Launch App**: `G:\Leyline\publish\Leyline.exe` (or via the **Leyline** shortcut on your Desktop / Start Menu)
- **Re-run Installer**: `powershell -ExecutionPolicy Bypass -File G:\Leyline\Install-Leyline.ps1`
- **Brand Virtual Audio Driver Endpoints**: Click **⚡ Brand & Activate Leyline Audio Endpoints (UAC)** inside the **VST & VIRTUAL DRIVER** tab, or run `G:\Leyline\driver\LeylineVAD\Install-LeylineDriver.ps1`.
