/*++

Module Name:

    leylineendpoints.h

Abstract:

    Builds one render + capture endpoint pair per Leyline cable (LEYLINE_CABLES in LeylineShared.h).

    Each cable is a copy of the HDMI render pair (no offload pin) and the mic-in capture pair with:
      - its own filter names: WaveRender<Id>, TopoRender<Id>, WaveCapture<Id>, TopoCapture<Id>
        (registered as KS interfaces by LeylineVAD.inx),
      - its own name GUID on the jack pin, so Windows names the endpoints after the cable
        (the INF maps each GUID to a name under MediaCategories),
      - Leyline topology automation tables: jack state follows the cable's connection, the
        connection can be set through KSPROPSETID_Leyline, and changes fire a jack-info-change event.

    Included only by adapter.cpp (after minipairs.h, whose endpoint tables are static).

--*/

#ifndef _LEYLINE_ENDPOINTS_H_
#define _LEYLINE_ENDPOINTS_H_

#include "LeylineShared.h"
#include "LeylineCable.h"

//=============================================================================
// Topology automation tables
//=============================================================================

static
PCPROPERTY_ITEM LeylineRenderTopoProperties[] =
{
    { &KSPROPSETID_Jack,    KSPROPERTY_JACK_DESCRIPTION,        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,                         PropertyHandler_LeylineRenderTopoFilter },
    { &KSPROPSETID_Jack,    KSPROPERTY_JACK_DESCRIPTION2,       KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,                         PropertyHandler_LeylineRenderTopoFilter },
    { &KSPROPSETID_Leyline, KSPROPERTY_LEYLINE_CABLE_CONNECTED, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT,  PropertyHandler_LeylineRenderTopoFilter },
};

static
PCEVENT_ITEM LeylineRenderTopoEvents[] =
{
    { &KSEVENTSETID_PinCapsChange, KSEVENT_PINCAPS_JACKINFOCHANGE, KSEVENT_TYPE_ENABLE | KSEVENT_TYPE_BASICSUPPORT, EventHandler_LeylineRenderTopoFilter },
};

DEFINE_PCAUTOMATION_TABLE_PROP_EVENT(AutomationLeylineRenderTopo, LeylineRenderTopoProperties, LeylineRenderTopoEvents);

static
PCPROPERTY_ITEM LeylineCaptureTopoProperties[] =
{
    { &KSPROPSETID_Jack,    KSPROPERTY_JACK_DESCRIPTION,        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,                         PropertyHandler_LeylineCaptureTopoFilter },
    { &KSPROPSETID_Jack,    KSPROPERTY_JACK_DESCRIPTION2,       KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,                         PropertyHandler_LeylineCaptureTopoFilter },
    { &KSPROPSETID_Leyline, KSPROPERTY_LEYLINE_CABLE_CONNECTED, KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT,  PropertyHandler_LeylineCaptureTopoFilter },
};

static
PCEVENT_ITEM LeylineCaptureTopoEvents[] =
{
    { &KSEVENTSETID_PinCapsChange, KSEVENT_PINCAPS_JACKINFOCHANGE, KSEVENT_TYPE_ENABLE | KSEVENT_TYPE_BASICSUPPORT, EventHandler_LeylineCaptureTopoFilter },
};

DEFINE_PCAUTOMATION_TABLE_PROP_EVENT(AutomationLeylineCaptureTopo, LeylineCaptureTopoProperties, LeylineCaptureTopoEvents);

//=============================================================================
// Per-cable names
//=============================================================================

#define LEYLINE_WIDE2(x)    L##x
#define LEYLINE_WIDE(x)     LEYLINE_WIDE2(x)

typedef struct _LEYLINE_CABLE_FILTER_NAMES
{
    PCWSTR WaveRender;
    PCWSTR TopoRender;
    PCWSTR WaveCapture;
    PCWSTR TopoCapture;
} LEYLINE_CABLE_FILTER_NAMES;

#define LEYLINE_FILTER_NAMES_ENTRY(Id, ...) \
    { L"WaveRender" LEYLINE_WIDE(#Id), L"TopoRender" LEYLINE_WIDE(#Id), L"WaveCapture" LEYLINE_WIDE(#Id), L"TopoCapture" LEYLINE_WIDE(#Id) },

static const LEYLINE_CABLE_FILTER_NAMES g_LeylineCableFilterNames[LEYLINE_CABLE_COUNT] =
{
    LEYLINE_CABLES(LEYLINE_FILTER_NAMES_ENTRY)
};

#define LEYLINE_NAME_GUID_ENTRY(Id, DisplayName, l, w1, w2, b1, b2, b3, b4, b5, b6, b7, b8) \
    { l, w1, w2, { b1, b2, b3, b4, b5, b6, b7, b8 } },

static const GUID g_LeylineCableNameGuids[LEYLINE_CABLE_COUNT] =
{
    LEYLINE_CABLES(LEYLINE_NAME_GUID_ENTRY)
};

//=============================================================================
// Endpoint tables (built once by LeylineBuildEndpoints)
//=============================================================================

static ENDPOINT_MINIPAIR    g_LeylineRenderPairs[LEYLINE_CABLE_COUNT];
static ENDPOINT_MINIPAIR    g_LeylineCapturePairs[LEYLINE_CABLE_COUNT];
static PCFILTER_DESCRIPTOR  g_LeylineRenderTopoDescriptors[LEYLINE_CABLE_COUNT];
static PCFILTER_DESCRIPTOR  g_LeylineCaptureTopoDescriptors[LEYLINE_CABLE_COUNT];
static PCPIN_DESCRIPTOR     g_LeylineRenderTopoPins[LEYLINE_CABLE_COUNT][SIZEOF_ARRAY(HdmiTopoMiniportPins)];
static PCPIN_DESCRIPTOR     g_LeylineCaptureTopoPins[LEYLINE_CABLE_COUNT][SIZEOF_ARRAY(MicInTopoMiniportPins)];

static PENDPOINT_MINIPAIR   g_LeylineRenderEndpoints[LEYLINE_CABLE_COUNT];
static PENDPOINT_MINIPAIR   g_LeylineCaptureEndpoints[LEYLINE_CABLE_COUNT];
static BOOLEAN              g_LeylineEndpointsBuilt = FALSE;

#pragma code_seg("PAGE")
static
VOID
LeylineBuildEndpoints()
{
    PAGED_CODE();

    if (g_LeylineEndpointsBuilt)
    {
        return;
    }

    for (ULONG i = 0; i < LEYLINE_CABLE_COUNT; i++)
    {
        const LEYLINE_CABLE_FILTER_NAMES& names = g_LeylineCableFilterNames[i];

        // Render side: HDMI pair, jack on the line-out pin.
        RtlCopyMemory(g_LeylineRenderTopoPins[i], HdmiTopoMiniportPins, sizeof(HdmiTopoMiniportPins));
        g_LeylineRenderTopoPins[i][KSPIN_TOPO_LINEOUT_DEST].KsPinDescriptor.Name = &g_LeylineCableNameGuids[i];

        g_LeylineRenderTopoDescriptors[i]                 = HdmiTopoMiniportFilterDescriptor;
        g_LeylineRenderTopoDescriptors[i].AutomationTable = &AutomationLeylineRenderTopo;
        g_LeylineRenderTopoDescriptors[i].Pins            = g_LeylineRenderTopoPins[i];

        g_LeylineRenderPairs[i]                  = HdmiMiniports;
        g_LeylineRenderPairs[i].TopoName         = const_cast<PWSTR>(names.TopoRender);
        g_LeylineRenderPairs[i].TemplateTopoName = NULL;
        g_LeylineRenderPairs[i].TopoDescriptor   = &g_LeylineRenderTopoDescriptors[i];
        g_LeylineRenderPairs[i].WaveName         = const_cast<PWSTR>(names.WaveRender);
        g_LeylineRenderPairs[i].TemplateWaveName = NULL;
        g_LeylineRenderPairs[i].LeylineCableId   = i + 1;
        g_LeylineRenderEndpoints[i]              = &g_LeylineRenderPairs[i];

        // Capture side: mic-in pair, jack on the mic elements pin.
        RtlCopyMemory(g_LeylineCaptureTopoPins[i], MicInTopoMiniportPins, sizeof(MicInTopoMiniportPins));
        g_LeylineCaptureTopoPins[i][KSPIN_TOPO_MIC_ELEMENTS].KsPinDescriptor.Name = &g_LeylineCableNameGuids[i];

        g_LeylineCaptureTopoDescriptors[i]                 = MicInTopoMiniportFilterDescriptor;
        g_LeylineCaptureTopoDescriptors[i].AutomationTable = &AutomationLeylineCaptureTopo;
        g_LeylineCaptureTopoDescriptors[i].Pins            = g_LeylineCaptureTopoPins[i];

        g_LeylineCapturePairs[i]                  = MicInMiniports;
        g_LeylineCapturePairs[i].TopoName         = const_cast<PWSTR>(names.TopoCapture);
        g_LeylineCapturePairs[i].TemplateTopoName = NULL;
        g_LeylineCapturePairs[i].TopoDescriptor   = &g_LeylineCaptureTopoDescriptors[i];
        g_LeylineCapturePairs[i].WaveName         = const_cast<PWSTR>(names.WaveCapture);
        g_LeylineCapturePairs[i].TemplateWaveName = NULL;
        g_LeylineCapturePairs[i].LeylineCableId   = i + 1;
        g_LeylineCaptureEndpoints[i]              = &g_LeylineCapturePairs[i];
    }

    g_LeylineEndpointsBuilt = TRUE;
}

// Topology + wave miniport per endpoint, render and capture per cable.
#undef  g_MaxMiniports
#define g_MaxMiniports  (LEYLINE_CABLE_COUNT * 2 * 2)

#endif // _LEYLINE_ENDPOINTS_H_
