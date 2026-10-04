using System;
using System.Collections.Generic;

namespace SwPropertyHandler
{
    // SolidWorks's own three legacy PROPERTYKEYs, confirmed directly from
    // earlier research (see CLAUDE.md "SOLVED" section and the live
    // TestPropertyHandler dump) - not guessed. These stay "always on": they
    // are not part of fields.json, not user-addable/removable, and are
    // served directly under SolidWorks's own FMTIDs/PIDs rather than ours,
    // so after repointing PropertyHandlers to our handler, these three
    // existing columns keep working under their existing identity instead
    // of going blank or being duplicated under our schema.
    public static class LegacyProperties
    {
        public static readonly PROPERTYKEY Description =
            new PROPERTYKEY(new Guid("6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2"), 100);

        public static readonly PROPERTYKEY OpenTime =
            new PROPERTYKEY(new Guid("3930FA19-37C3-4B0F-8AFA-55B5E45F6A45"), 113);

        public static readonly PROPERTYKEY LastSavedWith =
            new PROPERTYKEY(new Guid("29ED838A-063D-4502-89C4-BD1A79DFBE21"), 112);

        // The classic SummaryInformation FMTID/PIDs (PIDSI_TITLE=2 etc.) -
        // reused here as a generic identity, not because the file is OLE
        // structured storage (it isn't - StgOpenStorage fails on it, see
        // CLAUDE.md). SolidWorks's own handler reports these same five under
        // this same identity, sourced from its internal Summary tab data -
        // exactly what ISwDMDocument.Title/Author/Subject/Comments/Keywords
        // already expose (confirmed matching blank values on the test file).
        private static readonly Guid SummaryInfoFormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

        public static readonly PROPERTYKEY Title = new PROPERTYKEY(SummaryInfoFormatId, 2);
        public static readonly PROPERTYKEY Subject = new PROPERTYKEY(SummaryInfoFormatId, 3);
        public static readonly PROPERTYKEY Author = new PROPERTYKEY(SummaryInfoFormatId, 4);
        public static readonly PROPERTYKEY Keywords = new PROPERTYKEY(SummaryInfoFormatId, 5);
        public static readonly PROPERTYKEY Comment = new PROPERTYKEY(SummaryInfoFormatId, 6);

        // System.Rating (FMTID 64440492-4C8B-11D1-8B70-080036B11A03#9) is
        // deliberately excluded - no SWDM equivalent exists to source a star
        // rating from, so it stays unserved, same as under SolidWorks's own
        // handler (also always blank there).
        public static readonly PROPERTYKEY[] All =
            { Description, OpenTime, LastSavedWith, Title, Subject, Author, Keywords, Comment };

        // Case-insensitive - excluded from both fields.json (see
        // FieldListEditor) and the auto-match-against-existing-columns logic
        // (see SwPropertyStore), since all of these are already always
        // served here. Broader than just the 3 originally-unique names:
        // "Title" and "Subject" in particular are exact matches against the
        // SAME PROPERTYKEY this class already serves via the Summary tab -
        // a file with an actual custom property literally named "Title"
        // would otherwise auto-match to the identical PKEY, a real
        // double-serving collision, not just a label collision.
        public static readonly string[] ReservedNames =
        {
            "Description", "OpenTime", "LastSavedWith",
            "Title", "Subject", "Author", "Authors", "Comment", "Comments", "Keywords", "Tags"
        };

        public static bool KeyEquals(PROPERTYKEY a, PROPERTYKEY b) => a.fmtid == b.fmtid && a.pid == b.pid;

        // File-format version code -> product year, from SolidWorks's own
        // published API documentation (not reverse-engineered) - confirmed
        // against two real installs on this machine (SW2019 = 12000,
        // SW2020 = 13000). See CLAUDE.md, Phase 2 / item 8.
        private static readonly Dictionary<int, string> VersionTable = new Dictionary<int, string>
        {
            { 1500, "2000" }, { 1750, "2001" }, { 1950, "2001Plus" }, { 2200, "2003" },
            { 2500, "2004" }, { 2800, "2005" }, { 3100, "2006" }, { 3400, "2007" },
            { 3800, "2008" }, { 4100, "2009" }, { 4400, "2010" }, { 4700, "2011" },
            { 5000, "2012" }, { 6000, "2013" }, { 7000, "2014" }, { 8000, "2015" },
            { 9000, "2016" }, { 10000, "2017" }, { 11000, "2018" }, { 12000, "2019" },
            { 13000, "2020" }, { 14000, "2021" }, { 15000, "2022" }, { 16000, "2023" },
            { 17000, "2024" }, { 18000, "2025" }, { 19000, "2026" },
        };

        public static string FormatVersion(int code)
        {
            // Unknown code (a future SolidWorks version this table hasn't
            // been updated for) -> blank, not a guess.
            return VersionTable.TryGetValue(code, out string year) ? $"SOLIDWORKS {year}" : null;
        }
    }
}
