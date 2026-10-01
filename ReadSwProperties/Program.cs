using System;
using SolidWorks.Interop.swdocumentmgr;

namespace ReadSwProperties
{
    class Program
    {
        // The Document Manager API license key is NOT stored in source - it's a
        // secret and this file is in git. It's read at runtime from the
        // SWDM_LICENSE_KEY environment variable; the key itself (and how to set
        // it) lives in SwDmLicenseKey.md, which .gitignore excludes.
        // The key is separate from a normal SolidWorks license - it's requested
        // from the SOLIDWORKS Customer Portal against your serial number
        // (search the portal for "Document Manager API license key"; ask your
        // reseller/VAR if you can't find the request form). Without a valid
        // key, GetApplication() below returns null.
        private const string LicenseKeyVariable = "SWDM_LICENSE_KEY";

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ReadSwProperties.exe \"C:\\path\\to\\file.SLDPRT\"");
                return;
            }

            string filePath = args[0];

            string licenseKey = Environment.GetEnvironmentVariable(LicenseKeyVariable);
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                Console.WriteLine("No Document Manager license key found.");
                Console.WriteLine("Set the " + LicenseKeyVariable + " environment variable first");
                Console.WriteLine("(see SwDmLicenseKey.md for the key and the exact command).");
                return;
            }

            // --- Step 1: connect to the Document Manager API ---
            var classFactory = (SwDMClassFactory)Activator.CreateInstance(
                Type.GetTypeFromProgID("SwDocumentMgr.SwDMClassFactory"));

            ISwDMApplication swDmApp = classFactory.GetApplication(licenseKey);
            if (swDmApp == null)
            {
                Console.WriteLine("Could not start the Document Manager application.");
                Console.WriteLine("Most likely cause: an invalid " + LicenseKeyVariable + " value.");
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
