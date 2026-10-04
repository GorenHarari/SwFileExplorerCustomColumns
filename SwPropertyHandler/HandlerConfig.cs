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
    internal static class HandlerConfig
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        private static readonly string FieldsPath = Path.Combine(ConfigDir, "fields.json");
        private static readonly string KnownColumnsPath = Path.Combine(ConfigDir, "knownColumns.json");

        // name -> pid, ordered by pid for stable GetAt enumeration.
        public static List<KeyValuePair<string, int>> LoadFields()
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

        public static Dictionary<string, PROPERTYKEY> LoadKnownColumns()
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
    }
}
