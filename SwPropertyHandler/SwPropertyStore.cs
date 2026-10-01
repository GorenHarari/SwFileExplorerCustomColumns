using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.swdocumentmgr;

namespace SwPropertyHandler
{
    // Real implementation (see CLAUDE.md, Phase 3/4). Initialize opens the
    // SWDM document once and reads fields.json once, both cached for the
    // lifetime of this instance - GetCount/GetAt/GetValue then just reuse
    // that instead of re-opening the file or re-reading the config on every
    // call.
    //
    // Three sources of properties are served, in this priority order:
    //   1. LegacyProperties - 8 fixed PROPERTYKEYs, always present,
    //      continuing exactly what SolidWorks's own handler used to serve.
    //   2. Auto-matched properties - any of THIS file's actual custom
    //      properties whose name happens to match an already-existing
    //      Explorer column (from knownColumns.json, built by
    //      FieldListEditor). Reported under that column's real identity, so
    //      no new column is minted for something that already has one. This
    //      is the general case Description already was a special-cased
    //      instance of - see CLAUDE.md, Phase 4 / Issue 2.
    //   3. fields.json - explicit tracked fields for properties with no
    //      existing match, each getting a permanently-assigned PID under
    //      our own schema (SchemaApplyTool/SchemaGenerator.cs).
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

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        private static readonly string FieldsPath = Path.Combine(ConfigDir, "fields.json");
        private static readonly string KnownColumnsPath = Path.Combine(ConfigDir, "knownColumns.json");

        // name -> pid, ordered by pid for stable GetAt enumeration.
        private List<KeyValuePair<string, int>> _fields = new List<KeyValuePair<string, int>>();

        // Computed per-instance from this file's actual custom properties.
        private List<KeyValuePair<string, PROPERTYKEY>> _autoMatched = new List<KeyValuePair<string, PROPERTYKEY>>();

        private ISwDMDocument25 _doc;

        public void Initialize(string pszFilePath, uint grfMode)
        {
            _fields = LoadFields();
            _doc = TryOpenDocument(pszFilePath);
            _autoMatched = ComputeAutoMatches(_doc, _fields);
        }

        public void GetCount(out uint cProps)
        {
            cProps = (uint)(_fields.Count + _autoMatched.Count + LegacyProperties.All.Length);
        }

        public void GetAt(uint iProp, out PROPERTYKEY pkey)
        {
            if (iProp < _fields.Count)
            {
                pkey = new PROPERTYKEY(SchemaFormatId, (uint)_fields[(int)iProp].Value);
                return;
            }

            int autoIndex = (int)iProp - _fields.Count;
            if (autoIndex < _autoMatched.Count)
            {
                pkey = _autoMatched[autoIndex].Value;
                return;
            }

            int legacyIndex = autoIndex - _autoMatched.Count;
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
                else
                {
                    PROPERTYKEY keyCopy = key;
                    string autoName = _autoMatched.FirstOrDefault(a => LegacyProperties.KeyEquals(a.Value, keyCopy)).Key;
                    if (autoName != null)
                    {
                        pv = _doc.GetCustomPropertyValues(autoName, out SwDmCustomInfoType _, out string _unused4);
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

                // Defensive: reserved names are always served via
                // LegacyProperties instead - skip them here even if
                // fields.json was hand-edited to include one, so a value is
                // never reported twice under two different PROPERTYKEYs.
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

        private static Dictionary<string, PROPERTYKEY> LoadKnownColumns()
        {
            var result = new Dictionary<string, PROPERTYKEY>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(KnownColumnsPath))
                {
                    return result;
                }

                string json = File.ReadAllText(KnownColumnsPath);
                var serializer = new JavaScriptSerializer();
                var raw = serializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);
                foreach (var kvp in raw)
                {
                    var fmtid = new Guid((string)kvp.Value["fmtid"]);
                    uint pid = Convert.ToUInt32(kvp.Value["pid"]);
                    result[kvp.Key] = new PROPERTYKEY(fmtid, pid);
                }
            }
            catch
            {
                // Missing/corrupt cache - just means no auto-matching this time, not a crash.
            }
            return result;
        }

        // For every real custom property on THIS file, check whether its
        // name matches an already-existing Explorer column - if so, serve
        // it under that column's real identity instead of needing a
        // fields.json entry at all. Skips anything LegacyProperties already
        // owns, and anything fields.json already claims under our own
        // schema, so nothing is ever reported under two PROPERTYKEYs.
        private static List<KeyValuePair<string, PROPERTYKEY>> ComputeAutoMatches(
            ISwDMDocument25 doc, List<KeyValuePair<string, int>> fields)
        {
            var result = new List<KeyValuePair<string, PROPERTYKEY>>();
            if (doc == null)
            {
                return result;
            }

            try
            {
                var knownColumns = LoadKnownColumns();
                if (knownColumns.Count == 0)
                {
                    return result;
                }

                var names = doc.GetCustomPropertyNames() as object[];
                if (names == null)
                {
                    return result;
                }

                foreach (var n in names)
                {
                    string name = n as string;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (LegacyProperties.ReservedNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (fields.Any(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (knownColumns.TryGetValue(name, out PROPERTYKEY key))
                    {
                        result.Add(new KeyValuePair<string, PROPERTYKEY>(name, key));
                    }
                }
            }
            catch
            {
                // Any SWDM failure here just means no auto-matching this time, not a crash.
            }

            return result;
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
