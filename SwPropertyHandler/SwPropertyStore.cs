using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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

        // How to resolve each of LegacyProperties.All to a live value, keyed
        // by PROPERTYKEY instead of a chain of LegacyProperties.KeyEquals
        // checks - same resolution, just a lookup instead of up to 8
        // sequential comparisons.
        private sealed class PropertyKeyComparer : IEqualityComparer<PROPERTYKEY>
        {
            public bool Equals(PROPERTYKEY a, PROPERTYKEY b) => LegacyProperties.KeyEquals(a, b);
            public int GetHashCode(PROPERTYKEY k) => k.fmtid.GetHashCode() ^ (int)k.pid;
        }

        private static readonly Dictionary<PROPERTYKEY, Func<SwDmDocument, object>> LegacyValueResolvers =
            new Dictionary<PROPERTYKEY, Func<SwDmDocument, object>>(new PropertyKeyComparer())
            {
                [LegacyProperties.Description] = doc => doc.GetCustomProperty("Description"),
                [LegacyProperties.OpenTime] = doc => doc.GetFileAvgTime(),
                [LegacyProperties.LastSavedWith] = doc => LegacyProperties.FormatVersion(doc.GetVersionCode()),
                [LegacyProperties.Title] = doc => doc.Title,
                [LegacyProperties.Subject] = doc => doc.Subject,
                [LegacyProperties.Author] = doc => doc.Author,
                [LegacyProperties.Keywords] = doc => doc.Keywords,
                [LegacyProperties.Comment] = doc => doc.Comments,
            };

        // name -> pid, ordered by pid for stable GetAt enumeration.
        private List<KeyValuePair<string, int>> _fields = new List<KeyValuePair<string, int>>();

        // Computed per-instance from this file's actual custom properties.
        private List<KeyValuePair<string, PROPERTYKEY>> _autoMatched = new List<KeyValuePair<string, PROPERTYKEY>>();

        private SwDmDocument _doc;

        public void Initialize(string pszFilePath, uint grfMode)
        {
            _fields = HandlerConfig.LoadFields();
            _doc = SwDmDocument.TryOpen(pszFilePath);
            _autoMatched = AutoMatcher.ComputeAutoMatches(_doc, _fields);
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
                if (LegacyValueResolvers.TryGetValue(key, out var resolve))
                {
                    pv = resolve(_doc);
                }
                else if (key.fmtid == SchemaFormatId)
                {
                    uint pid = key.pid;
                    string name = _fields.FirstOrDefault(f => f.Value == pid).Key;
                    if (name != null)
                    {
                        pv = _doc.GetCustomProperty(name);
                    }
                }
                else
                {
                    PROPERTYKEY keyCopy = key;
                    string autoName = _autoMatched.FirstOrDefault(a => LegacyProperties.KeyEquals(a.Value, keyCopy)).Key;
                    if (autoName != null)
                    {
                        pv = _doc.GetCustomProperty(autoName);
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

        ~SwPropertyStore()
        {
            try
            {
                _doc?.Dispose();
            }
            catch
            {
                // Best-effort cleanup only - never throw from a finalizer.
            }
        }
    }
}
