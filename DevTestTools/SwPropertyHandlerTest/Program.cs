using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwPropertyHandler;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
        {
            RunBatch(args[1]);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--concurrent", StringComparison.OrdinalIgnoreCase))
        {
            RunConcurrent(args[1]);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--rename-test", StringComparison.OrdinalIgnoreCase))
        {
            RunRenameTest(args[1]);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--batch-direct", StringComparison.OrdinalIgnoreCase))
        {
            RunBatchDirect(args[1]);
            return;
        }

        if (args.Length > 0 && args[0].Equals("--handle-loop", StringComparison.OrdinalIgnoreCase))
        {
            int iterations = args.Length > 2 ? int.Parse(args[2]) : 200;
            RunHandleLoop(args[1], iterations);
            return;
        }




        string filePath = args.Length > 0
            ? args[0]
            : @"C:\Users\Goren Harari\source\repos\SwFileExplorerCustomColumns\220-320612 WalkAir_WheelAxle.SLDPRT";

        // Direct (non-COM) instantiation first, to isolate whether any
        // discrepancy is in the compiled logic or in COM activation/caching.
        var direct = new SwPropertyStore();
        ((IInitializeWithFile)direct).Initialize(filePath, 0);
        ((IPropertyStore)direct).GetCount(out uint directCount);
        Console.WriteLine($"[Direct, non-COM] GetCount() = {directCount}");
        Console.WriteLine();

        var clsid = new Guid("E558E17D-51E7-4043-89D8-5EDB8498454F");

        Type comType = Type.GetTypeFromCLSID(clsid);
        object obj = Activator.CreateInstance(comType);

        Console.WriteLine($"Created COM object. Runtime type: {obj.GetType().FullName}");
        Console.WriteLine($"Implements IInitializeWithFile: {obj is IInitializeWithFile}");
        Console.WriteLine($"Implements IPropertyStore:      {obj is IPropertyStore}");

        if (obj is IInitializeWithFile iwf)
        {
            iwf.Initialize(filePath, 0);
            Console.WriteLine($"Initialize('{filePath}') called OK.");
        }

        if (obj is IPropertyStore ps)
        {
            ps.GetCount(out uint count);
            Console.WriteLine($"GetCount() = {count}");

            for (uint i = 0; i < count; i++)
            {
                ps.GetAt(i, out PROPERTYKEY pkey);
                Console.WriteLine($"  [{i}] fmtid={pkey.fmtid} pid={pkey.pid}");

                ps.GetValue(ref pkey, out object value);
                Console.WriteLine($"      value = '{value}' (type {value?.GetType().Name ?? "null"})");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
    }

    // Times a fresh COM activation + Initialize + every GetValue call per
    // file, matching how Explorer actually drives a property handler (a new
    // instance per item), across every real SolidWorks file in a folder -
    // see CLAUDE.md, Phase 3's deferred performance test.
    static void RunBatch(string folder)
    {
        var clsid = new Guid("E558E17D-51E7-4043-89D8-5EDB8498454F");
        Type comType = Type.GetTypeFromCLSID(clsid);

        string[] extensions = { ".sldprt", ".sldasm", ".slddrw" };
        var files = Directory.GetFiles(folder)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Found {files.Count} SolidWorks files in '{folder}'.");
        Console.WriteLine();

        long totalMs = 0;
        foreach (string file in files)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                object obj = Activator.CreateInstance(comType);
                var iwf = (IInitializeWithFile)obj;
                var ps = (IPropertyStore)obj;

                iwf.Initialize(file, 0);
                ps.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    ps.GetAt(i, out PROPERTYKEY pkey);
                    ps.GetValue(ref pkey, out object _);
                }

                sw.Stop();
                totalMs += sw.ElapsedMilliseconds;
                Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  ({count,2} props)  {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  FAILED: {ex.GetType().Name}: {ex.Message}  {Path.GetFileName(file)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {totalMs} ms across {files.Count} files - average {(files.Count > 0 ? totalMs / files.Count : 0)} ms/file.");
    }

    // Same as RunBatch, but via direct (non-COM) instantiation - always
    // exercises the just-built code, independent of whatever is currently
    // registered/deployed in Program Files. Used to validate the resolved
    // values are unchanged by the eager-resolve-in-Initialize fix without
    // needing to redeploy/re-register anything first.
    static void RunBatchDirect(string folder)
    {
        string[] extensions = { ".sldprt", ".sldasm", ".slddrw" };
        var files = Directory.GetFiles(folder)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Found {files.Count} SolidWorks files in '{folder}'.");
        Console.WriteLine();

        long totalMs = 0;
        int failures = 0;
        foreach (string file in files)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var store = new SwPropertyStore();
                var iwf = (IInitializeWithFile)store;
                var ps = (IPropertyStore)store;

                iwf.Initialize(file, 0);
                ps.GetCount(out uint count);
                int nonBlank = 0;
                for (uint i = 0; i < count; i++)
                {
                    ps.GetAt(i, out PROPERTYKEY pkey);
                    ps.GetValue(ref pkey, out object value);
                    if (value != null) nonBlank++;
                }

                sw.Stop();
                totalMs += sw.ElapsedMilliseconds;
                Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  ({count,2} props, {nonBlank,2} non-blank)  {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                failures++;
                sw.Stop();
                Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  FAILED: {ex.GetType().Name}: {ex.Message}  {Path.GetFileName(file)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {totalMs} ms across {files.Count} files - average {(files.Count > 0 ? totalMs / files.Count : 0)} ms/file. Failures: {failures}.");
    }

    // Regression test for the "explorer.exe holds the file open, blocking
    // rename/delete" bug: drives SwPropertyStore directly (non-COM, so this
    // always exercises the just-built code regardless of what's currently
    // registered/deployed) through the exact same Initialize/GetCount/
    // GetAt/GetValue sequence Explorer uses, then - in this same process,
    // with no explicit Dispose and no forced GC - immediately tries to
    // rename the file. Before the fix this fails (the SWDM handle is still
    // open, pinned by the finalizer never having run); after the fix it
    // must succeed immediately, since the handle is closed synchronously
    // inside Initialize itself.
    static void RunRenameTest(string filePath)
    {
        string renamedPath = Path.Combine(
            Path.GetDirectoryName(filePath),
            Path.GetFileNameWithoutExtension(filePath) + "_RENAME_TEST" + Path.GetExtension(filePath));

        var store = new SwPropertyStore();
        var iwf = (IInitializeWithFile)store;
        var ps = (IPropertyStore)store;

        iwf.Initialize(filePath, 0);
        ps.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            ps.GetAt(i, out PROPERTYKEY pkey);
            ps.GetValue(ref pkey, out object _);
        }

        Console.WriteLine($"Initialize + GetCount({count}) + all GetValue calls done. Attempting rename, with no Dispose/GC in between...");

        try
        {
            File.Move(filePath, renamedPath);
            Console.WriteLine("PASS: rename succeeded immediately - file is not locked.");
            File.Move(renamedPath, filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: rename failed - file still locked: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Repeatedly drives the just-built SwPropertyStore directly (non-COM,
    // same reasoning as RunBatchDirect - always exercises the just-built
    // code, and avoids needing our CLSID freshly re-registered/elevated)
    // against ONE file, and reports the current process's OS handle count
    // every 20 iterations, with no explicit GC forced in between. This is
    // the concrete signal for the SwDmDocument COM-release fix: before the
    // fix, classFactory/app/_doc RCWs are left for the GC/finalizer, so
    // handle count should climb across iterations and only drop after a GC;
    // after the fix, each iteration releases its own COM objects
    // deterministically in Dispose, so handle count should stay flat.
    static void RunHandleLoop(string filePath, int iterations)
    {
        var proc = Process.GetCurrentProcess();

        Console.WriteLine($"Repeating Initialize/GetValue against '{Path.GetFileName(filePath)}' {iterations} times, no forced GC.");
        Console.WriteLine($"  [start] handles = {proc.HandleCount}");

        for (int i = 1; i <= iterations; i++)
        {
            var store = new SwPropertyStore();
            var iwf = (IInitializeWithFile)store;
            var ps = (IPropertyStore)store;

            iwf.Initialize(filePath, 0);
            ps.GetCount(out uint count);
            for (uint p = 0; p < count; p++)
            {
                ps.GetAt(p, out PROPERTYKEY pkey);
                ps.GetValue(ref pkey, out object _);
            }

            if (i % 20 == 0 || i == iterations)
            {
                proc.Refresh();
                Console.WriteLine($"  [{i,4}] handles = {proc.HandleCount}");
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        proc.Refresh();
        Console.WriteLine($"  [after forced GC] handles = {proc.HandleCount}");

        Console.WriteLine("Done.");
    }

    // Multiple threads, each creating and driving its own COM object against
    // a different file at the same time - tests whether anything in the
    // handler (or the underlying SWDM class factory/app) breaks, deadlocks,
    // or corrupts results under concurrent access. See CLAUDE.md, Phase 3's
    // deferred concurrency test (item 6).
    static void RunConcurrent(string folder)
    {
        var clsid = new Guid("E558E17D-51E7-4043-89D8-5EDB8498454F");
        Type comType = Type.GetTypeFromCLSID(clsid);

        string[] extensions = { ".sldprt", ".sldasm", ".slddrw" };
        var files = Directory.GetFiles(folder)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .ToList();

        Console.WriteLine($"Running {files.Count} files concurrently (one thread each)...");

        var results = new ConcurrentBag<(string file, bool ok, string detail, long ms)>();

        var sw = Stopwatch.StartNew();
        Parallel.ForEach(files, file =>
        {
            var fileSw = Stopwatch.StartNew();
            try
            {
                object obj = Activator.CreateInstance(comType);
                var iwf = (IInitializeWithFile)obj;
                var ps = (IPropertyStore)obj;

                iwf.Initialize(file, 0);
                ps.GetCount(out uint count);
                int nonBlank = 0;
                for (uint i = 0; i < count; i++)
                {
                    ps.GetAt(i, out PROPERTYKEY pkey);
                    ps.GetValue(ref pkey, out object value);
                    if (value != null) nonBlank++;
                }

                fileSw.Stop();
                results.Add((Path.GetFileName(file), true, $"{count} props, {nonBlank} non-blank", fileSw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                fileSw.Stop();
                results.Add((Path.GetFileName(file), false, $"{ex.GetType().Name}: {ex.Message}", fileSw.ElapsedMilliseconds));
            }
        });
        sw.Stop();

        foreach (var r in results.OrderBy(r => r.file))
        {
            Console.WriteLine($"{(r.ok ? "OK  " : "FAIL")} {r.ms,5} ms  {r.file}  - {r.detail}");
        }

        int failures = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"Wall-clock total: {sw.ElapsedMilliseconds} ms for {files.Count} files run concurrently. Failures: {failures}.");
    }
}
