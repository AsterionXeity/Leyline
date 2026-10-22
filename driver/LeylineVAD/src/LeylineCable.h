/*++

Module Name:

    LeylineCable.h

Abstract:

    Leyline virtual cable: a ring buffer that joins a render endpoint to its capture endpoint.
    Whatever the render stream plays is what the capture stream records (like VB-Cable).

    Both sides carry 16-bit stereo PCM at 48 kHz or 96 kHz (see speakerwavtable.h / micinwavtable.h).
    Each side can run at either rate; the cable converts 2:1 when they differ.

--*/

#pragma once

#include <ntddk.h>

#define LEYLINE_CABLE_CHANNELS          2
#define LEYLINE_CABLE_BLOCK_ALIGN       (LEYLINE_CABLE_CHANNELS * sizeof(SHORT))
#define LEYLINE_CABLE_MAX_RATE          96000

// Called from the render stream with the bytes the audio engine just played, at the render stream's rate.
VOID LeylineCableWrite(_In_reads_bytes_(ByteCount) const UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate);

// Called from the capture stream; fills Data at the capture stream's rate with cable audio,
// or silence when nothing is playing into the cable.
VOID LeylineCableRead(_Out_writes_bytes_(ByteCount) UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate);

// Called when a capture stream starts, so it begins near the newest audio instead of stale data.
VOID LeylineCableResetReader();
