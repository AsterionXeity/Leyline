#pragma once
//==============================================================================
// VoidPatchVAD.h
// VoidPatch Virtual Audio WDM/WaveRT Kernel Driver Definitions
//==============================================================================

#define VOIDPATCH_POOL_TAG 'dPoV' // 'VoPd'
#define VOIDPATCH_SAMPLE_RATE 48000
#define VOIDPATCH_CHANNELS 2
#define VOIDPATCH_BITS_PER_SAMPLE 32
#define VOIDPATCH_RING_BUFFER_FRAMES (48000 / 5) // 200ms lock-free ring buffer per bus

enum VoidPatchEndpointId
{
    Endpoint_System = 0,
    Endpoint_Game = 1,
    Endpoint_Browser = 2,
    Endpoint_Music = 3,
    Endpoint_VoiceChat = 4,
    Endpoint_StreamMix = 5,
    Endpoint_MicFx = 6,
    Endpoint_Count = 7
};

struct VoidPatchSubdeviceDescriptor
{
    VoidPatchEndpointId Id;
    const wchar_t* WaveSubdeviceName;
    const wchar_t* TopoSubdeviceName;
    const wchar_t* FriendlyBusName;
    bool IsCaptureEndpoint;
};

static const VoidPatchSubdeviceDescriptor g_VoidPatchEndpoints[Endpoint_Count] =
{
    { Endpoint_System,    L"WaveSystem",  L"TopoSystem",  L"VoidPatch System",     false },
    { Endpoint_Game,      L"WaveGame",    L"TopoGame",    L"VoidPatch Game",       false },
    { Endpoint_Browser,   L"WaveBrowser", L"TopoBrowser", L"VoidPatch Browser",    false },
    { Endpoint_Music,     L"WaveMusic",   L"TopoMusic",   L"VoidPatch Music",      false },
    { Endpoint_VoiceChat, L"WaveChat",    L"TopoChat",    L"VoidPatch Voice Chat", false },
    { Endpoint_StreamMix, L"WaveStream",  L"TopoStream",  L"VoidPatch Stream Mix", true  },
    { Endpoint_MicFx,     L"WaveMicFx",   L"TopoMicFx",   L"VoidPatch Mic FX",     true  }
};
