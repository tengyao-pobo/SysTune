using System.Runtime.InteropServices;

namespace SysTune.App.Interop;

/// <summary>Enumerates hardware display adapters via DXGI (name, VRAM, LUID). No GPU vendor SDK needed.</summary>
internal static class Dxgi
{
    internal sealed record AdapterInfo(string Name, long Luid, ulong DedicatedVideoMemory, ulong SharedSystemMemory, uint VendorId);

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    public static List<AdapterInfo> GetHardwareAdapters()
    {
        var result = new List<AdapterInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory is null) return result;
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) == DXGI_ERROR_NOT_FOUND || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) < 0) continue;
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                    if (desc.VendorId == 0x1414) continue; // Microsoft Basic Render Driver
                    var luid = ((long)desc.AdapterLuidHigh << 32) | desc.AdapterLuidLow;
                    if (result.Exists(a => a.Luid == luid)) continue;
                    result.Add(new AdapterInfo(desc.Description.Trim(), luid, desc.DedicatedVideoMemory, desc.SharedSystemMemory, desc.VendorId));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return result;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemoryRaw;
        public nuint DedicatedSystemMemoryRaw;
        public nuint SharedSystemMemoryRaw;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;

        public readonly ulong DedicatedVideoMemory => DedicatedVideoMemoryRaw;

        public readonly ulong SharedSystemMemory => SharedSystemMemoryRaw;
    }

    // Only the vtable slots we call need real signatures; the rest are placeholders that keep the slot order.
    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();

        void SetPrivateDataInterface();

        void GetPrivateData();

        void GetParent();

        void EnumAdapters();

        void MakeWindowAssociation();

        void GetWindowAssociation();

        void CreateSwapChain();

        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? ppAdapter);
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();

        void SetPrivateDataInterface();

        void GetPrivateData();

        void GetParent();

        void EnumOutputs();

        void GetDesc();

        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
