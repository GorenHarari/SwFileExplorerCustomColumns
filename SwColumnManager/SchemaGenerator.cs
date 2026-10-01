using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace SwColumnManager
{
    // Builds the .propdesc XML for our tracked fields. One shared FMTID for
    // the whole schema (see CLAUDE.md, Phase 2 / item 1) - every property
    // just gets the next propID, same pattern SolidWorks's own
    // solidworksproperties.propdesc uses. Everything is type="String" (item 3
    // - checked against the real SolidWorks schema, which does the same even
    // for properties that look numeric/time-based).
    internal static class SchemaGenerator
    {
        // Locked, permanent - generated once, never changes. See CLAUDE.md.
        public const string SchemaFormatId = "{42161C84-EBEC-4753-9E00-9D700D9B4361}";

        private const string CanonicalPrefix = "SwSync";

        public static string BuildCanonicalName(string fieldName)
        {
            string sanitized = Regex.Replace(fieldName, "[^A-Za-z0-9]", "");
            if (sanitized.Length == 0)
            {
                sanitized = "Field";
            }
            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "F" + sanitized;
            }
            return $"{CanonicalPrefix}.{sanitized}";
        }

        public static string BuildXml(Dictionary<string, int> fields)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<schema xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
            sb.AppendLine("        xmlns=\"http://schemas.microsoft.com/windows/2006/propertydescription\"");
            sb.AppendLine("        schemaVersion=\"1.0\" >");
            sb.AppendLine();
            sb.AppendLine("  <propertyDescriptionList publisher=\"SwFileExplorerCustomColumns\" product=\"CustomProperties\">");
            sb.AppendLine();

            foreach (var kvp in fields.OrderBy(kvp => kvp.Value))
            {
                string canonicalName = BuildCanonicalName(kvp.Key);
                string label = SecurityElement.Escape(kvp.Key);

                sb.AppendLine($"    <propertyDescription name=\"{canonicalName}\" formatID=\"{SchemaFormatId}\" propID=\"{kvp.Value}\">");
                sb.AppendLine($"      <description>{label}</description>");
                sb.AppendLine("      <searchInfo inInvertedIndex=\"true\" isColumn=\"true\" columnIndexType=\"OnDisk\"/>");
                sb.AppendLine("      <typeInfo type=\"String\" multipleValues=\"false\" isViewable=\"true\" isQueryable=\"true\"/>");
                sb.AppendLine($"      <labelInfo label=\"{label}\" />");
                sb.AppendLine("    </propertyDescription>");
                sb.AppendLine();
            }

            sb.AppendLine("  </propertyDescriptionList>");
            sb.AppendLine("</schema>");

            return sb.ToString();
        }
    }
}
