using System;
using System.IO;
using System.Linq;
using SolidWorks.Interop.swdocumentmgr;

namespace SwPropertyHandler
{
    // The one class in this project that references
    // SolidWorks.Interop.swdocumentmgr and makes SWDM COM calls - opening a
    // document, reading its Summary-tab fields and custom properties.
    // Everything else (SwPropertyStore, AutoMatcher) works against this
    // plain C#-typed surface instead of touching SWDM types directly.
    internal sealed class SwDmDocument : IDisposable
    {
        private readonly ISwDMDocument23 _doc;

        private SwDmDocument(ISwDMDocument23 doc)
        {
            _doc = doc;
        }

        public static SwDmDocument TryOpen(string filePath)
        {
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

                var classFactory = (SwDMClassFactory)Activator.CreateInstance(
                    Type.GetTypeFromProgID("SwDocumentMgr.SwDMClassFactory"));

                ISwDMApplication app = classFactory.GetApplication(LicenseKey.Value);
                if (app == null)
                {
                    return null;
                }

                ISwDMDocument doc = app.GetDocument(filePath, docType, true, out SwDmDocumentOpenError _);
                return doc is ISwDMDocument23 doc23 ? new SwDmDocument(doc23) : null;
            }
            catch
            {
                return null;
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
            try
            {
                _doc?.CloseDoc();
            }
            catch
            {
                // Best-effort cleanup only - a disposal failure here must
                // never propagate into explorer.exe.
            }
        }
    }
}
