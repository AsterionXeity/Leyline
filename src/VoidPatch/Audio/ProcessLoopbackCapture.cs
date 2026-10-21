using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace VoidPatch.Audio;

/// <summary>
/// Captures audio directly from a specific Windows Process ID (and its child processes)
/// using Windows 10 (2004+) / Windows 11 ActivateAudioInterfaceAsync with VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK.
/// </summary>
public sealed unsafe class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private uint _targetProcessId;
    private readonly string _targetProcessName;
    private volatile bool _isCapturing;
    private Thread? _captureThread;
    private WaveFormat _waveFormat;

    public WaveFormat WaveFormat
    {
        get => _waveFormat;
        set => _waveFormat = value;
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public ProcessLoopbackCapture(uint processId, string processName = "")
    {
        _targetProcessId = processId;
        _targetProcessName = processName;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public void StartRecording()
    {
        if (_isCapturing) return;
        _isCapturing = true;
        _captureThread = new Thread(CaptureThreadLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = $"VoidPatch-ProcLoopback-{_targetProcessId}"
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void StopRecording()
    {
        _isCapturing = false;
        try
        {
            _captureThread?.Join(500);
        }
        catch { }
    }

    private uint ResolveCurrentProcessId()
    {
        if (_targetProcessId != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)_targetProcessId);
                if (!p.HasExited) return _targetProcessId;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(_targetProcessName))
        {
            try
            {
                var procs = Process.GetProcessesByName(_targetProcessName);
                var active = procs.FirstOrDefault(p => !p.HasExited);
                if (active != null)
                {
                    _targetProcessId = (uint)active.Id;
                    return _targetProcessId;
                }
            }
            catch { }
        }

        return _targetProcessId;
    }

    private void CaptureThreadLoop()
    {
        Exception? error = null;
        IntPtr audioClientPtr = IntPtr.Zero;
        IntPtr captureClientPtr = IntPtr.Zero;
        IntPtr eventHandle = IntPtr.Zero;

        try
        {
            uint pid = ResolveCurrentProcessId();
            if (pid == 0)
                return;

            audioClientPtr = ActivateProcessAudioClient(pid);
            if (audioClientPtr == IntPtr.Zero)
                return;

            void** acVtbl = *(void***)audioClientPtr;

            // Prepare WAVEFORMATEX for 48000 Hz, 2-channel 32-bit IEEE Float
            WaveFormatExFloat fmt = new()
            {
                FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                Channels = 2,
                SamplesPerSec = 48000,
                BitsPerSample = 32,
                BlockAlign = 8,
                AvgBytesPerSec = 48000 * 8,
                ExtraSize = 0
            };

            // IAudioClient::Initialize (vtable slot 3)
            // AUDCLNT_SHAREMODE_SHARED = 0
            // AUDCLNT_STREAMFLAGS_LOOPBACK (0x00020000) | AUDCLNT_STREAMFLAGS_EVENTCALLBACK (0x00040000) | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM (0x80000000) | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY (0x08000000)
            const uint streamFlags = 0x00020000u | 0x00040000u | 0x80000000u | 0x08000000u;
            var initialize = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormatExFloat*, IntPtr, int>)acVtbl[3];

            int hr = initialize(audioClientPtr, 0, streamFlags, 200_000, 0, &fmt, IntPtr.Zero);
            if (hr < 0)
                return;

            eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            var setEventHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)acVtbl[7];
            setEventHandle(audioClientPtr, eventHandle);

            Guid iidCapture = IID_IAudioCaptureClient;
            var getService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, int>)acVtbl[14];
            if (getService(audioClientPtr, &iidCapture, out captureClientPtr) < 0 || captureClientPtr == IntPtr.Zero)
                return;

            var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)acVtbl[5];
            var stop = (delegate* unmanaged[Stdcall]<IntPtr, int>)acVtbl[6];

            if (start(audioClientPtr) < 0)
                return;

            void** capVtbl = *(void***)captureClientPtr;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, out byte*, out uint, out uint, out ulong, out ulong, int>)capVtbl[3];
            var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)capVtbl[4];
            var getNextPacketSize = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)capVtbl[5];

            byte[] managedBuf = new byte[48000];

            while (_isCapturing)
            {
                WaitForSingleObject(eventHandle, 50);

                while (_isCapturing && getNextPacketSize(captureClientPtr, out uint packetLength) >= 0 && packetLength > 0)
                {
                    if (getBuffer(captureClientPtr, out byte* dataPtr, out uint framesAvailable, out uint flags, out _, out _) >= 0)
                    {
                        int bytesToCopy = (int)(framesAvailable * 8);
                        if (bytesToCopy > 0)
                        {
                            if (managedBuf.Length < bytesToCopy)
                                managedBuf = new byte[bytesToCopy * 2];

                            if ((flags & 0x2) != 0 || dataPtr == null) // AUDCLNT_BUFFERFLAGS_SILENT
                            {
                                Array.Clear(managedBuf, 0, bytesToCopy);
                            }
                            else
                            {
                                Marshal.Copy((IntPtr)dataPtr, managedBuf, 0, bytesToCopy);
                            }

                            DataAvailable?.Invoke(this, new WaveInEventArgs(managedBuf, bytesToCopy));
                        }

                        releaseBuffer(captureClientPtr, framesAvailable);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            stop(audioClientPtr);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (captureClientPtr != IntPtr.Zero) Marshal.Release(captureClientPtr);
            if (audioClientPtr != IntPtr.Zero) Marshal.Release(audioClientPtr);
            if (eventHandle != IntPtr.Zero) CloseHandle(eventHandle);
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private static IntPtr ActivateProcessAudioClient(uint processId)
    {
        IntPtr completionEvent = CreateEventW(IntPtr.Zero, true, false, null);
        ActivationCompletionState state = new()
        {
            Vtbl = CompletionHandlerVtbl,
            RefCount = 1,
            CompletionEvent = completionEvent
        };

        try
        {
            AudioClientActivationParams activationParams = new()
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = processId,
                ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            };

            PropVariant activatePropVar = new()
            {
                Vt = 65, // VT_BLOB
                BlobSize = (uint)sizeof(AudioClientActivationParams),
                BlobData = (IntPtr)(&activationParams)
            };

            Guid iidAudioClient = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref iidAudioClient,
                &activatePropVar,
                (IntPtr)(&state),
                out IntPtr asyncOpPtr);

            if (hr < 0)
                return IntPtr.Zero;

            WaitForSingleObject(completionEvent, 3000);

            if (asyncOpPtr != IntPtr.Zero)
            {
                void** opVtbl = *(void***)asyncOpPtr;
                var getActivateResult = (delegate* unmanaged[Stdcall]<IntPtr, out int, out IntPtr, int>)opVtbl[3];
                if (getActivateResult(asyncOpPtr, out int activateHr, out IntPtr activatedInterface) >= 0 && activateHr >= 0)
                {
                    Marshal.Release(asyncOpPtr);
                    return activatedInterface;
                }
                Marshal.Release(asyncOpPtr);
            }
        }
        catch
        {
        }
        finally
        {
            if (completionEvent != IntPtr.Zero)
                CloseHandle(completionEvent);
        }

        return IntPtr.Zero;
    }

    private static readonly IntPtr* CompletionHandlerVtbl = CreateCompletionVtbl();

    private static IntPtr* CreateCompletionVtbl()
    {
        IntPtr* vtbl = (IntPtr*)NativeMemory.AllocZeroed(4, (nuint)sizeof(IntPtr));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<ActivationCompletionState*, Guid*, void**, int>)&Completion_QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<ActivationCompletionState*, uint>)&Completion_AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<ActivationCompletionState*, uint>)&Completion_Release;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<ActivationCompletionState*, IntPtr, int>)&Completion_ActivateCompleted;
        return vtbl;
    }

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_ICompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Completion_QueryInterface(ActivationCompletionState* thisPtr, Guid* iid, void** ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_ICompletionHandler || *iid == IID_IAgileObject)
        {
            *ppv = thisPtr;
            return 0;
        }
        *ppv = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Completion_AddRef(ActivationCompletionState* thisPtr) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Completion_Release(ActivationCompletionState* thisPtr) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Completion_ActivateCompleted(ActivationCompletionState* thisPtr, IntPtr activateOperation)
    {
        if (thisPtr->CompletionEvent != IntPtr.Zero)
            SetEvent(thisPtr->CompletionEvent);
        return 0;
    }

    public void Dispose() => StopRecording();

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        PropVariant* activationParams,
        IntPtr completionHandler,
        out IntPtr activationOperation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationCompletionState
    {
        public IntPtr* Vtbl;
        public uint RefCount;
        public IntPtr CompletionEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public uint Padding;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExFloat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }
}
