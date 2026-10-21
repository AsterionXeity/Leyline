#pragma once
//==============================================================================
// LeylineVAD.h
// Leyline Virtual Audio WDM/WaveRT Kernel Driver Definitions
//==============================================================================

#define LEYLINE_POOL_TAG 'dPoV' // 'VoPd'
#define LEYLINE_SAMPLE_RATE 48000
#define LEYLINE_CHANNELS 2
#define LEYLINE_BITS_PER_SAMPLE 32
#define LEYLINE_RING_BUFFER_FRAMES (48000 / 5) // 200ms lock-free ring buffer per bus

enum LeylineEndpointId
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

struct LeylineSubdeviceDescriptor
{
    LeylineEndpointId Id;
    const wchar_t* WaveSubdeviceName;
    const wchar_t* TopoSubdeviceName;
    const wchar_t* FriendlyBusName;
    bool IsCaptureEndpoint;
};

static const LeylineSubdeviceDescriptor g_LeylineEndpoints[Endpoint_Count] =
{
    { Endpoint_System,    L"WaveSystem",  L"TopoSystem",  L"Leyline System",     false },
    { Endpoint_Game,      L"WaveGame",    L"TopoGame",    L"Leyline Game",       false },
    { Endpoint_Browser,   L"WaveBrowser", L"TopoBrowser", L"Leyline Browser",    false },
    { Endpoint_Music,     L"WaveMusic",   L"TopoMusic",   L"Leyline Music",      false },
    { Endpoint_VoiceChat, L"WaveChat",    L"TopoChat",    L"Leyline Voice Chat", false },
    { Endpoint_StreamMix, L"WaveStream",  L"TopoStream",  L"Leyline Stream Mix", true  },
    { Endpoint_MicFx,     L"WaveMicFx",   L"TopoMicFx",   L"Leyline Mic FX",     true  }
};
