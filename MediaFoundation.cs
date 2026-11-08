using System.Runtime.InteropServices;

namespace Mirror;

/// <summary>The few Media Foundation calls the app needs. GUIDs are from the Windows SDK headers.</summary>
internal static class MF
{
    public const uint Version = 0x00020070;

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");          // MF_MT_MAJOR_TYPE
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");            // MF_MT_SUBTYPE
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");         // MF_MT_AVG_BITRATE
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");      // MF_MT_INTERLACE_MODE
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");          // MF_MT_FRAME_SIZE
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");          // MF_MT_FRAME_RATE
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");   // MF_MT_PIXEL_ASPECT_RATIO
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");      // MF_MT_DEFAULT_STRIDE
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0"); // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");              // MFMediaType_Video
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");              // MFVideoFormat_RGB32
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");               // MFVideoFormat_H264
    public const uint InterlaceProgressive = 2;                                                   // MFVideoInterlace_Progressive

    [DllImport("mfplat.dll")]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

    public static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    /// <summary>Wraps a raw IMFAttributes-derived pointer (media type or attribute store) for setting values.</summary>
    public static IMFAttributes Attributes(IntPtr pointer) => (IMFAttributes)Marshal.GetObjectForIUnknown(pointer);

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
}

// COM calls go by position in the vtable, so every method up to the last one used must be listed in order.
[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
    void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
    void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem();
    void DeleteItem(); void DeleteAllItems();
    void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
    void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
    void SetDouble();
    void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void A01(); void A02(); void A03(); void A04(); void A05(); void A06(); void A07(); void A08(); void A09(); void A10();
    void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19(); void A20();
    void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29(); void A30();
    void GetSampleFlags(out uint flags);
    void SetSampleFlags(uint flags);
    void GetSampleTime(out long time);
    void SetSampleTime(long time);
    void GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void GetBufferCount(out uint count);
    void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    void Unlock();
    void GetCurrentLength(out uint length);
    void SetCurrentLength(uint length);
    void GetMaxLength(out uint length);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    void GetStreamSelection(uint stream, out int selected);
    void SetStreamSelection(uint stream, int selected);
    void GetNativeMediaType(uint stream, uint index, out IntPtr type);
    void GetCurrentMediaType(uint stream, out IntPtr type);
    void SetCurrentMediaType(uint stream, IntPtr reserved, IntPtr type);
    void SetCurrentPosition(ref Guid format, IntPtr position);
    void ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint streamFlags,
        out long timestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    void AddStream(IntPtr type, out uint index);
    void SetInputMediaType(uint index, IntPtr type, IntPtr encodingParameters);
    void BeginWriting();
    void WriteSample(uint index, [MarshalAs(UnmanagedType.Interface)] IMFSample sample);
    void SendStreamTick(uint index, long timestamp);
    void PlaceMarker(uint index, IntPtr context);
    void NotifyEndOfSegment(uint index);
    void Flush(uint index);
    void Finish();
}
