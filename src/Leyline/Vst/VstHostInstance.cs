using System.Runtime.InteropServices;
using System.Text;

namespace Leyline.Vst;

public sealed class VstParameterItem
{
    public uint Id { get; set; }
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Units { get; set; } = string.Empty;
    public double NormalizedValue { get; set; }
}

public sealed unsafe class VstHostInstance : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _moduleHandle;
    private bool _isVst3;
    private bool _isLoaded;
    private bool _isDisposed;

    // Pre-allocated audio buffers for zero-GC real-time processing
    private const int MaxBlockSize = 4096;
    private readonly float* _inLeft;
    private readonly float* _inRight;
    private readonly float* _outLeft;
    private readonly float* _outRight;
    private readonly float** _inputsPtr;
    private readonly float** _outputsPtr;

    // VST2 State
    private AEffect* _vst2Effect;
    private HostCallbackDelegate? _vst2HostCallbackHold;

    // VST3 State
    private void* _vst3Factory;
    private void* _vst3Component;
    private void* _vst3Processor;
    private void* _vst3Controller;
    private void* _vst3PlugView;
    private bool _vst3ControllerIsSeparate;
    private Vst3AudioBusBuffers* _vst3InBus;
    private Vst3AudioBusBuffers* _vst3OutBus;
    private Vst3ProcessData* _vst3ProcessData;
    private IntPtr _hostContextComPtr;
    private IntPtr _componentHandlerComPtr;
    private IntPtr _plugFrameComPtr;

    public string PluginPath { get; }
    public string PluginName { get; private set; }
    public string Format => _isVst3 ? "VST3" : "VST2";
    public bool IsLoaded => _isLoaded;
    public bool IsBypassed { get; set; }
    public float WetMix { get; set; } = 1.0f;
    public string LastError { get; private set; } = string.Empty;
    public Action<int, int>? OnEditorResizeRequested { get; set; }

    public VstHostInstance(string pluginPath, int sampleRate = 48000)
    {
        PluginPath = pluginPath;
        PluginName = System.IO.Path.GetFileNameWithoutExtension(pluginPath);

        _inLeft = (float*)NativeMemory.AllocZeroed(MaxBlockSize, sizeof(float));
        _inRight = (float*)NativeMemory.AllocZeroed(MaxBlockSize, sizeof(float));
        _outLeft = (float*)NativeMemory.AllocZeroed(MaxBlockSize, sizeof(float));
        _outRight = (float*)NativeMemory.AllocZeroed(MaxBlockSize, sizeof(float));

        _inputsPtr = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
        _outputsPtr = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
        _inputsPtr[0] = _inLeft;
        _inputsPtr[1] = _inRight;
        _outputsPtr[0] = _outLeft;
        _outputsPtr[1] = _outRight;

        _vst3InBus = (Vst3AudioBusBuffers*)NativeMemory.AllocZeroed(1, (nuint)sizeof(Vst3AudioBusBuffers));
        _vst3OutBus = (Vst3AudioBusBuffers*)NativeMemory.AllocZeroed(1, (nuint)sizeof(Vst3AudioBusBuffers));
        _vst3ProcessData = (Vst3ProcessData*)NativeMemory.AllocZeroed(1, (nuint)sizeof(Vst3ProcessData));

        LoadPlugin(sampleRate);
    }

    private void LoadPlugin(int sampleRate)
    {
        try
        {
            if (!System.IO.File.Exists(PluginPath))
            {
                LastError = "Plugin file not found.";
                return;
            }

            if (!NativeLibrary.TryLoad(PluginPath, out _moduleHandle))
            {
                LastError = "Could not load native binary (ensure 64-bit plugin).";
                return;
            }

            // Check VST3 entry point first
            if (NativeLibrary.TryGetExport(_moduleHandle, "GetPluginFactory", out IntPtr getFactoryPtr))
            {
                _isVst3 = true;
                _isLoaded = InitializeVst3(getFactoryPtr, sampleRate);
                return;
            }

            // Check VST2 entry points
            if (NativeLibrary.TryGetExport(_moduleHandle, "VSTPluginMain", out IntPtr vstMainPtr) ||
                NativeLibrary.TryGetExport(_moduleHandle, "main", out vstMainPtr))
            {
                _isVst3 = false;
                _isLoaded = InitializeVst2(vstMainPtr, sampleRate);
                return;
            }

            LastError = "No valid VST2 or VST3 entry point exported.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _isLoaded = false;
        }
    }

    #region VST3 Hosting Implementation

    private static readonly Guid IID_FUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IComponent = new("E831FF31-F2D5-4301-928E-BBEE25697802");
    private static readonly Guid IID_IAudioProcessor = new("42043F99-B7DA-453C-A569-E79D9AAEC33D");
    private static readonly Guid IID_IEditController = new("DCD7BBE3-7742-448D-A874-AACC979C759E");
    private static readonly Guid IID_IConnectionPoint = new("70A4156F-6E6E-4026-9891-48BFAA60D8D1");
    private static readonly Guid IID_IHostApplication = new("58E595CC-DB2D-4969-8B6A-AF8C36A664E5");
    private static readonly Guid IID_IComponentHandler = new("93A0BEA3-0BD0-45DB-8E89-0B0CC1E46AC6");
    private static readonly Guid IID_IPlugFrame = new("367FAF01-AFA9-4693-8D4D-A2A0ED0882A3");

    private bool InitializeVst3(IntPtr getFactoryPtr, int sampleRate)
    {
        if (NativeLibrary.TryGetExport(_moduleHandle, "InitDll", out IntPtr initDllPtr))
        {
            var initDll = (delegate* unmanaged[Stdcall]<byte>)initDllPtr;
            initDll();
        }

        var getFactory = (delegate* unmanaged[Stdcall]<void*>)getFactoryPtr;
        _vst3Factory = getFactory();
        if (_vst3Factory == null)
        {
            LastError = "GetPluginFactory returned null.";
            return false;
        }

        _hostContextComPtr = CreateHostContextVTable();
        _componentHandlerComPtr = CreateComponentHandlerVTable();
        _plugFrameComPtr = CreatePlugFrameVTable();

        void** factoryVtbl = *(void***)_vst3Factory;
        var countClasses = (delegate* unmanaged[Stdcall]<void*, int>)factoryVtbl[4];
        var getClassInfo = (delegate* unmanaged[Stdcall]<void*, int, PClassInfo*, int>)factoryVtbl[5];
        var createInstance = (delegate* unmanaged[Stdcall]<void*, byte*, byte*, void**, int>)factoryVtbl[6];

        int numClasses = countClasses(_vst3Factory);
        Guid audioClassCid = Guid.Empty;

        for (int i = 0; i < numClasses; i++)
        {
            PClassInfo info = default;
            if (getClassInfo(_vst3Factory, i, &info) == 0)
            {
                string category = Encoding.ASCII.GetString(info.Category, 32).TrimEnd('\0');
                string name = Encoding.ASCII.GetString(info.Name, 64).TrimEnd('\0');
                if (category.Equals("Audio Module Class", StringComparison.OrdinalIgnoreCase) || audioClassCid == Guid.Empty)
                {
                    audioClassCid = new Guid(new ReadOnlySpan<byte>(info.Cid, 16));
                    if (!string.IsNullOrWhiteSpace(name))
                        PluginName = name;
                    if (category.Equals("Audio Module Class", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
        }

        if (audioClassCid == Guid.Empty)
        {
            LastError = "No Audio Module Class found in VST3 factory.";
            return false;
        }

        byte* cidBytes = stackalloc byte[16];
        byte* iidCompBytes = stackalloc byte[16];
        WriteVst3Tuid(audioClassCid, cidBytes);
        WriteVst3Tuid(IID_IComponent, iidCompBytes);

        void* comp = null;
        int res = createInstance(_vst3Factory, cidBytes, iidCompBytes, &comp);
        if (res != 0 || comp == null)
        {
            // Try standard Windows COM GUID byte order fallback
            audioClassCid.TryWriteBytes(new Span<byte>(cidBytes, 16));
            IID_IComponent.TryWriteBytes(new Span<byte>(iidCompBytes, 16));
            res = createInstance(_vst3Factory, cidBytes, iidCompBytes, &comp);
        }

        if (res != 0 || comp == null)
        {
            LastError = $"VST3 createInstance failed (0x{res:X8}).";
            return false;
        }

        _vst3Component = comp;
        void** compVtbl = *(void***)_vst3Component;

        // IPluginBase::initialize (index 3)
        var initialize = (delegate* unmanaged[Stdcall]<void*, void*, int>)compVtbl[3];
        initialize(_vst3Component, (void*)_hostContextComPtr);

        // Query IAudioProcessor
        void* proc = QueryVst3Interface(_vst3Component, IID_IAudioProcessor);
        if (proc == null)
        {
            LastError = "VST3 plugin does not expose IAudioProcessor.";
            return false;
        }
        _vst3Processor = proc;

        // Query or create IEditController
        void* ctrl = QueryVst3Interface(_vst3Component, IID_IEditController);
        if (ctrl != null)
        {
            _vst3Controller = ctrl;
            _vst3ControllerIsSeparate = false;
        }
        else
        {
            // IComponent::getControllerClassId (index 5)
            byte* ctrlCidBytes = stackalloc byte[16];
            var getCtrlCid = (delegate* unmanaged[Stdcall]<void*, byte*, int>)compVtbl[5];
            if (getCtrlCid(_vst3Component, ctrlCidBytes) == 0)
            {
                byte* iidCtrlBytes = stackalloc byte[16];
                IID_IEditController.TryWriteBytes(new Span<byte>(iidCtrlBytes, 16));
                void* sepCtrl = null;
                if (createInstance(_vst3Factory, ctrlCidBytes, iidCtrlBytes, &sepCtrl) == 0 && sepCtrl != null)
                {
                    _vst3Controller = sepCtrl;
                    _vst3ControllerIsSeparate = true;
                    void** ctrlVtbl = *(void***)_vst3Controller;
                    var initCtrl = (delegate* unmanaged[Stdcall]<void*, void*, int>)ctrlVtbl[3];
                    initCtrl(_vst3Controller, (void*)_hostContextComPtr);

                    // Connect component and controller via IConnectionPoint if supported
                    void* cpComp = QueryVst3Interface(_vst3Component, IID_IConnectionPoint);
                    void* cpCtrl = QueryVst3Interface(_vst3Controller, IID_IConnectionPoint);
                    if (cpComp != null && cpCtrl != null)
                    {
                        var connectComp = (delegate* unmanaged[Stdcall]<void*, void*, int>)(*(void***)cpComp)[3];
                        var connectCtrl = (delegate* unmanaged[Stdcall]<void*, void*, int>)(*(void***)cpCtrl)[3];
                        connectComp(cpComp, cpCtrl);
                        connectCtrl(cpCtrl, cpComp);
                    }
                }
            }
        }

        if (_vst3Controller != null)
        {
            void** ctrlVtbl = *(void***)_vst3Controller;
            var setComponentHandler = (delegate* unmanaged[Stdcall]<void*, void*, int>)ctrlVtbl[16];
            setComponentHandler(_vst3Controller, (void*)_componentHandlerComPtr);
        }

        // Configure stereo bus arrangements and setupProcessing
        void** procVtbl = *(void***)_vst3Processor;
        ulong stereoArr = 3; // Left | Right
        var setBusArrangements = (delegate* unmanaged[Stdcall]<void*, ulong*, int, ulong*, int, int>)procVtbl[3];
        setBusArrangements(_vst3Processor, &stereoArr, 1, &stereoArr, 1);

        Vst3ProcessSetup setup = new()
        {
            ProcessMode = 0,        // kRealtime
            SymbolicSampleSize = 0, // kSample32
            MaxSamplesPerBlock = MaxBlockSize,
            SampleRate = sampleRate
        };
        var setupProcessing = (delegate* unmanaged[Stdcall]<void*, Vst3ProcessSetup*, int>)procVtbl[7];
        setupProcessing(_vst3Processor, &setup);

        // Activate audio input & output buses (IComponent::activateBus index 10)
        var activateBus = (delegate* unmanaged[Stdcall]<void*, int, int, int, byte, int>)compVtbl[10];
        activateBus(_vst3Component, 0, 0, 0, 1); // Audio Input 0
        activateBus(_vst3Component, 0, 1, 0, 1); // Audio Output 0

        // IComponent::setActive(1) (index 11)
        var setActive = (delegate* unmanaged[Stdcall]<void*, byte, int>)compVtbl[11];
        setActive(_vst3Component, 1);

        // IAudioProcessor::setProcessing(1) (index 8)
        var setProcessing = (delegate* unmanaged[Stdcall]<void*, byte, int>)procVtbl[8];
        setProcessing(_vst3Processor, 1);

        // Prepare ProcessData structures
        _vst3InBus->NumChannels = 2;
        _vst3InBus->SilenceFlags = 0;
        _vst3InBus->ChannelBuffers32 = _inputsPtr;

        _vst3OutBus->NumChannels = 2;
        _vst3OutBus->SilenceFlags = 0;
        _vst3OutBus->ChannelBuffers32 = _outputsPtr;

        _vst3ProcessData->ProcessMode = 0;
        _vst3ProcessData->SymbolicSampleSize = 0;
        _vst3ProcessData->NumInputs = 1;
        _vst3ProcessData->NumOutputs = 1;
        _vst3ProcessData->Inputs = _vst3InBus;
        _vst3ProcessData->Outputs = _vst3OutBus;

        return true;
    }

    private static void* QueryVst3Interface(void* unknownObj, Guid iid)
    {
        if (unknownObj == null) return null;
        void** vtbl = *(void***)unknownObj;
        var qi = (delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)vtbl[0];

        byte* iidBytes = stackalloc byte[16];
        iid.TryWriteBytes(new Span<byte>(iidBytes, 16));
        void* result = null;
        if (qi(unknownObj, iidBytes, &result) == 0 && result != null)
            return result;

        WriteVst3Tuid(iid, iidBytes);
        if (qi(unknownObj, iidBytes, &result) == 0 && result != null)
            return result;

        return null;
    }

    private static void WriteVst3Tuid(Guid guid, byte* dest)
    {
        guid.TryWriteBytes(new Span<byte>(dest, 16));
    }

    #endregion

    #region VST2 Hosting Implementation

    private bool InitializeVst2(IntPtr vstMainPtr, int sampleRate)
    {
        _vst2HostCallbackHold = Vst2HostCallback;
        IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_vst2HostCallbackHold);

        var mainFunc = (delegate* unmanaged[Cdecl]<IntPtr, AEffect*>)vstMainPtr;
        _vst2Effect = mainFunc(callbackPtr);
        if (_vst2Effect == null || _vst2Effect->Magic != 0x56737450) // 'VstP'
        {
            LastError = "Invalid VST2 AEffect magic header.";
            return false;
        }

        DispatchVst2(0, 0, 0, null, 0f);  // effOpen
        DispatchVst2(10, 0, 0, null, sampleRate); // effSetSampleRate
        DispatchVst2(11, 0, MaxBlockSize, null, 0f); // effSetBlockSize
        DispatchVst2(12, 0, 1, null, 0f); // effMainsChanged (resume)
        return true;
    }

    private nint DispatchVst2(int opcode, int index, nint value, void* ptr, float opt)
    {
        if (_vst2Effect == null || _vst2Effect->Dispatcher == null) return 0;
        var disp = (delegate* unmanaged[Cdecl]<AEffect*, int, int, nint, void*, float, nint>)_vst2Effect->Dispatcher;
        return disp(_vst2Effect, opcode, index, value, ptr, opt);
    }

    private static nint Vst2HostCallback(AEffect* effect, int opcode, int index, nint value, void* ptr, float opt)
    {
        return opcode switch
        {
            1 => 2400, // audioMasterVersion (VST 2.4)
            7 => 0,    // audioMasterGetTime
            _ => 0
        };
    }

    #endregion

    #region Real-Time Audio Processing

    public void ProcessStereo(Span<float> interleavedStereo, int sampleFrames)
    {
        if (!_isLoaded || IsBypassed || _isDisposed || sampleFrames <= 0)
            return;

        if (!Monitor.TryEnter(_sync))
            return;

        try
        {
            int offset = 0;
            while (offset < sampleFrames)
            {
                int block = Math.Min(MaxBlockSize, sampleFrames - offset);

                for (int i = 0; i < block; i++)
                {
                    int idx = (offset + i) * 2;
                    _inLeft[i] = interleavedStereo[idx];
                    _inRight[i] = interleavedStereo[idx + 1];
                    _outLeft[i] = 0f;
                    _outRight[i] = 0f;
                }

                if (_isVst3 && _vst3Processor != null)
                {
                    _vst3ProcessData->NumSamples = block;
                    void** procVtbl = *(void***)_vst3Processor;
                    var process = (delegate* unmanaged[Stdcall]<void*, Vst3ProcessData*, int>)procVtbl[9];
                    process(_vst3Processor, _vst3ProcessData);
                }
                else if (!_isVst3 && _vst2Effect != null && _vst2Effect->ProcessReplacing != null)
                {
                    var procRep = (delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void>)_vst2Effect->ProcessReplacing;
                    procRep(_vst2Effect, _inputsPtr, _outputsPtr, block);
                }
                else
                {
                    return;
                }

                float wet = Math.Clamp(WetMix, 0f, 1f);
                float dry = 1.0f - wet;

                for (int i = 0; i < block; i++)
                {
                    int idx = (offset + i) * 2;
                    float outL = float.IsFinite(_outLeft[i]) ? _outLeft[i] : _inLeft[i];
                    float outR = float.IsFinite(_outRight[i]) ? _outRight[i] : _inRight[i];

                    interleavedStereo[idx] = (_inLeft[i] * dry) + (outL * wet);
                    interleavedStereo[idx + 1] = (_inRight[i] * dry) + (outR * wet);
                }

                offset += block;
            }
        }
        catch
        {
            // Safeguard real-time audio thread against faulty 3rd-party plugin exceptions
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    #endregion

    #region Native Editor GUI & Parameters

    public bool TryAttachEditor(IntPtr parentHwnd, out int width, out int height)
    {
        width = 720;
        height = 480;

        lock (_sync)
        {
            if (!_isLoaded || _isDisposed) return false;

            try
            {
                if (_isVst3 && _vst3Controller != null)
                {
                    void** ctrlVtbl = *(void***)_vst3Controller;
                    var createView = (delegate* unmanaged[Stdcall]<void*, byte*, void*>)ctrlVtbl[17];

                    byte* editorName = stackalloc byte[8];
                    Encoding.ASCII.GetBytes("editor\0", new Span<byte>(editorName, 8));
                    void* view = createView(_vst3Controller, editorName);
                    if (view == null) return false;

                    _vst3PlugView = view;
                    void** viewVtbl = *(void***)_vst3PlugView;

                    byte* hwndType = stackalloc byte[8];
                    Encoding.ASCII.GetBytes("HWND\0", new Span<byte>(hwndType, 8));

                    var isSupported = (delegate* unmanaged[Stdcall]<void*, byte*, int>)viewVtbl[3];
                    if (isSupported(_vst3PlugView, hwndType) != 0)
                    {
                        CloseEditor();
                        return false;
                    }

                    var setFrame = (delegate* unmanaged[Stdcall]<void*, void*, int>)viewVtbl[12];
                    setFrame(_vst3PlugView, (void*)_plugFrameComPtr);

                    Vst3ViewRect rect = default;
                    var getSize = (delegate* unmanaged[Stdcall]<void*, Vst3ViewRect*, int>)viewVtbl[9];
                    if (getSize(_vst3PlugView, &rect) == 0)
                    {
                        int w = rect.Right - rect.Left;
                        int h = rect.Bottom - rect.Top;
                        if (w > 100 && h > 60)
                        {
                            width = w;
                            height = h;
                        }
                    }

                    var attached = (delegate* unmanaged[Stdcall]<void*, void*, byte*, int>)viewVtbl[4];
                    if (attached(_vst3PlugView, (void*)parentHwnd, hwndType) == 0)
                    {
                        if (getSize(_vst3PlugView, &rect) == 0)
                        {
                            int w = rect.Right - rect.Left;
                            int h = rect.Bottom - rect.Top;
                            if (w > 100 && h > 60)
                            {
                                width = w;
                                height = h;
                            }
                        }
                        return true;
                    }

                    CloseEditor();
                    return false;
                }
                else if (!_isVst3 && _vst2Effect != null)
                {
                    // Check effFlagsHasEditor (1 << 0)
                    if ((_vst2Effect->Flags & 1) == 0) return false;

                    Vst2ERect* rectPtr = null;
                    DispatchVst2(13, 0, 0, &rectPtr, 0f); // effEditGetRect
                    if (rectPtr != null)
                    {
                        int w = rectPtr->Right - rectPtr->Left;
                        int h = rectPtr->Bottom - rectPtr->Top;
                        if (w > 100 && h > 60)
                        {
                            width = w;
                            height = h;
                        }
                    }

                    DispatchVst2(14, 0, 0, (void*)parentHwnd, 0f); // effEditOpen
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public void CloseEditor()
    {
        lock (_sync)
        {
            try
            {
                if (_isVst3 && _vst3PlugView != null)
                {
                    void** viewVtbl = *(void***)_vst3PlugView;
                    var removed = (delegate* unmanaged[Stdcall]<void*, int>)viewVtbl[5];
                    removed(_vst3PlugView);

                    var release = (delegate* unmanaged[Stdcall]<void*, uint>)viewVtbl[2];
                    release(_vst3PlugView);
                    _vst3PlugView = null;
                }
                else if (!_isVst3 && _vst2Effect != null)
                {
                    DispatchVst2(15, 0, 0, null, 0f); // effEditClose
                }
            }
            catch
            {
            }
        }
    }

    public List<VstParameterItem> GetParameters()
    {
        var list = new List<VstParameterItem>();
        lock (_sync)
        {
            if (!_isLoaded || _isDisposed) return list;

            try
            {
                if (_isVst3 && _vst3Controller != null)
                {
                    void** ctrlVtbl = *(void***)_vst3Controller;
                    var getParamCount = (delegate* unmanaged[Stdcall]<void*, int>)ctrlVtbl[8];
                    var getParamInfo = (delegate* unmanaged[Stdcall]<void*, int, Vst3ParameterInfo*, int>)ctrlVtbl[9];
                    var getParamNorm = (delegate* unmanaged[Stdcall]<void*, uint, double>)ctrlVtbl[14];

                    int count = Math.Min(getParamCount(_vst3Controller), 64);
                    for (int i = 0; i < count; i++)
                    {
                        Vst3ParameterInfo info = default;
                        if (getParamInfo(_vst3Controller, i, &info) == 0)
                        {
                            string title = new string((char*)info.Title).TrimEnd('\0');
                            string units = new string((char*)info.Units).TrimEnd('\0');
                            double val = getParamNorm(_vst3Controller, info.ParamId);
                            list.Add(new VstParameterItem
                            {
                                Id = info.ParamId,
                                Index = i,
                                Name = string.IsNullOrWhiteSpace(title) ? $"Param {i + 1}" : title,
                                Units = units,
                                NormalizedValue = Math.Clamp(val, 0.0, 1.0)
                            });
                        }
                    }
                }
                else if (!_isVst3 && _vst2Effect != null && _vst2Effect->GetParameter != null)
                {
                    var getParam = (delegate* unmanaged[Cdecl]<AEffect*, int, float>)_vst2Effect->GetParameter;
                    int count = Math.Min(_vst2Effect->NumParams, 64);
                    byte* nameBuf = stackalloc byte[64];

                    for (int i = 0; i < count; i++)
                    {
                        NativeMemory.Clear(nameBuf, 64);
                        DispatchVst2(8, i, 0, nameBuf, 0f); // effGetParamName
                        string name = Encoding.ASCII.GetString(nameBuf, 32).TrimEnd('\0');
                        float val = getParam(_vst2Effect, i);
                        list.Add(new VstParameterItem
                        {
                            Id = (uint)i,
                            Index = i,
                            Name = string.IsNullOrWhiteSpace(name) ? $"Param {i + 1}" : name,
                            NormalizedValue = Math.Clamp(val, 0f, 1f)
                        });
                    }
                }
            }
            catch
            {
            }
        }
        return list;
    }

    public void SetParameter(uint id, int index, double normalizedValue)
    {
        lock (_sync)
        {
            if (!_isLoaded || _isDisposed) return;
            double clamped = Math.Clamp(normalizedValue, 0.0, 1.0);
            try
            {
                if (_isVst3 && _vst3Controller != null)
                {
                    void** ctrlVtbl = *(void***)_vst3Controller;
                    var setParamNorm = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)ctrlVtbl[15];
                    setParamNorm(_vst3Controller, id, clamped);
                }
                else if (!_isVst3 && _vst2Effect != null && _vst2Effect->SetParameter != null)
                {
                    var setParam = (delegate* unmanaged[Cdecl]<AEffect*, int, float, void>)_vst2Effect->SetParameter;
                    setParam(_vst2Effect, index, (float)clamped);
                }
            }
            catch
            {
            }
        }
    }

    #endregion

    #region Minimal VST3 Host COM Objects

    private IntPtr CreateHostContextVTable()
    {
        IntPtr* vtbl = (IntPtr*)NativeMemory.AllocZeroed(5, (nuint)sizeof(IntPtr));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)&HostApp_QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_Release;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, char*, int>)&HostApp_GetName;
        vtbl[4] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, byte*, byte*, void**, int>)&HostApp_CreateInstance;

        IntPtr* obj = (IntPtr*)NativeMemory.AllocZeroed(1, (nuint)sizeof(IntPtr));
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    private IntPtr CreateComponentHandlerVTable()
    {
        IntPtr* vtbl = (IntPtr*)NativeMemory.AllocZeroed(7, (nuint)sizeof(IntPtr));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)&ComponentHandler_QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_Release;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint, int>)&ComponentHandler_BeginEdit;
        vtbl[4] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint, double, int>)&ComponentHandler_PerformEdit;
        vtbl[5] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint, int>)&ComponentHandler_EndEdit;
        vtbl[6] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, int, int>)&ComponentHandler_RestartComponent;

        IntPtr* obj = (IntPtr*)NativeMemory.AllocZeroed(1, (nuint)sizeof(IntPtr));
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    private IntPtr CreatePlugFrameVTable()
    {
        IntPtr* vtbl = (IntPtr*)NativeMemory.AllocZeroed(4, (nuint)sizeof(IntPtr));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)&PlugFrame_QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, uint>)&HostStub_Release;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<void*, void*, Vst3ViewRect*, int>)&PlugFrame_ResizeView;

        IntPtr* obj = (IntPtr*)NativeMemory.AllocZeroed(1, (nuint)sizeof(IntPtr));
        obj[0] = (IntPtr)vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int HostApp_QueryInterface(void* thisPtr, byte* iid, void** obj)
    {
        *obj = thisPtr;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static uint HostStub_AddRef(void* thisPtr) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static uint HostStub_Release(void* thisPtr) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int HostApp_GetName(void* thisPtr, char* name)
    {
        ReadOnlySpan<char> hostName = "Leyline Audio Host\0";
        hostName.CopyTo(new Span<char>(name, 128));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int HostApp_CreateInstance(void* thisPtr, byte* cid, byte* iid, void** obj)
    {
        *obj = null;
        return -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int ComponentHandler_QueryInterface(void* thisPtr, byte* iid, void** obj)
    {
        *obj = thisPtr;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int ComponentHandler_BeginEdit(void* thisPtr, uint id) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int ComponentHandler_PerformEdit(void* thisPtr, uint id, double valueNormalized) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int ComponentHandler_EndEdit(void* thisPtr, uint id) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int ComponentHandler_RestartComponent(void* thisPtr, int flags) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int PlugFrame_QueryInterface(void* thisPtr, byte* iid, void** obj)
    {
        *obj = thisPtr;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof( System.Runtime.CompilerServices.CallConvStdcall )])]
    private static int PlugFrame_ResizeView(void* thisPtr, void* view, Vst3ViewRect* newSize)
    {
        if (view != null && newSize != null)
        {
            void** viewVtbl = *(void***)view;
            var onSize = (delegate* unmanaged[Stdcall]<void*, Vst3ViewRect*, int>)viewVtbl[10];
            onSize(view, newSize);
        }
        return 0;
    }

    #endregion

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isLoaded = false;

            CloseEditor();

            try
            {
                if (_isVst3)
                {
                    if (_vst3Processor != null)
                    {
                        void** procVtbl = *(void***)_vst3Processor;
                        var setProcessing = (delegate* unmanaged[Stdcall]<void*, byte, int>)procVtbl[8];
                        setProcessing(_vst3Processor, 0);
                        var release = (delegate* unmanaged[Stdcall]<void*, uint>)procVtbl[2];
                        release(_vst3Processor);
                        _vst3Processor = null;
                    }
                    if (_vst3Component != null)
                    {
                        void** compVtbl = *(void***)_vst3Component;
                        var setActive = (delegate* unmanaged[Stdcall]<void*, byte, int>)compVtbl[11];
                        setActive(_vst3Component, 0);
                    }
                    if (_vst3Controller != null)
                    {
                        void** ctrlVtbl = *(void***)_vst3Controller;
                        if (_vst3ControllerIsSeparate)
                        {
                            var term = (delegate* unmanaged[Stdcall]<void*, int>)ctrlVtbl[4];
                            term(_vst3Controller);
                        }
                        var release = (delegate* unmanaged[Stdcall]<void*, uint>)ctrlVtbl[2];
                        release(_vst3Controller);
                        _vst3Controller = null;
                    }
                    if (_vst3Component != null)
                    {
                        void** compVtbl = *(void***)_vst3Component;
                        var term = (delegate* unmanaged[Stdcall]<void*, int>)compVtbl[4];
                        term(_vst3Component);
                        var release = (delegate* unmanaged[Stdcall]<void*, uint>)compVtbl[2];
                        release(_vst3Component);
                        _vst3Component = null;
                    }
                }
                else if (_vst2Effect != null)
                {
                    DispatchVst2(12, 0, 0, null, 0f); // effMainsChanged(0)
                    DispatchVst2(1, 0, 0, null, 0f);  // effClose
                    _vst2Effect = null;
                }
            }
            catch
            {
            }

            NativeMemory.Free(_inLeft);
            NativeMemory.Free(_inRight);
            NativeMemory.Free(_outLeft);
            NativeMemory.Free(_outRight);
            NativeMemory.Free(_inputsPtr);
            NativeMemory.Free(_outputsPtr);
            NativeMemory.Free(_vst3InBus);
            NativeMemory.Free(_vst3OutBus);
            NativeMemory.Free(_vst3ProcessData);
        }
    }

    #region Native Structs

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint HostCallbackDelegate(AEffect* effect, int opcode, int index, nint value, void* ptr, float opt);

    [StructLayout(LayoutKind.Sequential)]
    private struct AEffect
    {
        public int Magic;
        public void* Dispatcher;
        public void* Process;
        public void* SetParameter;
        public void* GetParameter;
        public int NumPrograms;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Flags;
        public nint Resvd1;
        public nint Resvd2;
        public int InitialDelay;
        public int RealQualities;
        public int OffQualities;
        public float IoRatio;
        public void* Object;
        public void* User;
        public int UniqueID;
        public int Version;
        public void* ProcessReplacing;
        public void* ProcessDoubleReplacing;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vst2ERect
    {
        public short Top;
        public short Left;
        public short Bottom;
        public short Right;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PClassInfo
    {
        public fixed byte Cid[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vst3ProcessSetup
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int MaxSamplesPerBlock;
        public double SampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vst3AudioBusBuffers
    {
        public int NumChannels;
        public ulong SilenceFlags;
        public float** ChannelBuffers32;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vst3ProcessData
    {
        public int ProcessMode;
        public int SymbolicSampleSize;
        public int NumSamples;
        public int NumInputs;
        public int NumOutputs;
        public Vst3AudioBusBuffers* Inputs;
        public Vst3AudioBusBuffers* Outputs;
        public void* InputParameterChanges;
        public void* OutputParameterChanges;
        public void* InputEvents;
        public void* OutputEvents;
        public void* ProcessContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vst3ViewRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Vst3ParameterInfo
    {
        public uint ParamId;
        public fixed char Title[128];
        public fixed char ShortTitle[128];
        public fixed char Units[128];
        public int StepCount;
        public double DefaultNormalizedValue;
        public int UnitId;
        public int Flags;
    }

    #endregion
}
