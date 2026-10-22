/*++

Module Name:

    LeylineCable.cpp

Abstract:

    Ring buffers joining each Leyline cable's render endpoint to its capture endpoint, plus each
    cable's connection state.

    A cable's buffer holds stereo 16-bit frames at the render stream's rate. Positions are 64-bit
    frame counters that only grow; the buffer index is the counter modulo the buffer length. Both
    streams run from SysVAD's position timer at DISPATCH_LEVEL, so a spin lock per cable guards its
    state and the buffer is non-paged.

    Supported rates are 48 kHz and 96 kHz, so a rate mismatch is always exactly 2:1:
      - reader at half the cable rate: average each pair of frames,
      - reader at double the cable rate: emit an interpolated frame, then the frame itself.

--*/

#include "LeylineCable.h"
#include "LeylineShared.h"

#define LEYLINE_CABLE_POOLTAG           'CyeL'

typedef struct _LEYLINE_FRAME
{
    SHORT Sample[LEYLINE_CABLE_CHANNELS];
} LEYLINE_FRAME;

// Half a second at the highest rate: well beyond the maximum reader lag.
#define LEYLINE_CABLE_FRAMES            (LEYLINE_CABLE_MAX_RATE / 2)

// Reader lag, in milliseconds. A reader further behind than the maximum is moved up to the target
// so latency cannot build up (e.g. after the capture side stalls).
#define LEYLINE_CABLE_TARGET_LAG_MS     20
#define LEYLINE_CABLE_MAX_LAG_MS        200

// Topology filters per cable that report a jack (render + capture).
#define LEYLINE_MAX_JACKS_PER_CABLE     2

typedef struct _LEYLINE_JACK
{
    PVOID                    Context;
    PFN_LEYLINE_JACK_CHANGED Callback;
} LEYLINE_JACK;

typedef struct _LEYLINE_CABLE
{
    KSPIN_LOCK      Lock;
    LEYLINE_FRAME*  Frames;             // LEYLINE_CABLE_FRAMES frames
    ULONGLONG       WritePos;           // frames written, at Rate
    ULONGLONG       ReadPos;            // frames consumed, at Rate
    ULONG           Rate;               // rate of the frames in the buffer (the render side's rate)
    LEYLINE_FRAME   LastReadFrame;      // previous frame handed out, for 2x interpolation
    LEYLINE_FRAME   PendingFrame;       // 2x upsampling: second output frame not yet handed out
    BOOLEAN         HasPendingFrame;

    volatile LONG   Connected;
    LEYLINE_JACK    Jacks[LEYLINE_MAX_JACKS_PER_CABLE];   // guarded by g_JackMutex
} LEYLINE_CABLE;

static LEYLINE_CABLE g_Cables[LEYLINE_CABLE_COUNT];
static FAST_MUTEX    g_JackMutex;

static LEYLINE_CABLE* GetCable(_In_ ULONG CableId)
{
    if (CableId == LEYLINE_NO_CABLE || CableId > LEYLINE_CABLE_COUNT)
    {
        return NULL;
    }
    LEYLINE_CABLE* cable = &g_Cables[CableId - 1];
    return (cable->Frames != NULL) ? cable : NULL;
}

static ULONGLONG FramesForMs(ULONG Rate, ULONG Ms)
{
    return (ULONGLONG)Rate * Ms / 1000;
}

// Caller holds the cable lock.
static VOID ResetReaderLocked(_Inout_ LEYLINE_CABLE* Cable)
{
    ULONGLONG target = FramesForMs(Cable->Rate, LEYLINE_CABLE_TARGET_LAG_MS);
    Cable->ReadPos = (Cable->WritePos > target) ? Cable->WritePos - target : 0;
    RtlZeroMemory(&Cable->LastReadFrame, sizeof(Cable->LastReadFrame));
    Cable->HasPendingFrame = FALSE;
}

// Caller holds the cable lock and has checked that a frame is available.
static LEYLINE_FRAME TakeFrameLocked(_Inout_ LEYLINE_CABLE* Cable)
{
    LEYLINE_FRAME frame = Cable->Frames[Cable->ReadPos % LEYLINE_CABLE_FRAMES];
    Cable->ReadPos++;
    return frame;
}

#pragma code_seg("PAGE")
NTSTATUS LeylineCablesInitialize()
{
    PAGED_CODE();

    ExInitializeFastMutex(&g_JackMutex);

    for (ULONG i = 0; i < LEYLINE_CABLE_COUNT; i++)
    {
        LEYLINE_CABLE* cable = &g_Cables[i];
        RtlZeroMemory(cable, sizeof(*cable));
        KeInitializeSpinLock(&cable->Lock);
        cable->Rate = 48000;
        cable->Frames = (LEYLINE_FRAME*)ExAllocatePool2(POOL_FLAG_NON_PAGED, LEYLINE_CABLE_FRAMES * sizeof(LEYLINE_FRAME), LEYLINE_CABLE_POOLTAG);
        if (cable->Frames == NULL)
        {
            LeylineCablesCleanup();
            return STATUS_INSUFFICIENT_RESOURCES;
        }
    }

    return STATUS_SUCCESS;
}

#pragma code_seg("PAGE")
VOID LeylineCablesCleanup()
{
    PAGED_CODE();

    for (ULONG i = 0; i < LEYLINE_CABLE_COUNT; i++)
    {
        if (g_Cables[i].Frames != NULL)
        {
            ExFreePoolWithTag(g_Cables[i].Frames, LEYLINE_CABLE_POOLTAG);
            g_Cables[i].Frames = NULL;
        }
    }
}

#pragma code_seg()
VOID LeylineCableWrite(_In_ ULONG CableId, _In_reads_bytes_(ByteCount) const UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate)
{
    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable == NULL)
    {
        return;
    }

    const LEYLINE_FRAME* frames = reinterpret_cast<const LEYLINE_FRAME*>(Data);
    ULONG frameCount = ByteCount / LEYLINE_CABLE_BLOCK_ALIGN;

    KIRQL oldIrql;
    KeAcquireSpinLock(&cable->Lock, &oldIrql);

    if (SampleRate != cable->Rate)
    {
        // The render side switched rate (48 <-> 96 kHz): old frames are at the wrong rate, drop them.
        cable->Rate = SampleRate;
        cable->ReadPos = cable->WritePos;
        RtlZeroMemory(&cable->LastReadFrame, sizeof(cable->LastReadFrame));
        cable->HasPendingFrame = FALSE;
    }

    while (frameCount > 0)
    {
        ULONG index = (ULONG)(cable->WritePos % LEYLINE_CABLE_FRAMES);
        ULONG run = min(frameCount, LEYLINE_CABLE_FRAMES - index);
        RtlCopyMemory(&cable->Frames[index], frames, run * sizeof(LEYLINE_FRAME));
        cable->WritePos += run;
        frames += run;
        frameCount -= run;
    }

    KeReleaseSpinLock(&cable->Lock, oldIrql);
}

#pragma code_seg()
VOID LeylineCableRead(_In_ ULONG CableId, _Out_writes_bytes_(ByteCount) UCHAR* Data, _In_ ULONG ByteCount, _In_ ULONG SampleRate)
{
    LEYLINE_FRAME* out = reinterpret_cast<LEYLINE_FRAME*>(Data);
    ULONG outFrames = ByteCount / LEYLINE_CABLE_BLOCK_ALIGN;
    ULONG i = 0;

    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable != NULL)
    {
        KIRQL oldIrql;
        KeAcquireSpinLock(&cable->Lock, &oldIrql);

        // Keep latency bounded: if the reader fell too far behind, jump to the target lag.
        if (cable->WritePos - cable->ReadPos > FramesForMs(cable->Rate, LEYLINE_CABLE_MAX_LAG_MS))
        {
            ResetReaderLocked(cable);
        }

        for (; i < outFrames; i++)
        {
            ULONGLONG available = cable->WritePos - cable->ReadPos;

            if (SampleRate == cable->Rate)
            {
                if (available < 1) break;
                out[i] = TakeFrameLocked(cable);
            }
            else if (SampleRate * 2 == cable->Rate)
            {
                // 96 kHz cable -> 48 kHz reader: average each pair of frames.
                if (available < 2) break;
                LEYLINE_FRAME a = TakeFrameLocked(cable);
                LEYLINE_FRAME b = TakeFrameLocked(cable);
                for (ULONG ch = 0; ch < LEYLINE_CABLE_CHANNELS; ch++)
                {
                    out[i].Sample[ch] = (SHORT)(((LONG)a.Sample[ch] + (LONG)b.Sample[ch]) / 2);
                }
            }
            else if (SampleRate == cable->Rate * 2)
            {
                // 48 kHz cable -> 96 kHz reader: midpoint between the previous and next frame, then the frame.
                // The second half is kept pending when the block ends between the two.
                if (cable->HasPendingFrame)
                {
                    out[i] = cable->PendingFrame;
                    cable->HasPendingFrame = FALSE;
                }
                else
                {
                    if (available < 1) break;
                    LEYLINE_FRAME next = TakeFrameLocked(cable);
                    for (ULONG ch = 0; ch < LEYLINE_CABLE_CHANNELS; ch++)
                    {
                        out[i].Sample[ch] = (SHORT)(((LONG)cable->LastReadFrame.Sample[ch] + (LONG)next.Sample[ch]) / 2);
                    }
                    cable->PendingFrame = next;
                    cable->HasPendingFrame = TRUE;
                }
            }
            else
            {
                // Not a supported rate pair; record silence rather than garbage.
                break;
            }

            cable->LastReadFrame = out[i];
        }

        KeReleaseSpinLock(&cable->Lock, oldIrql);
    }

    // Nothing (more) is playing into the cable: record silence for the rest.
    if (i < outFrames)
    {
        RtlZeroMemory(&out[i], (outFrames - i) * sizeof(LEYLINE_FRAME));
    }
}

#pragma code_seg()
VOID LeylineCableResetReader(_In_ ULONG CableId)
{
    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable == NULL)
    {
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&cable->Lock, &oldIrql);
    ResetReaderLocked(cable);
    KeReleaseSpinLock(&cable->Lock, oldIrql);
}

#pragma code_seg()
BOOLEAN LeylineCableIsConnected(_In_ ULONG CableId)
{
    LEYLINE_CABLE* cable = GetCable(CableId);
    return (cable != NULL && ReadNoFence(&cable->Connected) != 0) ? TRUE : FALSE;
}

#pragma code_seg("PAGE")
VOID LeylineCableSetConnected(_In_ ULONG CableId, _In_ BOOLEAN Connected)
{
    PAGED_CODE();

    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable == NULL)
    {
        return;
    }

    if (InterlockedExchange(&cable->Connected, Connected ? 1 : 0) == (Connected ? 1 : 0))
    {
        return;     // no change
    }

    // Tell Windows to re-read both jacks. Holding the mutex keeps registered miniports alive
    // (they unregister under the same mutex before being destroyed).
    ExAcquireFastMutex(&g_JackMutex);
    for (ULONG j = 0; j < LEYLINE_MAX_JACKS_PER_CABLE; j++)
    {
        if (cable->Jacks[j].Callback != NULL)
        {
            cable->Jacks[j].Callback(cable->Jacks[j].Context);
        }
    }
    ExReleaseFastMutex(&g_JackMutex);
}

#pragma code_seg("PAGE")
VOID LeylineCableRegisterJack(_In_ ULONG CableId, _In_ PVOID Context, _In_ PFN_LEYLINE_JACK_CHANGED Callback)
{
    PAGED_CODE();

    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable == NULL)
    {
        return;
    }

    ExAcquireFastMutex(&g_JackMutex);
    for (ULONG j = 0; j < LEYLINE_MAX_JACKS_PER_CABLE; j++)
    {
        if (cable->Jacks[j].Context == NULL)
        {
            cable->Jacks[j].Context = Context;
            cable->Jacks[j].Callback = Callback;
            break;
        }
    }
    ExReleaseFastMutex(&g_JackMutex);
}

#pragma code_seg("PAGE")
VOID LeylineCableUnregisterJack(_In_ ULONG CableId, _In_ PVOID Context)
{
    PAGED_CODE();

    LEYLINE_CABLE* cable = GetCable(CableId);
    if (cable == NULL)
    {
        return;
    }

    ExAcquireFastMutex(&g_JackMutex);
    for (ULONG j = 0; j < LEYLINE_MAX_JACKS_PER_CABLE; j++)
    {
        if (cable->Jacks[j].Context == Context)
        {
            cable->Jacks[j].Context = NULL;
            cable->Jacks[j].Callback = NULL;
        }
    }
    ExReleaseFastMutex(&g_JackMutex);
}
