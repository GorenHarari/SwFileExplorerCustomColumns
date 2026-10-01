using System;
using SwPropertyHandler;

class Program
{
    static void Main(string[] args)
    {
        string filePath = args.Length > 0
            ? args[0]
            : @"C:\Users\Goren Harari\source\repos\SwFileExplorerCustomColumns\220-320612 WalkAir_WheelAxle.SLDPRT";

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
}
