using System.Collections.Generic;
using Microsoft.Win32;

namespace SwColumnManager
{
    // Reads/writes HKLM\...\PropertySystem\PropertyHandlers\<ext> for the
    // three SolidWorks document extensions. See CLAUDE.md, Phase 2 / item 8:
    // the original CLSID is a vendor-chosen constant baked into
    // sldpropertyhandler.dll at build time, confirmed identical across the
    // SW2019 and SW2020 installs on this machine - safe to hardcode as the
    // permanent revert target, no dynamic backup needed.
    internal static class PropertyHandlerRegistry
    {
        public const string OriginalSolidWorksClsid = "{6A921E8A-C58C-4941-9E71-7946D9DCE941}";

        public static readonly string[] Extensions = { ".sldprt", ".sldasm", ".slddrw" };

        private const string KeyPathTemplate =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\{0}";

        public static string GetCurrentClsid(string extension)
        {
            string keyPath = string.Format(KeyPathTemplate, extension);
            using (var key = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                return key?.GetValue(null) as string;
            }
        }

        public static void SetClsid(string extension, string clsid)
        {
            string keyPath = string.Format(KeyPathTemplate, extension);
            using (var key = Registry.LocalMachine.CreateSubKey(keyPath))
            {
                key.SetValue(null, clsid);
            }
        }

        public static Dictionary<string, string> GetCurrentClsids()
        {
            var result = new Dictionary<string, string>();
            foreach (string ext in Extensions)
            {
                result[ext] = GetCurrentClsid(ext);
            }
            return result;
        }
    }
}
