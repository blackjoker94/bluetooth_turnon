using System;
using System.Runtime.InteropServices;

namespace bluetooth_turnon
{
    #region Core Audio & Kernel Streaming Interop

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            int dataFlow,
            uint dwStateMask,
            out IMMDeviceCollection ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            int dataFlow,
            int role,
            out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
            out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);

        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            [In] ref Guid iid,
            uint dwClsCtx,
            IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig]
        int OpenPropertyStore(
            uint stgmAccess,
            out IPropertyStore ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid guid, uint id)
        {
            fmtid = guid;
            pid = id;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pwszVal;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public byte byteVal;
        [FieldOffset(8)] public long longVal;
        [FieldOffset(16)] private long _padding64;
    }

    [ComImport]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint pCount);

        [PreserveSig]
        int GetConnector(uint nIndex, [MarshalAs(UnmanagedType.Interface)] out object ppConnector);

        [PreserveSig]
        int GetSubunitCount(out uint pCount);

        [PreserveSig]
        int GetSubunit(uint nIndex, out IntPtr ppSubunit);

        [PreserveSig]
        int GetPartById(uint nId, [MarshalAs(UnmanagedType.Interface)] out object ppPart);

        [PreserveSig]
        int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string pDeviceId);

        [PreserveSig]
        int GetSignalPath(IPart pIPartFrom, IPart pIPartTo, bool bRejectProcesses, out IntPtr ppParts);
    }

    [ComImport]
    [Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnector
    {
        [PreserveSig]
        int GetType(out int pType);

        [PreserveSig]
        int GetDataFlow(out int pFlow);

        [PreserveSig]
        int ConnectTo(IConnector pConnectTo);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int IsConnected(out bool pbConnected);

        [PreserveSig]
        int GetConnectedTo([MarshalAs(UnmanagedType.Interface)] out object ppConTo);

        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrConnectorId);

        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrDeviceId);
    }

    [ComImport]
    [Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPart
    {
        [PreserveSig]
        int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrName);

        [PreserveSig]
        int GetLocalId(out uint pnId);

        [PreserveSig]
        int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string ppwstrGlobalId);

        [PreserveSig]
        int GetPartType(out int pPartType);

        [PreserveSig]
        int GetSubType(out Guid pSubType);

        [PreserveSig]
        int GetControlInterfaceCount(out uint pCount);

        [PreserveSig]
        int GetControlInterface(uint nIndex, out IntPtr ppInterfaceDesc);

        [PreserveSig]
        int EnumPartsIncoming(out IntPtr ppParts);

        [PreserveSig]
        int EnumPartsOutgoing(out IntPtr ppParts);

        [PreserveSig]
        int GetTopologyObject(out IDeviceTopology ppTopology);

        [PreserveSig]
        int Activate(uint dwClsCtx, [In] ref Guid refiid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IKsControl
    {
        [PreserveSig]
        int KsProperty(
            [In] ref KSIDENTIFIER Property,
            uint PropertyLength,
            IntPtr PropertyData,
            uint DataLength,
            out uint BytesReturned);

        [PreserveSig]
        int KsMethod(
            [In] ref KSIDENTIFIER Method,
            uint MethodLength,
            IntPtr MethodData,
            uint DataLength,
            out uint BytesReturned);

        [PreserveSig]
        int KsEvent(
            [In] ref KSIDENTIFIER Event,
            uint EventLength,
            IntPtr EventData,
            uint DataLength,
            out uint BytesReturned);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KSIDENTIFIER
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    #endregion
}
