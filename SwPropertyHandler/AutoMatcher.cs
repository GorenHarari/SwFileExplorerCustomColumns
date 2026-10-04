using System;
using System.Collections.Generic;
using System.Linq;

namespace SwPropertyHandler
{
    // For every real custom property on a given file, checks whether its
    // name matches an already-existing Explorer column - if so, it should
    // be served under that column's real identity instead of needing a
    // fields.json entry at all. Skips anything LegacyProperties already
    // owns, and anything fields.json already claims under our own schema,
    // so nothing is ever reported under two PROPERTYKEYs.
    internal static class AutoMatcher
    {
        public static List<KeyValuePair<string, PROPERTYKEY>> ComputeAutoMatches(
            SwDmDocument doc, List<KeyValuePair<string, int>> fields)
        {
            var result = new List<KeyValuePair<string, PROPERTYKEY>>();
            if (doc == null)
            {
                return result;
            }

            try
            {
                var knownColumns = HandlerConfig.LoadKnownColumns();
                if (knownColumns.Count == 0)
                {
                    return result;
                }

                foreach (var name in doc.GetCustomPropertyNames())
                {
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
    }
}
