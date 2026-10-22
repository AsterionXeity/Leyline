/*++

Module Name:

    LeylineCable.cpp

Abstract:

    Ring buffer joining the Leyline render endpoint to its capture endpoint.

    The buffer holds stereo 16-bit frames at the render stream's rate. Positions are 64-bit frame
    counters that only grow; the buffer index is the counter modulo the buffer length. Both streams
    run from SysVAD's position timer at DISPATCH_LEVEL, so a spin lock guards the state and the
    buffer lives in (non-paged) driver data.

    Supported rates are 48 kHz and 96 kHz, so a rate mismatch is always exactly 2:1:
      - reader at half the cable rate: average each pair of frames,
      - reader at double the cable rate: emit an interpolated frame, then the frame itself.

--*/

#include "LeylineCable.h"

typedef struct _LEYLINE_FRAME
{
    SHORT Sample[LEYLINE_CABLE_CHANNELS];
} LEYLINE_FRAME;

// One second at the highest rate: far more than the reader ever lags behind.
#define LEYLINE_CABLE_FRAMES            LEYLINE_CABLE_MAX_RATE

// Reader lag, in milliseconds. A reader further behind than the maximum is moved up to the target
// so latency cannot build up (e.g. after the capture side stalls).
#define LEYLINE_CABLE_TARGET_LAG_MS     20
#define LEYLINE_CABLE_MAX_LAG_MS        200

static LEYLINE_FRAME g_CableFrames[LEYLINE_CABLE_FRAMES];
static ULONGLONG     g_CableWritePos = 0;       // frames written, at g_CableRate
static ULONGLONG     g_CableReadPos = 0;        // frames consumed, at g_CableRate
static ULONG         g_CableRate = 48000;       // rate of the frames in the buffer (the render side's rate)
static LEYLINE_FRAME g_LastReadFrame = {};      // previous frame handed out, for 2x interpolation
static LEYLINE_FRAME g_PendingFrame = {};       // 2x upsampling: second output frame not yet handed out
static BOOLEAN       g_HasPendingFrame = FALSE;
static KSPIN_LOCK    g_CableLock;
static LONG          g_CableLockInitialized = 0;

static VOID EnsureLockInitialized()
{
    if (InterlockedCompareExchange(&g_CableLockInitialized, 1, 0) == 0)
    {
        KeInitializeSpinLock(&g_CableLock);
        InterlockedExchange(&g_CableLockInitialized, 2);
    }
    while (g_CableLockInitialized != 2)
    {
        YieldProcessor();
    }
}

static ULONGLONG FramesForMs(ULONG Rate, ULONG Ms)
{
    return (ULONGLONG)Rate * Ms / 1000;
}

// Caller holds the lock.
static VOID ResetReaderLocked()
{
    ULONGLONG target = FramesForMs(g_CableRate, LEYLINE_CABLE_TARGET_LAG_MS);
    g_CableReadPos = (g_CableWritePos > target) ? g_CableWritePos - target : 0;
    RtlZeroMemory(&g_LastReadFrame, sizeof(g_LastReadFrame));
    g_HasPendingFrame = FALSE;
}

// Caller holds the lock and has checked that a frame is available.
static LEYLINE_FRAME TakeFrameLocked()
{
    LEYLINE_FRAME frame = g_CableFrames[g_CableReadPos % LEYLINE_CABLE_FRAMES];
    g_CableReadPos++;
    return frame;
}

#pragma code_seg()
VOID LeylineCableWrite(_In_reads_bytes_(ByteCount) const UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate)
{
    EnsureLockInitialized();

    const LEYLINE_FRAME* frames = reinterpret_cast<const LEYLINE_FRAME*>(Data);
    ULONG frameCount = ByteCount / LEYLINE_CABLE_BLOCK_ALIGN;

    KIRQL oldIrql;
    KeAcquireSpinLock(&g_CableLock, &oldIrql);

    if (SampleRate != g_CableRate)
    {
        // The render side switched rate (48 <-> 96 kHz): old frames are at the wrong rate, drop them.
        g_CableRate = SampleRate;
        g_CableReadPos = g_CableWritePos;
        RtlZeroMemory(&g_LastReadFrame, sizeof(g_LastReadFrame));
        g_HasPendingFrame = FALSE;
    }

    while (frameCount > 0)
    {
        ULONG index = (ULONG)(g_CableWritePos % LEYLINE_CABLE_FRAMES);
        ULONG run = min(frameCount, LEYLINE_CABLE_FRAMES - index);
        RtlCopyMemory(&g_CableFrames[index], frames, run * sizeof(LEYLINE_FRAME));
        g_CableWritePos += run;
        frames += run;
        frameCount -= run;
    }

    KeReleaseSpinLock(&g_CableLock, oldIrql);
}

#pragma code_seg()
VOID LeylineCableRead(_Out_writes_bytes_(ByteCount) UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate)
{
    EnsureLockInitialized();

    LEYLINE_FRAME* out = reinterpret_cast<LEYLINE_FRAME*>(Data);
    ULONG outFrames = ByteCount / LEYLINE_CABLE_BLOCK_ALIGN;

    KIRQL oldIrql;
    KeAcquireSpinLock(&g_CableLock, &oldIrql);

    // Keep latency bounded: if the reader fell too far behind, jump to the target lag.
    if (g_CableWritePos - g_CableReadPos > FramesForMs(g_CableRate, LEYLINE_CABLE_MAX_LAG_MS))
    {
        ResetReaderLocked();
    }

    ULONG i = 0;
    for (; i < outFrames; i++)
    {
        ULONGLONG available = g_CableWritePos - g_CableReadPos;

        if (SampleRate == g_CableRate)
        {
            if (available < 1) break;
            out[i] = TakeFrameLocked();
        }
        else if (SampleRate * 2 == g_CableRate)
        {
            // 96 kHz cable -> 48 kHz reader: average each pair of frames.
            if (available < 2) break;
            LEYLINE_FRAME a = TakeFrameLocked();
            LEYLINE_FRAME b = TakeFrameLocked();
            for (ULONG ch = 0; ch < LEYLINE_CABLE_CHANNELS; ch++)
            {
                out[i].Sample[ch] = (SHORT)(((LONG)a.Sample[ch] + (LONG)b.Sample[ch]) / 2);
            }
        }
        else if (SampleRate == g_CableRate * 2)
        {
            // 48 kHz cable -> 96 kHz reader: midpoint between the previous and next frame, then the frame.
            // The second half is kept pending when the block ends between the two.
            if (g_HasPendingFrame)
            {
                out[i] = g_PendingFrame;
                g_HasPendingFrame = FALSE;
            }
            else
            {
                if (available < 1) break;
                LEYLINE_FRAME next = TakeFrameLocked();
                for (ULONG ch = 0; ch < LEYLINE_CABLE_CHANNELS; ch++)
                {
                    out[i].Sample[ch] = (SHORT)(((LONG)g_LastReadFrame.Sample[ch] + (LONG)next.Sample[ch]) / 2);
                }
                g_PendingFrame = next;
                g_HasPendingFrame = TRUE;
            }
        }
        else
        {
            // Not a supported rate pair; record silence rather than garbage.
            break;
        }

        g_LastReadFrame = out[i];
    }

    KeReleaseSpinLock(&g_CableLock, oldIrql);

    // Nothing (more) is playing into the cable: record silence for the rest.
    if (i < outFrames)
    {
        RtlZeroMemory(&out[i], (outFrames - i) * sizeof(LEYLINE_FRAME));
    }
}

#pragma code_seg()
VOID LeylineCableResetReader()
{
    EnsureLockInitialized();

    KIRQL oldIrql;
    KeAcquireSpinLock(&g_CableLock, &oldIrql);
    ResetReaderLocked();
    KeReleaseSpinLock(&g_CableLock, oldIrql);
}
