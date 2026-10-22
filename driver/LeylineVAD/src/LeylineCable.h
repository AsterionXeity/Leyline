/*++

Module Name:

    LeylineCable.h

Abstract:

    Leyline virtual cables. Each cable joins a render endpoint to a capture endpoint through a ring
    buffer: whatever the render stream plays is what the capture stream records (like VB-Cable).

    Both sides carry 16-bit stereo PCM at 48 kHz or 96 kHz (see hdmiwavtable.h / micinwavtable.h).
    Each side can run at either rate; the cable converts 2:1 when they differ.

    Cables are identified by a 1-based id (ENDPOINT_MINIPAIR::LeylineCableId); 0 means "not a cable".
    The cable list itself is LEYLINE_CABLES in LeylineShared.h.

--*/

#pragma once

#include <ntddk.h>

#define LEYLINE_CABLE_CHANNELS          2
#define LEYLINE_CABLE_BLOCK_ALIGN       (LEYLINE_CABLE_CHANNELS * sizeof(SHORT))
#define LEYLINE_CABLE_MAX_RATE          96000

#define LEYLINE_NO_CABLE                0

// Driver load/unload: allocate and free every cable's buffer.
NTSTATUS LeylineCablesInitialize();
VOID LeylineCablesCleanup();

// Called from a render stream with the bytes the audio engine just played, at the stream's rate.
VOID LeylineCableWrite(_In_ ULONG CableId, _In_reads_bytes_(ByteCount) const UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate);

// Called from a capture stream; fills Data at the stream's rate with cable audio,
// or silence when nothing is playing into the cable.
VOID LeylineCableRead(_In_ ULONG CableId, _Out_writes_bytes_(ByteCount) UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate);

// Called when a capture stream starts, so it begins near the newest audio instead of stale data.
VOID LeylineCableResetReader(_In_ ULONG CableId);

//
// Connection state (KSPROPERTY_LEYLINE_CABLE_CONNECTED). A disconnected cable reports its jacks as
// unplugged, so Windows shows both endpoints as "Disconnected".
//
BOOLEAN LeylineCableIsConnected(_In_ ULONG CableId);

// Sets the state and, when it changes, notifies every registered jack so Windows re-reads it.
VOID LeylineCableSetConnected(_In_ ULONG CableId, _In_ BOOLEAN Connected);

// Topology miniports register their jack so connection changes reach Windows (jack-info-change event).
typedef VOID (*PFN_LEYLINE_JACK_CHANGED)(_In_ PVOID Context);
VOID LeylineCableRegisterJack(_In_ ULONG CableId, _In_ PVOID Context, _In_ PFN_LEYLINE_JACK_CHANGED Callback);
VOID LeylineCableUnregisterJack(_In_ ULONG CableId, _In_ PVOID Context);
