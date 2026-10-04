using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SwColumnManager
{
    // Single source of truth for fields.json - the tracked custom-property
    // name -> PID list shared by the field list editor (MainForm) and the
    // elevated Apply/Uninstall actions (InstallActions). Previously
    // reimplemented separately in both places, and they'd quietly drifted:
    // MainForm's dictionary used a case-insensitive comparer, InstallActions's
    // didn't - a property name differing only by case could dedupe
    // differently depending on which code path read the file. Both now go
    // through this one implementation.
    internal static class FieldsStore
    {
        public static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        public static readonly string FieldsPath = Path.Combine(ConfigDir, "fields.json");

        public static Dictionary<string, int> Load()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FieldsPath))
            {
                return result;
            }

            string json = File.ReadAllText(FieldsPath);
            var serializer = new JavaScriptSerializer();
            foreach (var kvp in serializer.Deserialize<Dictionary<string, int>>(json))
            {
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        public static void Save(Dictionary<string, int> fields)
        {
            Directory.CreateDirectory(ConfigDir);
            var serializer = new JavaScriptSerializer();
            File.WriteAllText(FieldsPath, serializer.Serialize(fields));
        }
    }
}
