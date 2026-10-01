using System;
using System.Runtime.InteropServices;

namespace SwPropertyHandler
{
    // PROPERTYKEY as Windows actually defines it: a 16-byte GUID followed by
    // a 4-byte DWORD, 20 bytes total, no padding.
    //
    // IMPORTANT: SwFilterDump's NativeMethods.PROPERTYKEY declares `pid` as a
    // C# `long` (8 bytes), making its managed struct 24 bytes instead of the
    // real 20. That was harmless there because it only ever *reads* a
    // PROPERTYKEY a native callee already wrote - .NET over-allocates the
    // buffer, the native side writes 20 real bytes, the leftover 4 bytes stay
    // zero, and zero-extending a small positive 32-bit value into a 64-bit
    // field happens to produce the right number on this little-endian CPU.
    // That trick does NOT work in the other direction: here, native code
    // (Explorer) allocates an exact 20-byte buffer and passes it to our
    // GetAt as the out parameter. If our struct were 24 bytes, the marshaler
    // would think it's writing 24 bytes into a buffer the caller only
    // allocated 20 for - a real buffer-overrun risk inside explorer.exe.
    // `pid` must be `uint` here, not `long`.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    // Declared for IMPLEMENTING (not importing) - no [ComImport]. The Guid
    // must match the real native IInitializeWithFile IID exactly, since
    // that's how Explorer's QueryInterface finds our implementation.
    [ComVisible(true)]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    // Declared for IMPLEMENTING. GetValue/SetValue use `object` marshaled as
    // UnmanagedType.Struct rather than a hand-built PROPVARIANT: the CLR's
    // default COM interop marshaler converts a managed string into a native
    // VARIANT with VT_BSTR, which is binary-layout-compatible with
    // PROPVARIANT for the VT_BSTR case - this is the standard technique for
    // implementing IPropertyStore in managed code without hand-marshaling
    // PROPVARIANT's full union.
    [ComVisible(true)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint cProps);

        void GetAt(uint iProp, out PROPERTYKEY pkey);

        void GetValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Struct)] out object pv);

        void SetValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Struct)] ref object pv);

        void Commit();
    }
}
