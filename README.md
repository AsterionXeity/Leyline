# VoidPatch — Virtual Audio Router, Dual-Bus Mixer, Soundboard & VST Host

**VoidPatch** is a native Windows 10 & Windows 11 studio audio routing and mixing application inspired by Elgato Wave Link.

## Key Features

1. **Dual-Bus Studio Mixing (Monitor Mix & Stream Mix)**
   - Every input channel strip has independent **🎧 Monitor Mix** (personal headphones/speakers) and **📡 Stream Mix** (virtual broadcast output) faders, mute buttons, fader-link toggle, and real-time stereo L/R peak meters.
   - Built-in **Stream Mix WAV Recorder** (`My Documents\VoidPatch Recordings`).

2. **Virtual Audio Device Routing & Direct App Process Capture (Windows 10 & 11)**
   - **App Router Tab**: Uses Windows `IAudioPolicyConfigFactory` COM interop to route any active application (`Chrome`, `Spotify`, `Discord`, `Games`, etc.) to any Virtual Audio Bus on the fly without leaving VoidPatch.
   - **Direct Process Capture**: Uses Windows 10 (2004+) / Windows 11 `ActivateAudioInterfaceAsync` (`VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`) to capture individual `.exe` application audio streams directly into a mixer strip without changing their Windows playback device.

3. **VoidPatch Virtual Audio Driver & Endpoint Provisioner**
   - Includes the **VoidPatch Virtual Audio Driver Manager** (under the **⚡ VST & VIRTUAL DRIVER** tab and `driver\VoidPatchVAD\`) which provisions and brands your Windows CoreAudio virtual buses as:
     - `VoidPatch System (VoidPatch Virtual Audio)`
     - `VoidPatch Game (VoidPatch Virtual Audio)`
     - `VoidPatch Browser (VoidPatch Virtual Audio)`
     - `VoidPatch Music (VoidPatch Virtual Audio)`
     - `VoidPatch Voice Chat (VoidPatch Virtual Audio)`
     - `VoidPatch Stream Mix (VoidPatch Virtual Audio)`
     - `VoidPatch Mic FX (VoidPatch Virtual Audio)`
   - Includes 1-click backup & restore to original endpoint names at any time.
   - Complete WDM/WaveRT kernel driver source & INF package included in `G:\VoidPatch\driver\VoidPatchVAD\`.

4. **VST2 / VST3 Hosting + Built-in Zero-Latency Studio DSP**
   - **Built-in Studio DSP Rack** on every channel strip & master bus:
     - **VoidGate**: Noise Gate (Threshold, Release)
     - **VoidEQ**: 3-Band Studio Equalizer (100 Hz Low Shelf, 1.1 kHz Mid Peak, 8 kHz High Shelf)
     - **VoidComp**: Studio Compressor (Threshold, Ratio, Makeup Gain)
     - **VoidLimiter**: Brickwall Output Ceiling Limiter
   - **External 64-bit VST3 (`.vst3`) & VST2 (`.dll`) Host**:
     - Automatically scans `C:\Program Files\Common Files\VST3` and VST2 folders.
     - Hosts native plugin GUI editors (`IPlugView` / `effEditOpen`) plus a parameter slider rack.

5. **Built-in Soundboard with Global Hotkeys**
   - Pre-loaded with 8 synthesized studio sound effects (`MLG Airhorn`, `Censor Beep`, `Sub Bass Drop`, `Ba-Dum Tss`, `Victory Fanfare`, `Sci-Fi Laser`, `8-Bit Coin`, `Dramatic Hit`) and supports `.wav`, `.mp3`, `.ogg`, `.flac`, and `.m4a`.
   - Per-pad volume, playback mode (`Stop Toggle`, `One-Shot`, `Overlap`, `Loop`), independent Monitor/Stream routing, and system-wide **Global Hotkeys** (`Ctrl+Shift+1..8`, `Ctrl+Shift+0` Panic Stop).

## Running & Installing

- **Launch App**: `G:\VoidPatch\publish\VoidPatch.exe` (or via the **VoidPatch** shortcut on your Desktop / Start Menu)
- **Re-run Installer**: `powershell -ExecutionPolicy Bypass -File G:\VoidPatch\Install-VoidPatch.ps1`
- **Brand Virtual Audio Driver Endpoints**: Click **⚡ Brand & Activate VoidPatch Audio Endpoints (UAC)** inside the **VST & VIRTUAL DRIVER** tab, or run `G:\VoidPatch\driver\VoidPatchVAD\Install-VoidPatchDriver.ps1`.
