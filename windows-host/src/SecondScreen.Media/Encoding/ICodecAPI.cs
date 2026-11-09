using System.Runtime.InteropServices;

namespace SecondScreen.Media.Encoding;

[ComImport]
[Guid("901db863-a4e2-47ac-a801-46e30ed64803")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICodecAPI
{
    [PreserveSig]
    int IsSupported([In] ref Guid api);

    [PreserveSig]
    int IsModifiable([In] ref Guid api);

    [PreserveSig]
    int GetParameterRange([In] ref Guid api, [Out] out object valueMin, [Out] out object valueMax, [Out] out object steppingDelta);

    [PreserveSig]
    int GetParameterValues([In] ref Guid api, [Out] out IntPtr values, [Out] out uint valuesCount);

    [PreserveSig]
    int GetValue([In] ref Guid api, [Out] out object value);

    [PreserveSig]
    int SetValue([In] ref Guid api, [In] ref object value);

    [PreserveSig]
    int RegisterForEvent([In] ref Guid api, [In] IntPtr userData);

    [PreserveSig]
    int UnregisterForEvent([In] ref Guid api);

    [PreserveSig]
    int SetAllDefaults();

    [PreserveSig]
    int SetValueWithNotify([In] ref Guid api, [In] ref object value, [Out] out IntPtr changedParam, [Out] out uint changedParamCount);

    [PreserveSig]
    int SetAllDefaultsWithNotify([Out] out IntPtr changedParam, [Out] out uint changedParamCount);

    [PreserveSig]
    int GetAllSettings([In] IntPtr pStream);

    [PreserveSig]
    int SetAllSettings([In] IntPtr pStream);
}

public static class CodecApiGuids
{
    // CODECAPI_AVLowLatencyMode: VT_BOOL (-1 for TRUE)
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // CODECAPI_AVEncMPVDefaultBPictureCount: VT_UI4 (0 = no B frames)
    public static readonly Guid DefaultBPictureCount = new("8d390a0e-4367-4a49-ac14-fb38ae365706");

    // CODECAPI_AVEncCommonRateControlMode: VT_UI4 (0 = CBR, 1 = PeakConstrainedVBR, 2 = UnconstrainedVBR, 3 = Quality)
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    // CODECAPI_AVEncCommonMeanBitRate: VT_UI4 (bitrate in bps)
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-e37f8e120572");

    // CODECAPI_AVEncCommonMaxBitRate: VT_UI4
    public static readonly Guid MaxBitRate = new("96510f4d-4522-48a5-b3e3-a6b6f4a861cf");

    // CODECAPI_AVEncCommonBufferSize: VT_UI4
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c5b-8106-3773de03277b");

    // CODECAPI_AVEncCommonQuality: VT_UI4 (0..100)
    public static readonly Guid Quality = new("fcbf5771-91ea-4e00-82d2-516e0769e3ab");

    // CODECAPI_AVEncAdaptiveMode: VT_UI4 (0 = None)
    public static readonly Guid AdaptiveMode = new("31637042-45e0-47b7-b08e-595304620021");

    // CODECAPI_AVEncMPVGOPSize: VT_UI4 (Keyframe interval)
    public static readonly Guid GopSize = new("95f42fac-4e81-4234-a403-6a9d5970c810");

    // CODECAPI_AVEncVideoForceKeyFrame: VT_UI4 (1 = Force next frame to be an IDR Keyframe)
    public static readonly Guid ForceKeyframe = new("398c1b98-8353-475a-9e28-13306d773018");

    public static void TrySet(ICodecAPI? codecApi, Guid propertyGuid, object value)
    {
        if (codecApi == null) return;
        try
        {
            var guid = propertyGuid;
            if (codecApi.IsSupported(ref guid) == 0) // S_OK
            {
                object val = value;
                codecApi.SetValue(ref guid, ref val);
            }
        }
        catch { }
    }
}

