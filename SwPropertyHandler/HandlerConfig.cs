using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SwPropertyHandler
{
    // Reads the two config files SwPropertyStore needs per file activation:
    // fields.json (explicitly tracked custom properties) and
    // knownColumns.json (the name -> PROPERTYKEY cache SwColumnManager
    // maintains for auto-matching against existing Explorer columns). Pure
    // config I/O, no SWDM/COM involved - kept separate from SwPropertyStore
    // so the COM-facing class stays focused on the IPropertyStore contract.
    //
    // Both files are cached in memory, keyed by last-write-time, since
    // Explorer activates a new SwPropertyStore (and therefore calls these
    // loaders) once per file in a folder view - without caching, every file
    // shown re-reads and re-parses both files from disk. Re-read only
    // happens when the file's last-write-time (or existence) actually
    // changes, so editing fields.json via SwColumnManager and re-Applying
    // still takes effect on the next browse.
    internal static class HandlerConfig
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        private static readonly string FieldsPath = Path.Combine(ConfigDir, "fields.json");
        private static readonly string KnownColumnsPath = Path.Combine(ConfigDir, "knownColumns.json");

        private sealed class CacheEntry<T>
        {
            public bool Existed;
            public DateTime LastWriteTimeUtc;
            public T Value;
        }

        private static volatile CacheEntry<List<KeyValuePair<string, int>>> _fieldsCache;
        private static volatile CacheEntry<Dictionary<string, PROPERTYKEY>> _knownColumnsCache;

        // name -> pid, ordered by pid for stable GetAt enumeration.
        public static List<KeyValuePair<string, int>> LoadFields()
        {
            bool existsNow = File.Exists(FieldsPath);
            DateTime mtimeNow = existsNow ? File.GetLastWriteTimeUtc(FieldsPath) : DateTime.MinValue;

            var cached = _fieldsCache;
            if (cached != null && cached.Existed == existsNow && cached.LastWriteTimeUtc == mtimeNow)
            {
                return cached.Value;
            }

            List<KeyValuePair<string, int>> value;
            try
            {
                if (!existsNow)
                {
                    value = new List<KeyValuePair<string, int>>();
                }
                else
                {
                    string json = File.ReadAllText(FieldsPath);
                    var serializer = new JavaScriptSerializer();
                    var dict = serializer.Deserialize<Dictionary<string, int>>(json);

                    // Defensive: reserved names are always served via
                    // LegacyProperties instead - skip them here even if
                    // fields.json was hand-edited to include one, so a value is
                    // never reported twice under two different PROPERTYKEYs.
                    value = dict
                        .Where(kvp => !LegacyProperties.ReservedNames.Any(r =>
                            string.Equals(r, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(kvp => kvp.Value)
                        .ToList();
                }
            }
            catch
            {
                value = new List<KeyValuePair<string, int>>();
            }

            _fieldsCache = new CacheEntry<List<KeyValuePair<string, int>>>
            {
                Existed = existsNow,
                LastWriteTimeUtc = mtimeNow,
                Value = value,
            };
            return value;
        }

        public static Dictionary<string, PROPERTYKEY> LoadKnownColumns()
        {
            bool existsNow = File.Exists(KnownColumnsPath);
            DateTime mtimeNow = existsNow ? File.GetLastWriteTimeUtc(KnownColumnsPath) : DateTime.MinValue;

            var cached = _knownColumnsCache;
            if (cached != null && cached.Existed == existsNow && cached.LastWriteTimeUtc == mtimeNow)
            {
                return cached.Value;
            }

            var result = new Dictionary<string, PROPERTYKEY>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (existsNow)
                {
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
            }
            catch
            {
                // Missing/corrupt cache - just means no auto-matching this time, not a crash.
            }

            _knownColumnsCache = new CacheEntry<Dictionary<string, PROPERTYKEY>>
            {
                Existed = existsNow,
                LastWriteTimeUtc = mtimeNow,
                Value = result,
            };
            return result;
        }
    }
}
