//==============================================================================
// VoidPatchVAD.cpp
// WDM PortCls / WaveRT Multi-Endpoint Virtual Audio Driver for Windows 10 & 11
// Compile with Windows Driver Kit (WDK 10/11) x64
//==============================================================================

#include <ntddk.h>
#include <portcls.h>
#include <stdunk.h>
#include <ksdebug.h>
#include "VoidPatchVAD.h"

extern "C" DRIVER_INITIALIZE DriverEntry;
extern "C" DRIVER_ADD_DEVICE VoidPatchAddDevice;

NTSTATUS
InstallSubdevicePair(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp,
    _In_ const VoidPatchSubdeviceDescriptor& Desc,
    _In_ PRESOURCELIST ResourceList
)
{
    NTSTATUS ntStatus = STATUS_SUCCESS;
    PPORT portWave = nullptr;
    PPORT portTopo = nullptr;
    PUNKNOWN unknownWave = nullptr;
    PUNKNOWN unknownTopo = nullptr;

    // Create WaveRT Port & Topology Port for this VoidPatch Virtual Bus
    ntStatus = PcNewPort(&portWave, CLSID_PortWaveRT);
    if (!NT_SUCCESS(ntStatus))
        return ntStatus;

    ntStatus = PcNewPort(&portTopo, CLSID_PortTopology);
    if (!NT_SUCCESS(ntStatus))
    {
        portWave->Release();
        return ntStatus;
    }

    // Register subdevices under KSCATEGORY_AUDIO / KSCATEGORY_RENDER / KSCATEGORY_CAPTURE
    ntStatus = PcRegisterSubdevice(DeviceObject, (PWSTR)Desc.WaveSubdeviceName, portWave);
    if (NT_SUCCESS(ntStatus))
    {
        ntStatus = PcRegisterSubdevice(DeviceObject, (PWSTR)Desc.TopoSubdeviceName, portTopo);
    }

    if (unknownWave && unknownTopo)
    {
        PcRegisterPhysicalConnection(
            DeviceObject,
            unknownTopo,
            0,
            unknownWave,
            0);
    }

    portTopo->Release();
    portWave->Release();
    return ntStatus;
}

NTSTATUS
StartVoidPatchDevice(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp,
    _In_ PRESOURCELIST ResourceList
)
{
    NTSTATUS status = STATUS_SUCCESS;
    for (int i = 0; i < Endpoint_Count; i++)
    {
        status = InstallSubdevicePair(DeviceObject, Irp, g_VoidPatchEndpoints[i], ResourceList);
        if (!NT_SUCCESS(status))
            break;
    }
    return status;
}

extern "C"
NTSTATUS
VoidPatchAddDevice(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PDEVICE_OBJECT PhysicalDeviceObject
)
{
    PAGED_CODE();
    return PcAddAdapterDevice(
        DriverObject,
        PhysicalDeviceObject,
        PCPFNSTARTDEVICE(StartVoidPatchDevice),
        Endpoint_Count * 2,
        0);
}

extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPathName
)
{
    PAGED_CODE();
    return PcInitializeAdapterDriver(
        DriverObject,
        RegistryPathName,
        PDRIVER_ADD_DEVICE(VoidPatchAddDevice));
}
