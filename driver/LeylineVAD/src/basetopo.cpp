/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    basetopo.cpp

Abstract:

    Implementation of topology miniport. This the base class for 
    all SYSVAD samples

--*/

//4127: conditional expression is constant
#pragma warning (disable : 4127)

#include <sysvad.h>
#include "basetopo.h"
#include "kshelper.h"
#include "LeylineCable.h"
#include "LeylineShared.h"

//=============================================================================
#pragma code_seg("PAGE")
CMiniportTopologySYSVAD::CMiniportTopologySYSVAD
(
    _In_        PCFILTER_DESCRIPTOR    *FilterDesc,
    _In_        USHORT                  DeviceMaxChannels
)
/*++

Routine Description:

  Topology miniport constructor

Arguments:

  FilterDesc - 

  DeviceMaxChannels - 

Return Value:

  void

--*/
{
    PAGED_CODE();

    DPF_ENTER(("[%s]",__FUNCTION__));

    m_AdapterCommon     = NULL;

    ASSERT(FilterDesc != NULL);
    m_FilterDescriptor  = FilterDesc;
    m_PortEvents        = NULL;
    
    ASSERT(DeviceMaxChannels > 0);
    m_DeviceMaxChannels = DeviceMaxChannels;

    m_LeylineCableId    = LEYLINE_NO_CABLE;
    m_LeylineJackPin    = ULONG(-1);
} // CMiniportTopologySYSVAD

CMiniportTopologySYSVAD::~CMiniportTopologySYSVAD
(
    void
)
/*++

Routine Description:

  Topology miniport destructor

Arguments:

Return Value:

  void

--*/
{
    PAGED_CODE();

    DPF_ENTER(("[%s]",__FUNCTION__));

    // Stop receiving connection changes before the event interface goes away.
    if (m_LeylineCableId != LEYLINE_NO_CABLE)
    {
        LeylineCableUnregisterJack(m_LeylineCableId, this);
    }

    SAFE_RELEASE(m_AdapterCommon);
    SAFE_RELEASE(m_PortEvents);
} // ~CMiniportTopologySYSVAD

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS
CMiniportTopologySYSVAD::DataRangeIntersection
( 
    _In_  ULONG                 PinId,
    _In_  PKSDATARANGE          ClientDataRange,
    _In_  PKSDATARANGE          MyDataRange,
    _In_  ULONG                 OutputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ResultantFormatLength)
          PVOID                 ResultantFormat     OPTIONAL,
    _Out_ PULONG                ResultantFormatLength 
)
/*++

Routine Description:

  The DataRangeIntersection function determines the highest 
  quality intersection of two data ranges. Topology miniport does nothing.

Arguments:

  PinId - Pin for which data intersection is being determined. 

  ClientDataRange - Pointer to KSDATARANGE structure which contains the data range 
                    submitted by client in the data range intersection property 
                    request

  MyDataRange - Pin's data range to be compared with client's data range

  OutputBufferLength - Size of the buffer pointed to by the resultant format 
                       parameter

  ResultantFormat - Pointer to value where the resultant format should be 
                    returned

  ResultantFormatLength - Actual length of the resultant format that is placed 
                          at ResultantFormat. This should be less than or equal 
                          to OutputBufferLength

Return Value:

  NT status code.

--*/
{
    UNREFERENCED_PARAMETER(PinId);
    UNREFERENCED_PARAMETER(ClientDataRange);
    UNREFERENCED_PARAMETER(MyDataRange);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(ResultantFormat);
    UNREFERENCED_PARAMETER(ResultantFormatLength);

    PAGED_CODE();

    DPF_ENTER(("[%s]",__FUNCTION__));

    return (STATUS_NOT_IMPLEMENTED);
} // DataRangeIntersection

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS
CMiniportTopologySYSVAD::GetDescription
( 
    _Out_ PPCFILTER_DESCRIPTOR *  OutFilterDescriptor 
)
/*++

Routine Description:

  The GetDescription function gets a pointer to a filter description. 
  It provides a location to deposit a pointer in miniport's description 
  structure. This is the placeholder for the FromNode or ToNode fields in 
  connections which describe connections to the filter's pins

Arguments:

  OutFilterDescriptor - Pointer to the filter description. 

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    ASSERT(OutFilterDescriptor);

    DPF_ENTER(("[%s]",__FUNCTION__));

    *OutFilterDescriptor = m_FilterDescriptor;

    return (STATUS_SUCCESS);
} // GetDescription

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS
CMiniportTopologySYSVAD::Init
( 
    _In_  PUNKNOWN          UnknownAdapter_,
    _In_  PPORTTOPOLOGY     Port_ 
)
/*++

Routine Description:

  Initializes the topology miniport.

Arguments:

  UnknownAdapter -

  Port_ - Pointer to topology port

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();
    
    ASSERT(UnknownAdapter_);
    ASSERT(Port_);

    DPF_ENTER(("[CMiniportTopologySYSVAD::Init]"));

    NTSTATUS    ntStatus;

    ntStatus = 
        UnknownAdapter_->QueryInterface( 
            IID_IAdapterCommon,
            (PVOID *) &m_AdapterCommon);
    
    if (NT_SUCCESS(ntStatus))
    {
        //
        // Get the port event interface.
        //
        ntStatus = Port_->QueryInterface(
            IID_IPortEvents, 
            (PVOID *)&m_PortEvents);
    }

    if (NT_SUCCESS(ntStatus))
    {
        m_AdapterCommon->MixerReset();
    }

    if (!NT_SUCCESS(ntStatus))
    {
        // clean up AdapterCommon
        SAFE_RELEASE(m_AdapterCommon);
        SAFE_RELEASE(m_PortEvents);
    }

    return ntStatus;
} // Init

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS                            
CMiniportTopologySYSVAD::PropertyHandlerGeneric
(
    _In_  PPCPROPERTY_REQUEST     PropertyRequest
)
/*++

Routine Description:

  Handles all properties for this miniport.

Arguments:

  PropertyRequest - property request structure

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    NTSTATUS                    ntStatus = STATUS_INVALID_DEVICE_REQUEST;

    switch (PropertyRequest->PropertyItem->Id)
    {
        case KSPROPERTY_AUDIO_VOLUMELEVEL:
            ntStatus = PropertyHandler_Volume(
                                m_AdapterCommon,
                                PropertyRequest,
                                m_DeviceMaxChannels);
            break;
        
        case KSPROPERTY_AUDIO_MUTE:
            ntStatus = PropertyHandler_Mute(
                                m_AdapterCommon,
                                PropertyRequest,
                                m_DeviceMaxChannels);
            break;

        case KSPROPERTY_AUDIO_PEAKMETER2:
            ntStatus = PropertyHandler_PeakMeter2(
                                m_AdapterCommon,
                                PropertyRequest,
                                m_DeviceMaxChannels);
            break;

        case KSPROPERTY_AUDIO_CPU_RESOURCES:
            ntStatus = PropertyHandler_CpuResources(PropertyRequest);
            break;

        case KSPROPERTY_AUDIO_MUX_SOURCE:
            ntStatus = PropertyHandlerMuxSource(PropertyRequest);
            break;

        case KSPROPERTY_AUDIO_DEV_SPECIFIC:
            ntStatus = PropertyHandlerDevSpecific(PropertyRequest);
            break;

        default:
            DPF(D_TERSE, ("[PropertyHandlerGeneric: Invalid Device Request]"));
    }

    return ntStatus;
} // PropertyHandlerGeneric

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS                            
CMiniportTopologySYSVAD::PropertyHandlerMuxSource
(
    _In_  PPCPROPERTY_REQUEST     PropertyRequest
)
/*++

Routine Description:

  PropertyHandler for KSPROPERTY_AUDIO_MUX_SOURCE.

Arguments:

  PropertyRequest - property request structure

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    DPF_ENTER(("[%s]",__FUNCTION__));

    NTSTATUS                    ntStatus = STATUS_INVALID_DEVICE_REQUEST;

    //
    // Validate node
    // This property is only valid for WAVEIN_MUX node.
    //
    // TODO if (WAVEIN_MUX == PropertyRequest->Node)
    {
        if (PropertyRequest->ValueSize >= sizeof(ULONG))
        {
            PULONG pulMuxValue = PULONG(PropertyRequest->Value);
            
            if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET)
            {
                *pulMuxValue = m_AdapterCommon->MixerMuxRead();
                PropertyRequest->ValueSize = sizeof(ULONG);
                ntStatus = STATUS_SUCCESS;
            }
            else if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET)
            {
                m_AdapterCommon->MixerMuxWrite(*pulMuxValue);
                ntStatus = STATUS_SUCCESS;
            }
            else if (PropertyRequest->Verb & KSPROPERTY_TYPE_BASICSUPPORT)
            {
                ntStatus = 
                    PropertyHandler_BasicSupport
                    ( 
                        PropertyRequest, 
                        KSPROPERTY_TYPE_ALL,
                        VT_I4
                    );
            }
        }
        else
        {
            DPF(D_TERSE, ("[PropertyHandlerMuxSource - Invalid parameter]"));
            ntStatus = STATUS_INVALID_PARAMETER;
        }
    }

    return ntStatus;
} // PropertyHandlerMuxSource

//=============================================================================
#pragma code_seg("PAGE")
NTSTATUS                            
CMiniportTopologySYSVAD::PropertyHandlerDevSpecific
(
    _In_  PPCPROPERTY_REQUEST     PropertyRequest
)
/*++

Routine Description:

  Property handler for KSPROPERTY_AUDIO_DEV_SPECIFIC

Arguments:

  PropertyRequest - property request structure

Return Value:

  NT status code.

--*/
{
    PAGED_CODE();

    DPF_ENTER(("[%s]",__FUNCTION__));

    NTSTATUS ntStatus=STATUS_SUCCESS;

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_BASICSUPPORT)
    {
        if( DEV_SPECIFIC_VT_BOOL == PropertyRequest->Node )
        {
            ntStatus = PropertyHandler_BasicSupport(PropertyRequest,KSPROPERTY_TYPE_ALL,VT_BOOL);
        }
        else
        {
            ULONG ExpectedSize = sizeof( KSPROPERTY_DESCRIPTION ) + 
                                 sizeof( KSPROPERTY_MEMBERSHEADER ) + 
                                 sizeof( KSPROPERTY_BOUNDS_LONG );
            DWORD ulPropTypeSetId;

            if( DEV_SPECIFIC_VT_I4 == PropertyRequest->Node )
            {
                ulPropTypeSetId = VT_I4;
            }
            else if ( DEV_SPECIFIC_VT_UI4 == PropertyRequest->Node )
            {
                ulPropTypeSetId = VT_UI4;
            }
            else
            {
                ulPropTypeSetId = VT_ILLEGAL;
                ntStatus = STATUS_INVALID_PARAMETER;
            }

            if( NT_SUCCESS(ntStatus))
            {
                if ( !PropertyRequest->ValueSize )
                {
                    PropertyRequest->ValueSize = ExpectedSize;
                    ntStatus = STATUS_BUFFER_OVERFLOW;
                } 
                else if (PropertyRequest->ValueSize >= sizeof(KSPROPERTY_DESCRIPTION))
                {
                    // if return buffer can hold a KSPROPERTY_DESCRIPTION, return it
                    //
                    PKSPROPERTY_DESCRIPTION PropDesc = PKSPROPERTY_DESCRIPTION(PropertyRequest->Value);

                    PropDesc->AccessFlags       = KSPROPERTY_TYPE_ALL;
                    PropDesc->DescriptionSize   = ExpectedSize;
                    PropDesc->PropTypeSet.Set   = KSPROPTYPESETID_General;
                    PropDesc->PropTypeSet.Id    = ulPropTypeSetId;
                    PropDesc->PropTypeSet.Flags = 0;
                    PropDesc->MembersListCount  = 0;
                    PropDesc->Reserved          = 0;

                    if ( PropertyRequest->ValueSize >= ExpectedSize )
                    {
                        // Extra information to return
                        PropDesc->MembersListCount  = 1;

                        PKSPROPERTY_MEMBERSHEADER MembersHeader = ( PKSPROPERTY_MEMBERSHEADER )( PropDesc + 1);
                        MembersHeader->MembersFlags = KSPROPERTY_MEMBER_RANGES;
                        MembersHeader->MembersCount  = 1;
                        MembersHeader->MembersSize   = sizeof( KSPROPERTY_BOUNDS_LONG );
                        MembersHeader->Flags = 0;

                        PKSPROPERTY_BOUNDS_LONG PeakMeterBounds = (PKSPROPERTY_BOUNDS_LONG)( MembersHeader + 1);
                        if(VT_I4 == ulPropTypeSetId )
                        {
                            PeakMeterBounds->SignedMinimum = 0;
                            PeakMeterBounds->SignedMaximum = 0x7fffffff;
                        }
                        else
                        {
                            PeakMeterBounds->UnsignedMinimum = 0;
                            PeakMeterBounds->UnsignedMaximum = 0xffffffff;
                        }

                        // set the return value size
                        PropertyRequest->ValueSize = ExpectedSize;
                    }
                    else
                    {
                        // No extra information to return.
                        PropertyRequest->ValueSize = sizeof(KSPROPERTY_DESCRIPTION);
                    }

                    ntStatus = STATUS_SUCCESS;
                } 
                else if (PropertyRequest->ValueSize >= sizeof(ULONG))
                {
                    // if return buffer can hold a ULONG, return the access flags
                    //
                    *(PULONG(PropertyRequest->Value)) = KSPROPERTY_TYPE_ALL;

                    PropertyRequest->ValueSize = sizeof(ULONG);
                    ntStatus = STATUS_SUCCESS;                    
                }
                else
                {
                    PropertyRequest->ValueSize = 0;
                    ntStatus = STATUS_BUFFER_TOO_SMALL;
                }
            }
        }
    }
    else
    {
        // switch on node id
        switch( PropertyRequest->Node )
        {
        case DEV_SPECIFIC_VT_BOOL:
            {
                PBOOL pbDevSpecific;

                ntStatus = ValidatePropertyParams(PropertyRequest, sizeof(BOOL), 0);

                if (NT_SUCCESS(ntStatus))
                {
                    pbDevSpecific   = PBOOL (PropertyRequest->Value);

                    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET)
                    {
                        *pbDevSpecific = m_AdapterCommon->bDevSpecificRead();
                        PropertyRequest->ValueSize = sizeof(BOOL);
                    }
                    else if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET)
                    {
                        m_AdapterCommon->bDevSpecificWrite(*pbDevSpecific);
                    }
                    else
                    {
                        ntStatus = STATUS_INVALID_PARAMETER;
                    }
                }
            }
            break;
        case DEV_SPECIFIC_VT_I4:
            {
                INT* piDevSpecific;

                ntStatus = ValidatePropertyParams(PropertyRequest, sizeof(int), 0);

                if (NT_SUCCESS(ntStatus))
                {
                    piDevSpecific   = PINT (PropertyRequest->Value);

                    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET)
                    {
                        *piDevSpecific = m_AdapterCommon->iDevSpecificRead();
                        PropertyRequest->ValueSize = sizeof(int);
                    }
                    else if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET)
                    {
                        m_AdapterCommon->iDevSpecificWrite(*piDevSpecific);
                    }
                    else
                    {
                        ntStatus = STATUS_INVALID_PARAMETER;
                    }
                }
            }
            break;
        case DEV_SPECIFIC_VT_UI4:
            {
                UINT* puiDevSpecific;

                ntStatus = ValidatePropertyParams(PropertyRequest, sizeof(UINT), 0);

                if (NT_SUCCESS(ntStatus))
                {
                    puiDevSpecific   = PUINT (PropertyRequest->Value);

                    if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET)
                    {
                        *puiDevSpecific = m_AdapterCommon->uiDevSpecificRead();
                        PropertyRequest->ValueSize = sizeof(UINT);
                    }
                    else if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET)
                    {
                        m_AdapterCommon->uiDevSpecificWrite(*puiDevSpecific);
                    }
                    else
                    {
                        ntStatus = STATUS_INVALID_PARAMETER;
                    }
                }
            }
            break;
        default:
            ntStatus = STATUS_INVALID_PARAMETER;
            break;
        }


        if( !NT_SUCCESS(ntStatus))
        {
            DPF(D_TERSE, ("[%s - ntStatus=0x%08x]",__FUNCTION__,ntStatus));
        }
    }

    return ntStatus;
} // PropertyHandlerDevSpecific

//=============================================================================
#pragma code_seg("PAGE")
VOID
CMiniportTopologySYSVAD::AddEventToEventList
(
    _In_  PKSEVENT_ENTRY    EventEntry 
)
/*++

Routine Description:

  The AddEventToEventList method adds an event to the port driver's event list

Arguments:

  EventEntry - 

--*/
{
    PAGED_CODE();
    DPF_ENTER(("[CMiniportTopology::AddEventToEventList]"));

    ASSERT(m_PortEvents != NULL);

    m_PortEvents->AddEventToEventList(EventEntry);
}

//=============================================================================
#pragma code_seg()
VOID
CMiniportTopologySYSVAD::GenerateEventList
(
    _In_opt_    GUID   *Set,
    _In_        ULONG   EventId,
    _In_        BOOL    PinEvent,
    _In_        ULONG   PinId,
    _In_        BOOL    NodeEvent,
    _In_        ULONG   NodeId
)
/*++

Routine Description:

  The GenerateEventList method notifies clients through the port driver's list 
  of event entries that a particular event has occurred.

Arguments:

  Set -

  EventId - 

  PinEvent -

  PinId -

  NodeEvent -

  NodeId -

--*/
{
    DPF_ENTER(("[CMiniportTopologySYSVAD::GenerateEventList]"));

    ASSERT(m_PortEvents != NULL);

    m_PortEvents->GenerateEventList(
        Set,
        EventId,
        PinEvent,
        PinId,
        NodeEvent,
        NodeId);
}
 
#pragma code_seg()


//=============================================================================
// Leyline cables
//=============================================================================

#pragma code_seg("PAGE")
VOID
CMiniportTopologySYSVAD::LeylineAttach
(
    _In_ ULONG CableId,
    _In_ ULONG JackPinId
)
/*++
Routine Description:
  Binds this topology filter to a Leyline cable: its jack follows the cable's connection state.
--*/
{
    PAGED_CODE();

    m_LeylineCableId = CableId;
    m_LeylineJackPin = JackPinId;

    if (CableId != LEYLINE_NO_CABLE)
    {
        LeylineCableRegisterJack(CableId, this, LeylineJackChanged);
    }
}

#pragma code_seg()
VOID
CMiniportTopologySYSVAD::LeylineJackChanged
(
    _In_ PVOID Context
)
/*++
Routine Description:
  The cable was connected or disconnected: tell Windows to re-read the jack description.
--*/
{
    CMiniportTopologySYSVAD* This = static_cast<CMiniportTopologySYSVAD*>(Context);

    if (This->m_PortEvents == NULL)
    {
        return;     // not initialized yet; Windows reads the current state when it first queries the jack
    }

    This->GenerateEventList(
        (GUID*)&KSEVENTSETID_PinCapsChange,
        KSEVENT_PINCAPS_JACKINFOCHANGE,
        TRUE,                           // pin event
        This->m_LeylineJackPin,
        FALSE,                          // not a node event
        ULONG(-1));
}

#pragma code_seg("PAGE")
NTSTATUS
LeylineTopoPropertyHandler
(
    _In_        PPCPROPERTY_REQUEST         PropertyRequest,
    _In_        CMiniportTopologySYSVAD    *Miniport,
    _In_        ULONG                       cJackDescriptions,
    _In_reads_(cJackDescriptions) PKSJACK_DESCRIPTION *JackDescriptions
)
{
    PAGED_CODE();

    ASSERT(PropertyRequest);
    ASSERT(Miniport);

    NTSTATUS    ntStatus = STATUS_INVALID_DEVICE_REQUEST;
    ULONG       cableId  = Miniport->GetLeylineCableId();
    const GUID* set      = PropertyRequest->PropertyItem->Set;
    ULONG       id       = PropertyRequest->PropertyItem->Id;

    if (IsEqualGUIDAligned(*set, KSPROPSETID_Leyline) && id == KSPROPERTY_LEYLINE_CABLE_CONNECTED)
    {
        if (PropertyRequest->Verb & KSPROPERTY_TYPE_BASICSUPPORT)
        {
            return PropertyHandler_BasicSupport(PropertyRequest, KSPROPERTY_TYPE_BASICSUPPORT | KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_SET, VT_UI4);
        }

        if (PropertyRequest->ValueSize == 0)
        {
            PropertyRequest->ValueSize = sizeof(ULONG);
            return STATUS_BUFFER_OVERFLOW;
        }
        if (PropertyRequest->ValueSize < sizeof(ULONG))
        {
            return STATUS_BUFFER_TOO_SMALL;
        }

        if (PropertyRequest->Verb & KSPROPERTY_TYPE_GET)
        {
            *(PULONG)PropertyRequest->Value = LeylineCableIsConnected(cableId) ? 1 : 0;
            PropertyRequest->ValueSize = sizeof(ULONG);
            return STATUS_SUCCESS;
        }

        if (PropertyRequest->Verb & KSPROPERTY_TYPE_SET)
        {
            LeylineCableSetConnected(cableId, (*(PULONG)PropertyRequest->Value) != 0);
            return STATUS_SUCCESS;
        }

        return STATUS_INVALID_DEVICE_REQUEST;
    }

    if (!IsEqualGUIDAligned(*set, KSPROPSETID_Jack) ||
        (id != KSPROPERTY_JACK_DESCRIPTION && id != KSPROPERTY_JACK_DESCRIPTION2))
    {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    if (PropertyRequest->InstanceSize < sizeof(ULONG))
    {
        return STATUS_INVALID_PARAMETER;
    }

    ULONG nPinId = *(PULONG(PropertyRequest->Instance));
    if (nPinId >= cJackDescriptions || JackDescriptions[nPinId] == NULL)
    {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    if (PropertyRequest->Verb & KSPROPERTY_TYPE_BASICSUPPORT)
    {
        return PropertyHandler_BasicSupport(PropertyRequest, KSPROPERTY_TYPE_BASICSUPPORT | KSPROPERTY_TYPE_GET, VT_ILLEGAL);
    }

    ULONG cbNeeded = sizeof(KSMULTIPLE_ITEM) +
        ((id == KSPROPERTY_JACK_DESCRIPTION) ? sizeof(KSJACK_DESCRIPTION) : sizeof(KSJACK_DESCRIPTION2));

    if (PropertyRequest->ValueSize == 0)
    {
        PropertyRequest->ValueSize = cbNeeded;
        return STATUS_BUFFER_OVERFLOW;
    }
    if (PropertyRequest->ValueSize < cbNeeded)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (!(PropertyRequest->Verb & KSPROPERTY_TYPE_GET))
    {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    PKSMULTIPLE_ITEM pMI = (PKSMULTIPLE_ITEM)PropertyRequest->Value;
    pMI->Size  = cbNeeded;
    pMI->Count = 1;

    if (id == KSPROPERTY_JACK_DESCRIPTION)
    {
        PKSJACK_DESCRIPTION pDesc = (PKSJACK_DESCRIPTION)(pMI + 1);
        RtlCopyMemory(pDesc, JackDescriptions[nPinId], sizeof(KSJACK_DESCRIPTION));
        pDesc->IsConnected = LeylineCableIsConnected(cableId);
    }
    else
    {
        PKSJACK_DESCRIPTION2 pDesc = (PKSJACK_DESCRIPTION2)(pMI + 1);
        RtlZeroMemory(pDesc, sizeof(KSJACK_DESCRIPTION2));
        pDesc->DeviceStateInfo  = 0;
        // Presence detection: IsConnected in KSJACK_DESCRIPTION reflects the cable's real state.
        pDesc->JackCapabilities = JACKDESC2_PRESENCE_DETECT_CAPABILITY;
    }

    ntStatus = STATUS_SUCCESS;
    return ntStatus;
}

// Not pageable: port class calls event handlers at raised IRQL (e.g. PCEVENT_VERB_REMOVE when a client
// closes the filter). Only PCEVENT_VERB_ADD, which runs at PASSIVE_LEVEL, calls into paged code.
#pragma code_seg()
NTSTATUS
LeylineTopoEventHandler
(
    _In_        PPCEVENT_REQUEST            EventRequest,
    _In_        CMiniportTopologySYSVAD    *Miniport
)
{
    ASSERT(EventRequest);
    ASSERT(Miniport);

    switch (EventRequest->Verb)
    {
        case PCEVENT_VERB_SUPPORT:
            break;

        case PCEVENT_VERB_ADD:
            if (EventRequest->EventEntry == NULL)
            {
                return STATUS_UNSUCCESSFUL;
            }
            // AddEventToEventList is pageable; ADD only arrives at PASSIVE_LEVEL.
            NT_ASSERT(KeGetCurrentIrql() == PASSIVE_LEVEL);
            Miniport->AddEventToEventList(EventRequest->EventEntry);
            break;

        case PCEVENT_VERB_REMOVE:
            // Port class removes the entry; nothing to undo here.
            break;

        default:
            return STATUS_INVALID_PARAMETER;
    }

    return STATUS_SUCCESS;
}

#pragma code_seg()
