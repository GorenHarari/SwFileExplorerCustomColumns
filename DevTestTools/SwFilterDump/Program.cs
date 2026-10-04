using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SwFilterDump
{
    // Standard live per-item property interface (not defined in IFilterTextReader's
    // NativeMethods.cs, since that library only needs IFilter). If the filter COM object also
    // implements this, it would explain how Explorer's ExtendedProperty/column display gets
    // Description/LastSavedWith/OpenTime through a path that never touches IFilter::GetChunk.
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out NativeMethods.PROPERTYKEY pkey);

        // Real IPropertyStore::GetValue fills a caller-allocated PROPVARIANT in place (by value),
        // unlike IFilter::GetValue which CoTaskMemAllocs a new one - different marshaling needed.
        [PreserveSig]
        int GetValue(ref NativeMethods.PROPERTYKEY key, out NativeMethods.PROPVARIANT pv);

        [PreserveSig]
        int SetValue(ref NativeMethods.PROPERTYKEY key, ref NativeMethods.PROPVARIANT pv);

        [PreserveSig]
        int Commit();
    }

    // IInitializeWithFile - how a real property handler is typically told which file to read,
    // before GetValue can be called.
    [ComImport]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithFile
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    // The InfoTip handler interface - what generates the hover-tooltip text for a file in
    // Explorer. Registered for .sldprt via ShellEx {00021500-...} -> CLSID {D5011C85-...}
    // ("SolidWorks file InfoTips Class") in sldwinshellextu.dll. Untested candidate for where
    // Description/LastSavedWith/OpenTime's live values might actually come from.
    [ComImport]
    [Guid("00021500-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IQueryInfo
    {
        void GetInfoTip(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszTip);
        void GetInfoFlags(out uint pdwFlags);
    }

    // Older, generic OLE structured-storage property mechanism (distinct from IFilter and from
    // IPropertyStore). If the compound file itself physically carries a real property set stream
    // under the SolidWorks Document formatID, ANY generic Windows code - not just SolidWorks'
    // own components - can read it directly via StgOpenStorage, with no registered handler
    // involved at all. This is the hypothesis under test: that Description/LastSavedWith/OpenTime
    // are stored this way (which is why they work live) while Material/Number/etc. are not
    // (which is why they only ever come from sldsearchifilter.dll's IFilter, used for indexing).
    [ComImport]
    [Guid("0000013A-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertySetStorage
    {
        void Create(ref Guid rfmtid, ref Guid pclsid, uint grfFlags, uint grfMode,
            out IPropertyStorage ppprstg);

        [PreserveSig]
        int Open(ref Guid rfmtid, uint grfMode, out IPropertyStorage ppprstg);

        void Delete(ref Guid rfmtid);
        void Enum(out object ppenum);
    }

    [ComImport]
    [Guid("00000138-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStorage
    {
        void ReadMultiple(uint cpspec, [In] NativeMethods.PROPSPEC[] rgpspec,
            [Out] NativeMethods.PROPVARIANT[] rgpropvar);

        void WriteMultiple(uint cpspec, [In] NativeMethods.PROPSPEC[] rgpspec,
            [In] NativeMethods.PROPVARIANT[] rgpropvar, uint propidNameFirst);

        void DeleteMultiple(uint cpspec, [In] NativeMethods.PROPSPEC[] rgpspec);
        void ReadPropertyNames(uint cpropid, [In] uint[] rgpropid, [Out] string[] rglpwstrName);
        void WritePropertyNames(uint cpropid, [In] uint[] rgpropid, [In] string[] rglpwstrName);
        void DeletePropertyNames(uint cpropid, [In] uint[] rgpropid);
        void Commit(uint grfCommitFlags);
        void Revert();

        [PreserveSig]
        int Enum(out IEnumSTATPROPSTG ppenum);

        void SetTimes(ref System.Runtime.InteropServices.ComTypes.FILETIME pctime,
            ref System.Runtime.InteropServices.ComTypes.FILETIME patime,
            ref System.Runtime.InteropServices.ComTypes.FILETIME pmtime);

        void SetClass(ref Guid clsid);
        void Stat(out STATPROPSETSTG statpsstg);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STATPROPSETSTG
    {
        public Guid fmtid;
        public Guid clsid;
        public uint grfFlags;
        public System.Runtime.InteropServices.ComTypes.FILETIME mtime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ctime;
        public System.Runtime.InteropServices.ComTypes.FILETIME atime;
        public uint dwOSVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STATPROPSTG
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpwstrName;
        public uint propid;
        public ushort vt;
    }

    [ComImport]
    [Guid("00000139-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumSTATPROPSTG
    {
        [PreserveSig]
        int Next(uint celt, [Out] STATPROPSTG[] rgelt, out uint pceltFetched);

        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumSTATPROPSTG ppenum);
    }

    // Drives the registered Windows IFilter for a .sldprt/.sldasm/.slddrw file directly -
    // the same low-level mechanism Windows Search uses - and prints every chunk it emits.
    // This bypasses the IFilterTextReader library's FilterReader/FilterLoader classes: those
    // wrap the load in a stream (IPersistStream via a custom IStream) and silently swallow the
    // real COM error when that fails, falling back to IPersistFile only if supported - and for
    // sldsearchifilter.dll neither path worked cleanly through that library. Here we load the
    // whole file into an HGLOBAL COM stream ourselves and drive IFilter step by step, so nothing
    // gets swallowed.
    internal class Program
    {
        // Known statically from the registry (HKCR\.sldprt\PersistentHandler -> CLSID -> InprocServer32),
        // confirmed earlier in this investigation - no need to re-look these up at runtime.
        private const string DllPath = @"C:\Program Files\Common Files\SOLIDWORKS Shared\sldsearchifilter.dll";
        private static readonly Guid FilterClsid = new Guid("AA261FDE-AB29-429c-9DF1-0EDEABAAFB7D");

        // The 24 Solidworks.Document.* properties we already extracted from
        // solidworksproperties.propdesc, keyed by (formatID, propID), so chunks that match get a
        // friendly label printed alongside the raw GUID/propID even if PSGetNameFromPropertyKey
        // can't resolve them (e.g. because they lack a labelInfo).
        private static readonly (Guid fmtid, int pid, string name)[] KnownSwProps =
        {
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 100, "Solidworks.Document.Description"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 101, "Solidworks.Document.Configurations"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 102, "Solidworks.Document.References"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 103, "Solidworks.Document.Features"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 104, "Solidworks.Document.CustomProperties"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 105, "Solidworks.Document.Notes"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 106, "Solidworks.Document.Tables"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 107, "Solidworks.Document.Material"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 108, "Solidworks.Document.Number"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 109, "Solidworks.Document.UserDescription"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 110, "Solidworks.Document.Project"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 111, "Solidworks.Document.Author"),
            (new Guid("29ED838A-063D-4502-89C4-BD1A79DFBE21"), 112, "Solidworks.Document.LastSavedWith"),
            (new Guid("3930FA19-37C3-4B0F-8AFA-55B5E45F6A45"), 113, "Solidworks.Document.OpenTime"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 201, "Solidworks.Document.Sketches"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 202, "Solidworks.Document.Layers"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 203, "Solidworks.Document.Sheets"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 204, "Solidworks.Document.Attributes"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 205, "Solidworks.Document.Cuts"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 206, "Solidworks.Document.Extrusions"),
            (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 207, "Solidworks.Document.Blocks"),
        };

        private static string ResolveName(Guid fmtid, int pid)
        {
            foreach (var p in KnownSwProps)
                if (p.fmtid == fmtid && p.pid == pid)
                    return p.name;

            // Fall back to asking Windows' own Property System (works for any registered
            // property, not just the SolidWorks ones, e.g. generic System.* properties).
            var key = new NativeMethods.PROPERTYKEY { fmtid = fmtid, pid = pid };
            try
            {
                if (NativeMethods.PSGetNameFromPropertyKey(ref key, out var canonical) == 0 &&
                    !string.IsNullOrEmpty(canonical))
                    return canonical;
            }
            catch
            {
                // ignore - fall through to raw form
            }

            return $"{{{fmtid}}}#{pid}";
        }

        private const string PropDescPath =
            @"C:\ProgramData\SolidWorks\SOLIDWORKS 2019\lang\english\xmlschema\solidworksproperties.propdesc";

        // Forces Windows to re-parse the (edited) .propdesc file - registration only happens once
        // at install time, so an edit alone has no effect until this runs.
        private static void RefreshSchema()
        {
            Console.WriteLine($"Unregistering: {PropDescPath}");
            int unregHr = NativeMethods.PSUnregisterPropertySchema(PropDescPath);
            Console.WriteLine($"PSUnregisterPropertySchema returned: 0x{unregHr:X8}");

            Console.WriteLine($"Re-registering: {PropDescPath}");
            int regHr = NativeMethods.PSRegisterPropertySchema(PropDescPath);
            Console.WriteLine($"PSRegisterPropertySchema returned: 0x{regHr:X8}");
        }

        private static void Main(string[] args)
        {
            // sldpropertyhandler.dll (and possibly others) depend on sibling SolidWorks DLLs that
            // aren't found via the default DLL search order when run outside a real SolidWorks
            // process - add both install directories to our own process PATH so LoadLibrary can
            // resolve those dependencies.
            string swMainDir = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS";
            string swSharedDir = @"C:\Program Files\Common Files\SOLIDWORKS Shared";
            Environment.SetEnvironmentVariable("PATH",
                swMainDir + ";" + swSharedDir + ";" + Environment.GetEnvironmentVariable("PATH"));

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: SwFilterDump.exe \"C:\\path\\to\\file.SLDPRT\"");
                Console.WriteLine("       SwFilterDump.exe --refresh-schema");
                return;
            }

            if (args[0].Equals("--refresh-schema", StringComparison.OrdinalIgnoreCase))
            {
                RefreshSchema();
                if (!Console.IsInputRedirected)
                {
                    Console.WriteLine("Done. Press any key to exit.");
                    Console.ReadKey();
                }
                return;
            }

            string filePath = Path.GetFullPath(args[0]);
            Console.WriteLine($"Filter DLL: {DllPath}");
            Console.WriteLine($"Filter CLSID: {FilterClsid}");
            Console.WriteLine($"File: {filePath}");
            Console.WriteLine();

            IntPtr dllHandle = NativeMethods.LoadLibrary(DllPath);
            if (dllHandle == IntPtr.Zero)
            {
                Console.WriteLine($"LoadLibrary failed. Win32 error: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                IntPtr getClassObjectPtr = NativeMethods.GetProcAddress(dllHandle, "DllGetClassObject");
                if (getClassObjectPtr == IntPtr.Zero)
                {
                    Console.WriteLine("GetProcAddress(DllGetClassObject) failed.");
                    return;
                }

                var dllGetClassObject =
                    Marshal.GetDelegateForFunctionPointer<NativeMethods.DllGetClassObject>(getClassObjectPtr);

                Guid clsid = FilterClsid;
                Guid classFactoryIid = typeof(NativeMethods.IClassFactory).GUID;
                int hr = dllGetClassObject(ref clsid, ref classFactoryIid, out object classFactoryObj);
                if (hr != 0)
                {
                    Console.WriteLine($"DllGetClassObject failed. HRESULT: 0x{hr:X8}");
                    return;
                }

                var classFactory = (NativeMethods.IClassFactory)classFactoryObj;

                Guid filterIid = typeof(NativeMethods.IFilter).GUID;
                classFactory.CreateInstance(null, ref filterIid, out object filterObj);
                Marshal.ReleaseComObject(classFactory);

                Console.WriteLine("Created filter COM object.");
                Console.WriteLine($"  Implements IPersistFile:      {filterObj is IPersistFile}");
                Console.WriteLine($"  Implements IPersistStream:    {filterObj is NativeMethods.IPersistStream}");
                Console.WriteLine($"  Implements IPropertyStore:    {filterObj is IPropertyStore}");
                Console.WriteLine($"  Implements IPropertySetStorage: {filterObj is IPropertySetStorage}");
                Console.WriteLine();

                var filter = (NativeMethods.IFilter)filterObj;

                bool loaded = false;

                if (filterObj is IPersistFile persistFile)
                {
                    try
                    {
                        persistFile.Load(filePath, 0); // STGM_READ
                        loaded = true;
                        Console.WriteLine("Loaded via IPersistFile.Load - OK");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"IPersistFile.Load threw: {ex.Message}");
                    }
                }

                if (!loaded && filterObj is NativeMethods.IPersistStream persistStream)
                {
                    byte[] bytes = File.ReadAllBytes(filePath);
                    IntPtr hGlobal = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, hGlobal, bytes.Length);

                    int chr = NativeMethods.CreateStreamOnHGlobal(hGlobal, true, out IStream comStream);
                    if (chr != 0)
                    {
                        Console.WriteLine($"CreateStreamOnHGlobal failed. HRESULT: 0x{chr:X8}");
                        return;
                    }

                    try
                    {
                        persistStream.Load(comStream);
                        loaded = true;
                        Console.WriteLine("Loaded via IPersistStream.Load (HGLOBAL stream) - OK");
                    }
                    catch (Exception ex)
                    {
                        var comEx = ex as COMException;
                        string hrText = comEx != null ? $" HRESULT: 0x{comEx.ErrorCode:X8}" : "";
                        Console.WriteLine($"IPersistStream.Load threw: {ex.Message}{hrText}");
                    }
                }

                if (!loaded)
                {
                    Console.WriteLine("Could not load the file into the filter via any supported interface.");
                    return;
                }

                // Optional 3rd arg selects which attribute-category flag(s) to request - these are
                // meant to be semantically distinct categories in a well-behaved filter, so asking
                // for just one (instead of OR-ing all three together, as the default below does)
                // might surface a genuinely different, smaller set - e.g. if Description is
                // categorized as an "index" attribute distinct from the bulk "crawl" set we saw.
                var categoryFlags = NativeMethods.IFILTER_INIT.APPLY_INDEX_ATTRIBUTES |
                                    NativeMethods.IFILTER_INIT.APPLY_CRAWL_ATTRIBUTES |
                                    NativeMethods.IFILTER_INIT.APPLY_OTHER_ATTRIBUTES;
                if (args.Length > 2)
                {
                    switch (args[2].ToLowerInvariant())
                    {
                        case "index":
                            categoryFlags = NativeMethods.IFILTER_INIT.APPLY_INDEX_ATTRIBUTES;
                            break;
                        case "crawl":
                            categoryFlags = NativeMethods.IFILTER_INIT.APPLY_CRAWL_ATTRIBUTES;
                            break;
                        case "other":
                            categoryFlags = NativeMethods.IFILTER_INIT.APPLY_OTHER_ATTRIBUTES;
                            break;
                        case "none":
                            categoryFlags = NativeMethods.IFILTER_INIT.NONE;
                            break;
                    }
                    Console.WriteLine($"Using category flags: {categoryFlags}");
                }

                var initFlags = NativeMethods.IFILTER_INIT.CANON_HYPHENS |
                                 NativeMethods.IFILTER_INIT.CANON_PARAGRAPHS |
                                 NativeMethods.IFILTER_INIT.CANON_SPACES |
                                 categoryFlags |
                                 NativeMethods.IFILTER_INIT.HARD_LINE_BREAKS;

                // Optional 2nd arg: comma-separated short property names (matched against
                // KnownSwProps) to request EXPLICITLY via Init's aAttributes array, instead of
                // asking for the filter's default crawl set. This is how a caller that wants one
                // specific property (which is presumably what Explorer's live column query does)
                // would call IFilter - lets us test whether Description behaves differently from
                // Material/Project/etc. even when both are asked for the same explicit way.
                IntPtr attrArrayPtr = IntPtr.Zero;
                int attrCount = 0;
                if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
                {
                    var names = args[1].Split(',');
                    var specs = new List<(Guid fmtid, int pid, string name)>();
                    foreach (var raw in names)
                    {
                        var n = raw.Trim();
                        var match = Array.Find(KnownSwProps,
                            p => p.name.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                                 p.name.Equals("Solidworks.Document." + n, StringComparison.OrdinalIgnoreCase));
                        if (match.name == null)
                        {
                            Console.WriteLine($"Unknown property name '{n}' - skipping.");
                            continue;
                        }
                        specs.Add(match);
                    }

                    if (specs.Count > 0)
                    {
                        attrCount = specs.Count;
                        int fullPropSpecSize = Marshal.SizeOf<NativeMethods.FULLPROPSPEC>();
                        attrArrayPtr = Marshal.AllocHGlobal(fullPropSpecSize * attrCount);
                        for (int i = 0; i < specs.Count; i++)
                        {
                            var fp = new NativeMethods.FULLPROPSPEC
                            {
                                guidPropSet = specs[i].fmtid,
                                psProperty = new NativeMethods.PROPSPEC
                                {
                                    ulKind = NativeMethods.PROPSPECKIND.PRSPEC_PROPID,
                                    data = new IntPtr(specs[i].pid)
                                }
                            };
                            Marshal.StructureToPtr(fp, attrArrayPtr + i * fullPropSpecSize, false);
                        }

                        Console.WriteLine($"Requesting {attrCount} explicit propert{(attrCount == 1 ? "y" : "ies")}: " +
                                           string.Join(", ", specs.ConvertAll(s => s.name)));
                        Console.WriteLine();
                    }
                }

                try
                {
                    var initResult = filter.Init(initFlags, attrCount, attrArrayPtr, out var pdwFlags);
                    Console.WriteLine($"Init() returned: {initResult} (pdwFlags={pdwFlags})");
                    Console.WriteLine();

                    if (initResult != NativeMethods.IFilterReturnCode.S_OK)
                    {
                        Console.WriteLine("Init did not return S_OK - stopping.");
                        return;
                    }

                    DumpChunks(filter);
                }
                finally
                {
                    if (attrArrayPtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(attrArrayPtr);

                    // Release the filter COM object (and its open handle on the file) before
                    // FreeLibrary - otherwise the object is left dangling in an unloaded module,
                    // and the file may still appear open/locked to a subsequent StgOpenStorage.
                    Marshal.FinalReleaseComObject(filterObj);
                }
            }
            finally
            {
                NativeMethods.FreeLibrary(dllHandle);
            }

            DumpPropertySets(filePath);
            TestInfoTips(filePath);
            TestPropertyHandler(filePath);

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.WriteLine("Done. Press any key to exit.");
                Console.ReadKey();
            }
        }

        // IStorage isn't needed for any of its own methods here - we only pass the resulting
        // object through and QueryInterface-cast it to IPropertySetStorage - so 'object' avoids
        // needing a full IStorage interop declaration.
        [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int StgOpenStorage(string pwcsName,
            [MarshalAs(UnmanagedType.Interface)] object pstgPriority, uint grfMode,
            IntPtr snbExclude, uint reserved,
            [MarshalAs(UnmanagedType.Interface)] out object ppstgOpen);

        private const uint STGM_READ = 0x00000000;
        private const uint STGM_SHARE_DENY_NONE = 0x00000040;

        // Reads the file's OWN embedded OLE property-set streams directly via StgOpenStorage -
        // completely independent of sldsearchifilter.dll or any other registered SolidWorks
        // component. This tests whether Description/LastSavedWith/OpenTime are physically stored
        // in the file as real, generic Windows property sets (which any code could read, which
        // would explain why they work live while Material/etc. - only ever produced on the fly by
        // the filter's own parsing logic - do not).
        private static void DumpPropertySets(string filePath)
        {
            Console.WriteLine();
            Console.WriteLine("--- Reading the file's own embedded OLE property sets directly (no SolidWorks component involved) ---");

            var fmtidsToTry = new (Guid fmtid, string label)[]
            {
                (new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), "Solidworks Document (Description/Configurations/Material/Number/etc.)"),
                (new Guid("29ED838A-063D-4502-89C4-BD1A79DFBE21"), "Solidworks LastSavedWith"),
                (new Guid("3930FA19-37C3-4B0F-8AFA-55B5E45F6A45"), "Solidworks OpenTime"),
                (new Guid("D5CDD505-2E9C-101B-9397-08002B2CF9AE"), "Classic UserDefinedProperties"),
                (new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), "Classic SummaryInformation"),
            };

            int hr = StgOpenStorage(filePath, null, STGM_READ | STGM_SHARE_DENY_NONE, IntPtr.Zero, 0,
                out object storage);
            if (hr != 0)
            {
                Console.WriteLine($"StgOpenStorage failed. HRESULT: 0x{hr:X8}");
                return;
            }

            try
            {
                if (!(storage is IPropertySetStorage propSetStorage))
                {
                    Console.WriteLine("Storage does not implement IPropertySetStorage.");
                    return;
                }

                foreach (var (fmtid, label) in fmtidsToTry)
                {
                    var fmtidCopy = fmtid;
                    int openHr = propSetStorage.Open(ref fmtidCopy, STGM_READ | STGM_SHARE_DENY_NONE,
                        out IPropertyStorage propStorage);

                    if (openHr != 0)
                    {
                        Console.WriteLine($"  [{label}] {fmtid} - not present (HRESULT: 0x{openHr:X8})");
                        continue;
                    }

                    Console.WriteLine($"  [{label}] {fmtid} - PRESENT. Properties physically stored:");
                    try
                    {
                        int enumHr = propStorage.Enum(out IEnumSTATPROPSTG enumStg);
                        if (enumHr != 0 || enumStg == null)
                        {
                            Console.WriteLine($"    (Enum failed, HRESULT: 0x{enumHr:X8})");
                            continue;
                        }

                        var elt = new STATPROPSTG[1];
                        while (enumStg.Next(1, elt, out uint fetched) == 0 && fetched == 1)
                        {
                            string name = elt[0].lpwstrName ?? "(no name)";
                            Console.WriteLine($"    propid={elt[0].propid} vt={elt[0].vt} name={name}");
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(propStorage);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(storage);
            }
        }

        private static readonly Guid InfoTipsClsid = new Guid("D5011C85-B26C-41D4-BD38-E7934E494379");
        private const string ShellExtDllPath = @"C:\Program Files\Common Files\SOLIDWORKS Shared\sldwinshellextu.dll";

        // Tests the "SolidWorks file InfoTips Class" - the component that generates the
        // Explorer hover-tooltip text - to see if it (a) implements IPropertyStore, and (b) what
        // its tooltip text actually contains, as a candidate for where Description/LastSavedWith/
        // OpenTime's live values might really come from.
        private static void TestInfoTips(string filePath)
        {
            Console.WriteLine();
            Console.WriteLine("--- Testing the SolidWorks InfoTips COM class (sldwinshellextu.dll) ---");

            IntPtr dllHandle = NativeMethods.LoadLibrary(ShellExtDllPath);
            if (dllHandle == IntPtr.Zero)
            {
                Console.WriteLine($"LoadLibrary failed. Win32 error: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                IntPtr getClassObjectPtr = NativeMethods.GetProcAddress(dllHandle, "DllGetClassObject");
                if (getClassObjectPtr == IntPtr.Zero)
                {
                    Console.WriteLine("GetProcAddress(DllGetClassObject) failed.");
                    return;
                }

                var dllGetClassObject =
                    Marshal.GetDelegateForFunctionPointer<NativeMethods.DllGetClassObject>(getClassObjectPtr);

                Guid clsid = InfoTipsClsid;
                Guid classFactoryIid = typeof(NativeMethods.IClassFactory).GUID;
                int hr = dllGetClassObject(ref clsid, ref classFactoryIid, out object classFactoryObj);
                if (hr != 0)
                {
                    Console.WriteLine($"DllGetClassObject failed. HRESULT: 0x{hr:X8}");
                    return;
                }

                var classFactory = (NativeMethods.IClassFactory)classFactoryObj;
                Guid unknownIid = new Guid("00000000-0000-0000-C000-000000000046"); // IID_IUnknown
                classFactory.CreateInstance(null, ref unknownIid, out object infoTipsObj);
                Marshal.ReleaseComObject(classFactory);

                Console.WriteLine("Created InfoTips COM object.");
                Console.WriteLine($"  Implements IPersistFile:   {infoTipsObj is IPersistFile}");
                Console.WriteLine($"  Implements IQueryInfo:     {infoTipsObj is IQueryInfo}");
                Console.WriteLine($"  Implements IPropertyStore: {infoTipsObj is IPropertyStore}");

                if (infoTipsObj is IPersistFile pf)
                {
                    try
                    {
                        pf.Load(filePath, 0);
                        Console.WriteLine("  IPersistFile.Load - OK");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  IPersistFile.Load threw: {ex.Message}");
                    }
                }

                if (infoTipsObj is IQueryInfo qi)
                {
                    try
                    {
                        qi.GetInfoTip(0, out string tip);
                        Console.WriteLine($"  GetInfoTip() = \"{tip?.Replace("\r\n", " | ")}\"");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  GetInfoTip() threw: {ex.Message}");
                    }
                }

                Marshal.FinalReleaseComObject(infoTipsObj);
            }
            finally
            {
                NativeMethods.FreeLibrary(dllHandle);
            }
        }

        private static readonly Guid PropertyHandlerClsid = new Guid("6A921E8A-C58C-4941-9E71-7946D9DCE941");
        private const string PropertyHandlerDllPath = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\sldpropertyhandler.dll";

        // Tests "CSolidworkPropertyStore Class" (sldpropertyhandler.dll) - found by scanning every
        // SolidWorks DLL's raw bytes for the Description/LastSavedWith/OpenTime PROPERTYKEYs and
        // the propdesc filename. Not currently registered in .sldprt's ShellEx PropertyHandler
        // slot (confirmed absent earlier), but its name and contents strongly suggest this is the
        // real, standalone property handler implementation for these properties.
        private static void TestPropertyHandler(string filePath)
        {
            Console.WriteLine();
            Console.WriteLine("--- Testing CSolidworkPropertyStore Class (sldpropertyhandler.dll) ---");

            IntPtr dllHandle = NativeMethods.LoadLibrary(PropertyHandlerDllPath);
            if (dllHandle == IntPtr.Zero)
            {
                Console.WriteLine($"LoadLibrary failed. Win32 error: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                IntPtr getClassObjectPtr = NativeMethods.GetProcAddress(dllHandle, "DllGetClassObject");
                if (getClassObjectPtr == IntPtr.Zero)
                {
                    Console.WriteLine("GetProcAddress(DllGetClassObject) failed.");
                    return;
                }

                var dllGetClassObject =
                    Marshal.GetDelegateForFunctionPointer<NativeMethods.DllGetClassObject>(getClassObjectPtr);

                Guid clsid = PropertyHandlerClsid;
                Guid classFactoryIid = typeof(NativeMethods.IClassFactory).GUID;
                int hr = dllGetClassObject(ref clsid, ref classFactoryIid, out object classFactoryObj);
                if (hr != 0)
                {
                    Console.WriteLine($"DllGetClassObject failed. HRESULT: 0x{hr:X8}");
                    return;
                }

                var classFactory = (NativeMethods.IClassFactory)classFactoryObj;
                Guid unknownIid = new Guid("00000000-0000-0000-C000-000000000046"); // IID_IUnknown
                classFactory.CreateInstance(null, ref unknownIid, out object handlerObj);
                Marshal.ReleaseComObject(classFactory);

                Console.WriteLine("Created CSolidworkPropertyStore COM object.");
                Console.WriteLine($"  Implements IPersistFile:        {handlerObj is IPersistFile}");
                Console.WriteLine($"  Implements IInitializeWithFile: {handlerObj is IInitializeWithFile}");
                Console.WriteLine($"  Implements IPropertyStore:      {handlerObj is IPropertyStore}");

                bool initialized = false;

                if (handlerObj is IInitializeWithFile iwf)
                {
                    int initHr = iwf.Initialize(filePath, 0); // STGM_READ
                    Console.WriteLine($"  IInitializeWithFile.Initialize() = 0x{initHr:X8}");
                    initialized = initHr == 0;
                }

                if (!initialized && handlerObj is IPersistFile pf)
                {
                    try
                    {
                        pf.Load(filePath, 0);
                        Console.WriteLine("  IPersistFile.Load - OK");
                        initialized = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  IPersistFile.Load threw: {ex.Message}");
                    }
                }

                if (initialized && handlerObj is IPropertyStore ps)
                {
                    int countHr = ps.GetCount(out uint count);
                    Console.WriteLine($"  GetCount() = 0x{countHr:X8}, count={count}");

                    for (uint i = 0; i < count; i++)
                    {
                        int atHr = ps.GetAt(i, out var pkey);
                        if (atHr != 0) continue;

                        string name = ResolveName(pkey.fmtid, (int)pkey.pid);
                        int valHr = ps.GetValue(ref pkey, out var pv);
                        if (valHr == 0)
                        {
                            object value = pv.Value;
                            Console.WriteLine($"    [{i}] {name} ({pkey.fmtid}#{pkey.pid}) = {value}");
                            pv.Clear();
                        }
                        else
                        {
                            Console.WriteLine($"    [{i}] {name} ({pkey.fmtid}#{pkey.pid}) -- GetValue failed 0x{valHr:X8}");
                        }
                    }
                }

                if (handlerObj != null)
                    Marshal.FinalReleaseComObject(handlerObj);
            }
            finally
            {
                NativeMethods.FreeLibrary(dllHandle);
            }
        }

        private static void DumpChunks(NativeMethods.IFilter filter)
        {
            int statSize = Marshal.SizeOf<NativeMethods.STAT_CHUNK>();
            IntPtr statPtr = Marshal.AllocHGlobal(statSize);

            int chunkCount = 0;
            int valueChunkCount = 0;
            int textChunkCount = 0;

            try
            {
                while (true)
                {
                    var chunkResult = filter.GetChunk(statPtr);

                    if (chunkResult == NativeMethods.IFilterReturnCode.FILTER_E_END_OF_CHUNKS)
                        break;

                    if (chunkResult != NativeMethods.IFilterReturnCode.S_OK &&
                        chunkResult != NativeMethods.IFilterReturnCode.FILTER_S_LAST_TEXT &&
                        chunkResult != NativeMethods.IFilterReturnCode.FILTER_S_LAST_VALUES)
                    {
                        Console.WriteLine($"GetChunk() returned {chunkResult} - stopping.");
                        break;
                    }

                    chunkCount++;
                    var chunk = Marshal.PtrToStructure<NativeMethods.STAT_CHUNK>(statPtr);

                    string propName;
                    var spec = chunk.attribute.psProperty;
                    if (spec.ulKind == NativeMethods.PROPSPECKIND.PRSPEC_LPWSTR && spec.data != IntPtr.Zero)
                        propName = Marshal.PtrToStringUni(spec.data);
                    else
                        propName = ResolveName(chunk.attribute.guidPropSet, (int)spec.data.ToInt64());

                    bool isText = (chunk.flags & NativeMethods.CHUNKSTATE.CHUNK_TEXT) != 0;
                    bool isValue = (chunk.flags & NativeMethods.CHUNKSTATE.CHUNK_VALUE) != 0;

                    Console.WriteLine(
                        $"chunk#{chunk.idChunk} flags={chunk.flags} propset={chunk.attribute.guidPropSet} name={propName}");

                    if (isText)
                    {
                        textChunkCount++;
                        DumpText(filter);
                    }

                    if (isValue)
                    {
                        valueChunkCount++;
                        DumpValue(filter, propName);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(statPtr);
            }

            Console.WriteLine();
            Console.WriteLine(
                $"--- Done: {chunkCount} chunk(s) total, {textChunkCount} text, {valueChunkCount} value ---");
        }

        private static void DumpText(NativeMethods.IFilter filter)
        {
            uint bufSize = 4096;
            char[] buffer = new char[bufSize];
            var localSize = bufSize;
            var result = filter.GetText(ref localSize, buffer);

            if (result == NativeMethods.IFilterReturnCode.S_OK ||
                result == NativeMethods.IFilterReturnCode.FILTER_S_LAST_TEXT)
            {
                string text = new string(buffer, 0, (int)Math.Min(localSize, bufSize));
                string preview = text.Length > 120 ? text.Substring(0, 120) + "..." : text;
                Console.WriteLine($"    text: \"{preview.Replace("\n", "\\n").Replace("\r", "")}\"");
            }
        }

        private static void DumpValue(NativeMethods.IFilter filter, string propName)
        {
            while (true)
            {
                IntPtr pval = IntPtr.Zero;
                var result = filter.GetValue(ref pval);

                if (result != NativeMethods.IFilterReturnCode.S_OK &&
                    result != NativeMethods.IFilterReturnCode.FILTER_S_LAST_VALUES)
                    break;

                if (pval != IntPtr.Zero)
                {
                    try
                    {
                        var propVariant = Marshal.PtrToStructure<NativeMethods.PROPVARIANT>(pval);
                        object value = propVariant.Value;
                        Console.WriteLine($"    value ({propName}) [{propVariant.Type}] = {value}");
                        propVariant.Clear();
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pval);
                    }
                }

                if (result == NativeMethods.IFilterReturnCode.FILTER_S_LAST_VALUES)
                    break;
            }
        }
    }
}
