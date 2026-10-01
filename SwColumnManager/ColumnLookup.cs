using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SwColumnManager
{
    // Resolves "which existing Explorer column is this" to a real
    // PROPERTYKEY, via IShellFolder2::MapColumnToSCID - the actual Windows
    // API for mapping a column index to its identity. Needed because most
    // generic Windows columns (Authors, Title, Tags, ...) are built into
    // propsys.dll itself, not registered via an external .propdesc file -
    // confirmed by checking the PropertySchema registry key directly, which
    // only lists third-party registrations (Office's, SolidWorks's), not
    // these. See CLAUDE.md, Phase 4 / Issue 2.
    //
    // Deliberately kept out of SwPropertyHandler: this COM interop is new
    // and was genuinely easy to get wrong (an early version of this exact
    // code AccessViolationException'd from a vtable mismatch before the
    // signatures below were fixed) - a crash here should only ever take
    // down this standalone editor, never explorer.exe.
    internal static class ColumnLookup
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // Flattened (not using C# interface inheritance) and every method
        // this class never calls simplified to plain IntPtr parameters -
        // only BindToObject and MapColumnToSCID need to be exactly right.
        // Order/count must still match the real vtable exactly (IShellFolder's
        // 10 methods, then IShellFolder2's 7).
        [ComImport]
        [Guid("93F2F68C-1D1B-11D3-A30E-00C04F79ABD1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder2
        {
            // --- IShellFolder (10) ---
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc, IntPtr pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
            void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, IntPtr apidl, IntPtr rgfInOut);
            void GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl, IntPtr pszName, uint uFlags, out IntPtr ppidlOut);

            // --- IShellFolder2 (7) ---
            void GetDefaultSearchGUID(out Guid pguid);
            void EnumSearches(out IntPtr ppenum);
            void GetDefaultColumn(uint dwRes, out uint pSort, out uint pDisplay);
            void GetDefaultColumnState(uint iColumn, out uint pcsFlags);
            void GetDetailsEx(IntPtr pidl, IntPtr pscid, IntPtr pv);
            void GetDetailsOf(IntPtr pidl, uint iColumn, IntPtr psd);
            void MapColumnToSCID(uint iColumn, out PROPERTYKEY pscid);
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out object ppshf);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        public class ColumnInfo
        {
            public string Name;
            public PROPERTYKEY Key;

            public override string ToString() => Name;
        }

        // Enumerates every named Explorer column this PC knows about, with
        // its real PROPERTYKEY. Uses the user's Documents folder as context
        // (any real folder works - this just needs *a* shell folder to ask).
        public static List<ColumnInfo> GetAllColumns()
        {
            var results = new List<ColumnInfo>();

            string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            int hr = SHGetDesktopFolder(out object desktopObj);
            if (hr != 0) return results;
            var desktop = (IShellFolder2)desktopObj;

            hr = SHParseDisplayName(folderPath, IntPtr.Zero, out IntPtr pidl, 0, out uint _);
            if (hr != 0) return results;

            Guid iidShellFolder2 = typeof(IShellFolder2).GUID;
            desktop.BindToObject(pidl, IntPtr.Zero, ref iidShellFolder2, out object folder2Obj);
            var folder2 = (IShellFolder2)folder2Obj;

            dynamic shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic shellFolder = shellApp.NameSpace(folderPath);

            for (uint i = 0; i < 400; i++)
            {
                string name;
                try { name = shellFolder.GetDetailsOf(shellFolder.Items(), (int)i); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;

                try
                {
                    folder2.MapColumnToSCID(i, out PROPERTYKEY pkey);
                    results.Add(new ColumnInfo { Name = name, Key = pkey });
                }
                catch
                {
                    // Skip columns MapColumnToSCID can't resolve rather than fail the whole lookup.
                }
            }

            return results;
        }
    }
}
