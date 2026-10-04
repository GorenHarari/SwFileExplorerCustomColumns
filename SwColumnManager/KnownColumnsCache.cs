using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SwColumnManager
{
    // Writes knownColumns.json - the name -> PROPERTYKEY cache SwPropertyStore
    // uses to auto-match a file's custom properties against already-existing
    // Explorer columns (see CLAUDE.md, Phase 4 / Issue 2). Paired with
    // ColumnLookup, which does the actual IShellFolder2 lookup; this class
    // just owns turning that result into the cache file on disk.
    internal static class KnownColumnsCache
    {
        public static readonly string Path = System.IO.Path.Combine(FieldsStore.ConfigDir, "knownColumns.json");

        // Best-effort - a stale or missing cache just means less auto-matching
        // until the next successful refresh, not a crash. Cheap enough (well
        // under a second for ~325 columns, confirmed earlier) to just always
        // redo on editor launch rather than add a manual refresh button.
        public static void Refresh()
        {
            try
            {
                var columns = ColumnLookup.GetAllColumns();

                var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in columns)
                {
                    // First occurrence wins if a display name has more than
                    // one registered PROPERTYKEY (seen for a few names during
                    // testing) - deterministic, simplest tie-break available.
                    if (!byName.ContainsKey(c.Name))
                    {
                        byName[c.Name] = new Dictionary<string, object>
                        {
                            ["fmtid"] = c.Key.fmtid.ToString(),
                            ["pid"] = c.Key.pid
                        };
                    }
                }

                Directory.CreateDirectory(FieldsStore.ConfigDir);
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(Path, serializer.Serialize(byName));
            }
            catch
            {
                // See method comment - best-effort only.
            }
        }
    }
}
