
/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    basetopo.h

Abstract:

    Declaration of topology miniport.

--*/

#ifndef _SYSVAD_BASETOPO_H_
#define _SYSVAD_BASETOPO_H_

//=============================================================================
// Classes
//=============================================================================

///////////////////////////////////////////////////////////////////////////////
// CMiniportTopologySYSVAD
//

class CMiniportTopologySYSVAD
{
  protected:
    PADAPTERCOMMON              m_AdapterCommon;        // Adapter common object.
    PPCFILTER_DESCRIPTOR        m_FilterDescriptor;     // Filter descriptor.
    PPORTEVENTS                 m_PortEvents;           // Event interface.
    USHORT                      m_DeviceMaxChannels;    // Max device channels.
    ULONG                       m_LeylineCableId;       // Leyline: cable this filter belongs to (LEYLINE_NO_CABLE if none).
    ULONG                       m_LeylineJackPin;       // Leyline: pin that reports the cable's jack.

  public:
    CMiniportTopologySYSVAD(
        _In_        PCFILTER_DESCRIPTOR    *FilterDesc,
        _In_        USHORT                  DeviceMaxChannels
        );
    
    ~CMiniportTopologySYSVAD();

    NTSTATUS                    GetDescription
    (   
        _Out_ PPCFILTER_DESCRIPTOR *  Description
    );

    NTSTATUS                    DataRangeIntersection
    (   
        _In_  ULONG             PinId,
        _In_  PKSDATARANGE      ClientDataRange,
        _In_  PKSDATARANGE      MyDataRange,
        _In_  ULONG             OutputBufferLength,
        _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength)
              PVOID             ResultantFormat OPTIONAL,
        _Out_ PULONG            ResultantFormatLength
    );

    NTSTATUS                    Init
    ( 
        _In_  PUNKNOWN          UnknownAdapter,
        _In_  PPORTTOPOLOGY     Port_ 
    );

    // PropertyHandlers.
    NTSTATUS                    PropertyHandlerGeneric
    (
        _In_  PPCPROPERTY_REQUEST PropertyRequest
    );

    NTSTATUS                    PropertyHandlerMuxSource
    (
        _In_  PPCPROPERTY_REQUEST PropertyRequest
    );

    NTSTATUS                    PropertyHandlerDevSpecific
    (
        _In_  PPCPROPERTY_REQUEST PropertyRequest
    );

    VOID                        AddEventToEventList
    (
        _In_  PKSEVENT_ENTRY    EventEntry 
    );
    
    VOID                        GenerateEventList
    (
        _In_opt_    GUID       *Set,
        _In_        ULONG       EventId,
        _In_        BOOL        PinEvent,
        _In_        ULONG       PinId,
        _In_        BOOL        NodeEvent,
        _In_        ULONG       NodeId
    );

    //
    // Leyline cables: a cable's topology filters report its connection state through their jack and
    // fire a jack-info-change event when it changes (see LeylineCable.h).
    //
    VOID                        LeylineAttach
    (
        _In_        ULONG       CableId,
        _In_        ULONG       JackPinId
    );

    ULONG                       GetLeylineCableId() const { return m_LeylineCableId; }
    ULONG                       GetLeylineJackPin() const { return m_LeylineJackPin; }

    static VOID                 LeylineJackChanged
    (
        _In_        PVOID       Context
    );
};

//
// Shared Leyline handlers, called by each topology filter type after casting MajorTarget to its own class.
//   Properties: KSPROPERTY_JACK_DESCRIPTION, KSPROPERTY_JACK_DESCRIPTION2 (IsConnected follows the cable),
//               KSPROPSETID_Leyline / KSPROPERTY_LEYLINE_CABLE_CONNECTED.
//   Events:     KSEVENT_PINCAPS_JACKINFOCHANGE.
//
NTSTATUS LeylineTopoPropertyHandler
(
    _In_        PPCPROPERTY_REQUEST         PropertyRequest,
    _In_        CMiniportTopologySYSVAD    *Miniport,
    _In_        ULONG                       cJackDescriptions,
    _In_reads_(cJackDescriptions) PKSJACK_DESCRIPTION *JackDescriptions
);

NTSTATUS LeylineTopoEventHandler
(
    _In_        PPCEVENT_REQUEST            EventRequest,
    _In_        CMiniportTopologySYSVAD    *Miniport
);

#endif

