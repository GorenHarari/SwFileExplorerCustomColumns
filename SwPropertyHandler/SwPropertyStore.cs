using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.swdocumentmgr;

namespace SwPropertyHandler
{
    // Real implementation (see CLAUDE.md, Phase 3). Initialize opens the SWDM
    // document once and reads fields.json once, both cached for the lifetime
    // of this instance - GetCount/GetAt/GetValue then just reuse that instead
    // of re-opening the file or re-reading the config on every call.
    //
    // Every path that native code (Explorer, the Search Indexer) can reach
    // must never throw - a thrown exception here becomes a failure HRESULT at
    // best, or in the worst case destabilizes the host process, since this
    // runs inside explorer.exe. Every failure degrades to "no value" instead.
    [ComVisible(true)]
    [Guid("E558E17D-51E7-4043-89D8-5EDB8498454F")]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("SwFileExplorerCustomColumns.PropertyHandler")]
    public class SwPropertyStore : IInitializeWithFile, IPropertyStore
    {
        private static readonly Guid SchemaFormatId = new Guid("42161C84-EBEC-4753-9E00-9D700D9B4361");

        private static readonly string FieldsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns", "fields.json");

        // name -> pid, ordered by pid for stable GetAt enumeration.
        private List<KeyValuePair<string, int>> _fields = new List<KeyValuePair<string, int>>();

        private ISwDMDocument25 _doc;

        public void Initialize(string pszFilePath, uint grfMode)
        {
            _fields = LoadFields();
            _doc = TryOpenDocument(pszFilePath);
        }

        public void GetCount(out uint cProps)
        {
            cProps = (uint)(_fields.Count + LegacyProperties.All.Length);
        }

        public void GetAt(uint iProp, out PROPERTYKEY pkey)
        {
            if (iProp < _fields.Count)
            {
                pkey = new PROPERTYKEY(SchemaFormatId, (uint)_fields[(int)iProp].Value);
                return;
            }

            int legacyIndex = (int)iProp - _fields.Count;
            if (legacyIndex < 0 || legacyIndex >= LegacyProperties.All.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(iProp));
            }

            pkey = LegacyProperties.All[legacyIndex];
        }

        public void GetValue(ref PROPERTYKEY key, out object pv)
        {
            pv = null;

            if (_doc == null)
            {
                return;
            }

            try
            {
                if (LegacyProperties.KeyEquals(key, LegacyProperties.Description))
                {
                    pv = _doc.GetCustomPropertyValues("Description", out SwDmCustomInfoType _, out string _unused1);
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.OpenTime))
                {
                    _doc.GetFileAvgTime(out string fileTime, out string _unused2);
                    pv = fileTime;
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.LastSavedWith))
                {
                    pv = LegacyProperties.FormatVersion(_doc.GetVersion());
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.Title))
                {
                    pv = _doc.Title;
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.Subject))
                {
                    pv = _doc.Subject;
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.Author))
                {
                    pv = _doc.Author;
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.Keywords))
                {
                    pv = _doc.Keywords;
                }
                else if (LegacyProperties.KeyEquals(key, LegacyProperties.Comment))
                {
                    pv = _doc.Comments;
                }
                else if (key.fmtid == SchemaFormatId)
                {
                    uint pid = key.pid;
                    string name = _fields.FirstOrDefault(f => f.Value == pid).Key;
                    if (name != null)
                    {
                        pv = _doc.GetCustomPropertyValues(name, out SwDmCustomInfoType _, out string _unused3);
                    }
                }
            }
            catch
            {
                // Property missing on this file, or any SWDM failure - blank, not a crash.
                pv = null;
            }
        }

        public void SetValue(ref PROPERTYKEY key, ref object pv)
        {
            // Read-only handler - silently ignore writes.
        }

        public void Commit()
        {
        }

        private static List<KeyValuePair<string, int>> LoadFields()
        {
            try
            {
                if (!File.Exists(FieldsPath))
                {
                    return new List<KeyValuePair<string, int>>();
                }

                string json = File.ReadAllText(FieldsPath);
                var serializer = new JavaScriptSerializer();
                var dict = serializer.Deserialize<Dictionary<string, int>>(json);

                // Defensive: reserved names (Description/OpenTime/LastSavedWith) are
                // always served via LegacyProperties instead - skip them here even if
                // fields.json was hand-edited to include one, so a value is never
                // reported twice under two different PROPERTYKEYs.
                return dict
                    .Where(kvp => !LegacyProperties.ReservedNames.Any(r =>
                        string.Equals(r, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(kvp => kvp.Value)
                    .ToList();
            }
            catch
            {
                return new List<KeyValuePair<string, int>>();
            }
        }

        private static ISwDMDocument25 TryOpenDocument(string filePath)
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
                return doc as ISwDMDocument25;
            }
            catch
            {
                return null;
            }
        }

        ~SwPropertyStore()
        {
            try
            {
                _doc?.CloseDoc();
            }
            catch
            {
                // Best-effort cleanup only - never throw from a finalizer.
            }
        }
    }
}
