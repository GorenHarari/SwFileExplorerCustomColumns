using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.swdocumentmgr;

namespace SwPropertyHandler
{
    // The one class in this project that references
    // SolidWorks.Interop.swdocumentmgr and makes SWDM COM calls - opening a
    // document, reading its Summary-tab fields and custom properties.
    // Everything else (SwPropertyStore, AutoMatcher) works against this
    // plain C#-typed surface instead of touching SWDM types directly.
    //
    // All three SWDM COM objects this class touches (classFactory, app,
    // _doc) are held as fields and explicitly released in Dispose, rather
    // than left for the CLR's GC/finalizer to reclaim whenever it next runs
    // inside the long-lived explorer.exe/SearchFilterHost.exe host process.
    internal sealed class SwDmDocument : IDisposable
    {
        private readonly SwDMClassFactory _classFactory;
        private readonly ISwDMApplication _app;
        private readonly ISwDMDocument23 _doc;

        private SwDmDocument(SwDMClassFactory classFactory, ISwDMApplication app, ISwDMDocument23 doc)
        {
            _classFactory = classFactory;
            _app = app;
            _doc = doc;
        }

        public static SwDmDocument TryOpen(string filePath)
        {
            SwDMClassFactory classFactory = null;
            ISwDMApplication app = null;
            ISwDMDocument doc = null;
            try
            {
                SwDmDocumentType docType;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                switch (ext)
                {
                    case ".sldprt":
                        docType = SwDmDocumentType.swDmDocumentPart;
                        break;
                    case ".sldasm":
                        docType = SwDmDocumentType.swDmDocumentAssembly;
                        break;
                    case ".slddrw":
                        docType = SwDmDocumentType.swDmDocumentDrawing;
                        break;
                    default:
                        return null;
                }

                classFactory = (SwDMClassFactory)Activator.CreateInstance(
                    Type.GetTypeFromProgID("SwDocumentMgr.SwDMClassFactory"));

                app = classFactory.GetApplication(LicenseKey.Value);
                if (app == null)
                {
                    ReleaseQuiet(classFactory);
                    return null;
                }

                doc = app.GetDocument(filePath, docType, true, out SwDmDocumentOpenError _);
                if (doc is ISwDMDocument23 doc23)
                {
                    return new SwDmDocument(classFactory, app, doc23);
                }

                // Opened natively but isn't the interface we need - still
                // close/release it instead of leaving the file locked.
                CloseQuiet(doc);
                ReleaseQuiet(doc);
                ReleaseQuiet(app);
                ReleaseQuiet(classFactory);
                return null;
            }
            catch
            {
                CloseQuiet(doc);
                ReleaseQuiet(doc);
                ReleaseQuiet(app);
                ReleaseQuiet(classFactory);
                return null;
            }
        }

        private static void CloseQuiet(ISwDMDocument doc)
        {
            try
            {
                doc?.CloseDoc();
            }
            catch
            {
            }
        }

        private static void ReleaseQuiet(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch
            {
            }
        }

        public string Title => _doc.Title;
        public string Subject => _doc.Subject;
        public string Author => _doc.Author;
        public string Keywords => _doc.Keywords;
        public string Comments => _doc.Comments;

        // Resolved value (e.g. the real "10B21" for a linked Material
        // property), not the raw "SW-Material@..." formula-link string -
        // see CLAUDE.md's SWDM coverage check. Type/linkedTo out params are
        // never used by any caller, so they're discarded here instead of
        // leaking SwDmCustomInfoType outside this class.
        public string GetCustomProperty(string name)
        {
            return _doc.GetCustomPropertyValues(name, out SwDmCustomInfoType _, out string _);
        }

        public string[] GetCustomPropertyNames()
        {
            return (_doc.GetCustomPropertyNames() as object[])?.OfType<string>().ToArray()
                ?? Array.Empty<string>();
        }

        public string GetFileAvgTime()
        {
            _doc.GetFileAvgTime(out string fileTime, out string _);
            return fileTime;
        }

        // Raw file-format version code - see LegacyProperties.FormatVersion
        // for the code -> "SOLIDWORKS <year>" lookup, kept separate since
        // that's pure formatting, not a COM call.
        public int GetVersionCode() => _doc.GetVersion();

        public void Dispose()
        {
            // Best-effort only, and each release attempted independently -
            // a disposal failure here must never propagate into
            // explorer.exe, and one failed release must not block the
            // others.
            CloseQuiet(_doc);
            ReleaseQuiet(_doc);
            ReleaseQuiet(_app);
            ReleaseQuiet(_classFactory);
        }
    }
}
