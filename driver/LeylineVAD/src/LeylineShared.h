/*++

Module Name:

    LeylineShared.h

Abstract:

    Definitions shared by the Leyline driver and the Leyline app.

    The cable list is defined once here (LEYLINE_CABLES). Keep it in sync with:
      - the per-cable sections of TabletAudioSample\LeylineVAD.inx (filter names and display names), and
      - the cable list in the Leyline app.

--*/

#pragma once

//
// X(Id, DisplayName, NameGuid)
//   Id          - identifier used in the filter (KS interface) names: WaveRender<Id>, TopoRender<Id>, WaveCapture<Id>, TopoCapture<Id>
//   DisplayName - endpoint name shown by Windows, e.g. "System (Leyline Virtual Audio)" (used by the INF only)
//   NameGuid    - MediaCategories GUID whose "Name" the INF sets to DisplayName
//
// Update-InfCables.ps1 regenerates the per-cable INF entries from this list (Build-Driver.ps1 runs it).
//
#define LEYLINE_CABLES(X)                                                                                           \
    X(System,     "System",     0x6759668c, 0xb4fe, 0x4279, 0x90, 0x6a, 0x60, 0xa8, 0x46, 0x42, 0xe1, 0x58)        \
    X(Game,       "Game",       0x6827af89, 0x6539, 0x4b18, 0x84, 0x64, 0x77, 0x2a, 0x6a, 0xf3, 0xd0, 0x3f)        \
    X(Browser,    "Browser",    0x1d126417, 0x2c4c, 0x4601, 0x98, 0xa6, 0x5f, 0xc1, 0x2a, 0x73, 0xd8, 0x5d)        \
    X(Music,      "Music",      0xaf536979, 0x3d71, 0x454a, 0xa6, 0xd1, 0x6c, 0xeb, 0xd2, 0x31, 0xac, 0x5a)        \
    X(VoiceChat,  "Voice Chat", 0x49bc5886, 0x398a, 0x48fa, 0xb8, 0x82, 0xfe, 0xdd, 0x3c, 0x85, 0xcf, 0x46)        \
    X(StreamMix,  "Stream Mix", 0xd5c6af84, 0x48b0, 0x4b37, 0x90, 0x25, 0x1e, 0x85, 0x0c, 0xe7, 0xaa, 0x92)        \
    X(Microphone, "Microphone", 0xc4f743eb, 0x3521, 0x4c8a, 0x99, 0xc8, 0x5c, 0x05, 0xa9, 0xa0, 0x65, 0x19)

#define LEYLINE_COUNT_CABLE(Id, ...) +1
#define LEYLINE_CABLE_COUNT (0 LEYLINE_CABLES(LEYLINE_COUNT_CABLE))

//
// Private property set, supported on every cable's topology filters (render and capture).
//
//   KSPROPERTY_LEYLINE_CABLE_CONNECTED (ULONG, GET/SET)
//     0 = disconnected: both endpoints of the cable show as "Disconnected" (unplugged) in Windows.
//     1 = connected:    both endpoints are available.
//   Cables start disconnected; the Leyline app connects a cable when a channel uses it.
//
// {FCEF5F84-A2A9-45D3-9D90-AEEE9E67A1B0}
#define STATIC_KSPROPSETID_Leyline \
    0xfcef5f84, 0xa2a9, 0x45d3, 0x9d, 0x90, 0xae, 0xee, 0x9e, 0x67, 0xa1, 0xb0
#ifdef DEFINE_GUIDSTRUCT    // KS headers present
DEFINE_GUIDSTRUCT("FCEF5F84-A2A9-45D3-9D90-AEEE9E67A1B0", KSPROPSETID_Leyline);
#define KSPROPSETID_Leyline DEFINE_GUIDNAMED(KSPROPSETID_Leyline)
#endif

typedef enum
{
    KSPROPERTY_LEYLINE_CABLE_CONNECTED = 1,
} KSPROPERTY_LEYLINE;
