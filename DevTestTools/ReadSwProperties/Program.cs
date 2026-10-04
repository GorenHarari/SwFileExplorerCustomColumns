using System;
using SolidWorks.Interop.swdocumentmgr;

namespace ReadSwProperties
{
    class Program
    {
        // The Document Manager API license key comes from the same git-ignored
        // SwPropertyHandler\LicenseKey.cs the property handler uses (compiled
        // in via a linked file in ReadSwProperties.csproj) - see the README's
        // license key section. Without a valid key, GetApplication() below
        // returns null.
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ReadSwProperties.exe \"C:\\path\\to\\file.SLDPRT\"");
                return;
            }

            string filePath = args[0];

            // --- Step 1: connect to the Document Manager API ---
            var classFactory = (SwDMClassFactory)Activator.CreateInstance(
                Type.GetTypeFromProgID("SwDocumentMgr.SwDMClassFactory"));

            ISwDMApplication swDmApp = classFactory.GetApplication(SwPropertyHandler.LicenseKey.Value);
            if (swDmApp == null)
            {
                Console.WriteLine("Could not start the Document Manager application.");
                Console.WriteLine("Most likely cause: an invalid key in SwPropertyHandler\\LicenseKey.cs.");
                return;
            }

            // --- Step 2: open the file (read-only, so this is always safe) ---
            SwDmDocumentType docType = filePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? SwDmDocumentType.swDmDocumentAssembly
                : SwDmDocumentType.swDmDocumentPart;

            SwDmDocumentOpenError openError;
            ISwDMDocument swDoc = swDmApp.GetDocument(filePath, docType, true, out openError);

            if (swDoc == null)
            {
                Console.WriteLine($"Could not open '{filePath}'. Error: {openError}");
                return;
            }

            // --- Step 3: read every document-level custom property ---
            try
            {
                var names = swDoc.GetCustomPropertyNames() as object[];

                if (names == null || names.Length == 0)
                {
                    Console.WriteLine("No document-level custom properties found on this file.");
                }
                else
                {
                    Console.WriteLine($"Found {names.Length} custom propert{(names.Length == 1 ? "y" : "ies")}:");
                    Console.WriteLine();
                    foreach (var nameObj in names)
                    {
                        string name = (string)nameObj;
                        string value = swDoc.GetCustomProperty(name, out SwDmCustomInfoType type);
                        Console.WriteLine($"  {name}  =  {value}");
                    }
                }
            }
            finally
            {
                // Always close what we opened, even if something above throws.
                swDoc.CloseDoc();
            }

            Console.WriteLine();
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Done. Press any key to exit.");
                Console.ReadKey();
            }
        }
    }
}
