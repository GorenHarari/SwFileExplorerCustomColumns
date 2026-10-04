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
