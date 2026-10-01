# SwFileExplorerCustomColumns

## Goal
Get SolidWorks (.sldprt / .sldasm / .slddrw) custom properties to show up as
sortable/filterable columns in Windows Explorer.

## Background / what we've learned so far (mechanism now fully confirmed)
- **Correction: SolidWorks files are NOT OLE structured-storage (compound
  binary) files**, at least not for SW2019+ parts - this was the original
  assumption at the start of this project and it's wrong. Checked directly:
  `220-320612 WalkAir_WheelAxle.SLDPRT`'s first 8 bytes are
  `34 81 2E 9E 00 00 00 04`, not the OLE signature `D0 CF 11 E0 A1 B1 1A E1`.
  `StgOpenStorage` fails on it (`HRESULT 0x800300FF`) both from this
  project's own process and from a completely fresh one. Whatever internal
  format SolidWorks uses now, it is not a real OLE compound file that
  generic Windows structured-storage code (or any non-SolidWorks component)
  could read directly.
- **The real mechanism, found by reading the registry and SolidWorks' own
  property schema file directly (not guesswork):**
  - `.sldprt`/`.sldasm`/`.slddrw` each register exactly one relevant shell
    extension: `PersistentHandler` -> CLSID `{AA261FDE-AB29-429c-9DF1-
    0EDEABAAFB7D}` ("SolidWorks Filter") -> implemented by
    `C:\Program Files\Common Files\SOLIDWORKS Shared\sldsearchifilter.dll`.
    This is a read-only Windows Search IFilter, not a writable property
    handler - there is no registered component that lets Explorer's
    Properties dialog write values back into the file for any field.
  - SolidWorks separately registers its own property schema (visible under
    `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\
    PropertySchema\0001`, pointing at
    `C:\ProgramData\SolidWorks\SOLIDWORKS 2019\lang\english\xmlschema\
    solidworksproperties.propdesc`). That XML file defines **24** properties
    in the `Solidworks.Document.*` namespace (Description, Configurations,
    References, Features, CustomProperties, Notes, Tables, Material, Number,
    UserDescription, Project, Author, LastSavedWith, OpenTime, Sketches,
    Layers, Sheets, Attributes, Cuts, Extrusions, Blocks), all marked
    `isColumn="true" isViewable="true" isQueryable="true"`.
  - What actually shows in the file's Properties -> Details tab is a
    separate, much shorter, curated list SolidWorks installs in the registry
    under `HKCR\SldPart.Document` (`FullDetails` / `PreviewDetails` /
    `InfoTip` values - same for `SldAsm.Document`/`SldDrw.Document`). That's
    what limits the visible field count compared to e.g. Office documents,
    which register a much longer `FullDetails` list.
  - **First-pass test (superseded below):** queried all 24
    `Solidworks.Document.*` properties directly against a real part
    (`220-320612 WalkAir_WheelAxle.SLDPRT`) using
    `(Shell.Application).NameSpace(folder).ParseName(file).ExtendedProperty
    (name)` - this is Explorer's live, on-demand per-item property query
    (the same thing that would back a column's displayed value). Result:
    only **3 of 24** returned a value this way - `Description`,
    `LastSavedWith` ("SOLIDWORKS 2019"), `OpenTime` ("0:01"). All other 21 -
    including `Material` and `Number`, which do exist as real custom
    properties on that file - came back blank through this specific path.
  - **Ground truth, found by driving the filter's `IFilter` COM interface
    directly** (`SwFilterDump` tool, below) **- this corrects the above.**
    `IFilter::Init`/`GetChunk`/`GetValue` (the same low-level mechanism the
    Windows Search crawler uses, bypassing Explorer's live-query path
    entirely) shows `sldsearchifilter.dll` actually has real extraction
    logic for far more than 3 properties. Against the same test file it
    emitted 22 chunks, including a real, **resolved** value for
    `Solidworks.Document.Material` (`10B21`, not the raw formula-link string
    `SW-Material@<filename>` that `ReadSwProperties`/the Document Manager
    API shows), plus `.Number`, `.Project`, `.Configurations`,
    `.References`, `.UserDescription`, `.Features`/`.Sketches`/`.Extrusions`
    (full feature-tree/sketch text), `.Attributes`/`.Blocks`/`.Cuts`/
    `.Layers`/`.Notes`/`.Sheets`/`.Tables`, generic `System.Title`/
    `System.Keywords`/`System.Author`, `PerceivedType`, and a
    `Solidworks.Document.CustomProperties` chunk that is a single blob
    containing **every** named custom property on the file as `Name: Value`
    lines (including ones with no schema entry at all, like the Manufacturer
    /Model/Owner/Classification/Project/Language/Priority/Status properties
    added during earlier testing). One `CHUNK_TEXT` chunk
    (`System.Search.Contents`) feeds Windows Search's free-text index with
    the raw (unresolved) property text.
  - **SOLVED: the mechanism behind Description/LastSavedWith/OpenTime.**
    Spent a long time ruling out wrong candidates first (kept below for the
    record, since the elimination is what made the real answer findable):
    not `IFilter` under any calling convention (default, explicit single-
    property request via `aAttributes`, and each of the three `IFILTER_INIT`
    attribute-category flags individually - all byte-for-byte identical,
    never including those 3); not `IPropertyStore` on the filter object; not
    a real embedded OLE property-set stream (file isn't OLE compound storage
    at all); not any of the other shell extensions registered on `.sldprt`
    (`sldwinshellextu.dll`'s InfoTips/icon-overlay CLSIDs,
    `sldthumbnailprovider.dll`); not the persisted Windows Search index
    (`[Solidworks.Document.Description]` and `[...Material]` both fail
    identically as SELECT columns). Microsoft's own docs ("Returning
    properties from a filter handler") say a filter-only component has
    exactly 2 ways to expose a property, both ruled out - which was the
    clue that a **real property handler must exist**, just not registered
    where we'd been looking (the per-extension `shellex\{ECDD6472-...}`
    slot, confirmed absent both under `.sldprt` and under `SldPart.Document`).
    - **Found it by scanning every SolidWorks DLL's raw bytes** (225+ files,
      ~582MB under `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\` and
      `...\Common Files\SOLIDWORKS Shared\`) for the literal canonical
      property names and their FMTID GUIDs in **raw binary form** (a
      compiled `DEFINE_GUID`-style constant is stored as 16 raw bytes, not
      as a dash-separated ASCII string - the earlier string-only scan of
      `sldsearchifilter.dll` alone had missed this because it only checked
      formatted-string GUIDs). One file lit up with everything:
      **`sldpropertyhandler.dll`** (`C:\Program Files\SOLIDWORKS Corp\
      SOLIDWORKS\sldpropertyhandler.dll`) - contains `Solidworks.Document.
      LastSavedWith`, `Solidworks.Document.Description`, both FMTID GUIDs,
      and even the literal string `solidworksproperties.propdesc`.
    - Its self-registered CLSID is `{6A921E8A-C58C-4941-9E71-7946D9DCE941}`,
      named **"CSolidworkPropertyStore Class"** - not wired into `.sldprt`'s
      ShellEx chain, which is exactly why the earlier per-extension registry
      audit never found it.
    - **The actual registration point is a completely separate, generic,
      machine-wide table we hadn't checked**: `HKLM\SOFTWARE\Microsoft\
      Windows\CurrentVersion\PropertySystem\PropertyHandlers\<extension>`.
      This single table covers **hundreds** of file extensions across many
      formats and vendors (`.doc`, `.docx`, `.avi`, `.dng`, `.bmp`, etc.) -
      it *is* the "common protocol many formats use" answer: a built-in
      Windows Property System fallback lookup, keyed purely by extension,
      independent of the shell namespace's ProgID/ShellEx registration
      chain entirely. `.sldprt`, `.sldasm`, and `.slddrw` are all registered
      there, each pointing to `{6A921E8A-C58C-4941-9E71-7946D9DCE941}`.
    - **Confirmed by direct instantiation** (`SwFilterDump`'s
      `TestPropertyHandler`, below): created the CLSID, it implements real
      `IInitializeWithFile` + `IPropertyStore` (not `IPersistFile`).
      Initialized with the test file, then `GetCount()` returned **exactly
      9** - a hard, compiled-in cap, confirmed by enumerating every one via
      `GetAt()`/`GetValue()`:
      `Solidworks.Document.Description` = WalkAir_WheelAxle,
      `Solidworks.Document.OpenTime` = 0:01,
      `Solidworks.Document.LastSavedWith` = SOLIDWORKS 2019,
      plus `System.Comment`/`Author`/`Title`/`Subject`/`Rating`/`Keywords`
      (blank - the classic `SummaryInformation`/`FMTID_SummaryInformation`-
      family fields SW's own "Summary" tab wasn't filled in for on this
      file). **`Material`/`Number`/etc. are not in this list at all and
      never will be** - this handler doesn't attempt to report them,
      independent of the `.propdesc` schema's `<labelInfo>` mechanism
      entirely. This is why the earlier Material-labeling experiment showed
      a real value existing (via `IFilter`) yet still not appearing live:
      the label controls whether Explorer *offers* the column, but this
      separate, fixed-9-property handler is what actually *serves* any
      value, and it was never going to report Material regardless.
    - Note one small oddity found along the way: `LoadLibrary` on
      `sldpropertyhandler.dll` fails with `ERROR_MODULE_NOT_FOUND` (126)
      unless both SolidWorks install directories are on the calling
      process's `PATH` first - it depends on sibling DLLs normally already
      resolvable from inside a real SolidWorks process. `SwFilterDump`
      prepends both directories to its own process `PATH` before loading it.
    - **Practical implication - this materially changes Option 2's risk
      profile below.** We now have a complete, concrete, working reference
      for exactly what a real property handler looks like: the exact
      registry key to repoint (`PropertyHandlers\.sldprt` etc.), the exact
      interfaces to implement (`IInitializeWithFile` + `IPropertyStore`),
      and a live example object to compare behavior against. Writing our
      own handler and pointing this same key at it (instead of at
      `sldpropertyhandler.dll`) is now a well-understood, de-risked path -
      not a leap into unknown COM territory.
    - Kept for the record: earlier open-source archaeology (ReactOS's
      shell32 - `CFSFolder::GetDetailsEx` → `SH32_GetDetailsOfPKeyAsVariant`
      → `MapSCIDToShell32FsColumn`) showed a call chain that should fail for
      any PKEY outside a tiny hardcoded set, seemingly contradicting
      observed behavior. That's now explained too: real Windows' actual
      `CFSFolder`/property-resolution code evidently also consults the
      `PropertyHandlers\<ext>` table as an additional path this specific
      open reimplementation doesn't replicate - not a mystery, just a gap
      between a third-party reimplementation and real `shell32.dll`.
  - **What the `IFilter` dump is still useful for:** the filter's real
    extraction capability (Material, Number, Project, feature tree,
    sketches, etc. - everything above) is genuine and broader than first
    thought, and likely still feeds Windows Search's indexed/structured
    queries once crawled (a `CONTAINS(*, '10B21')` retest came back empty,
    but that's consistent with `CONTAINS()` only covering the one
    `CHUNK_TEXT` blob, which holds the *raw unresolved* text, not the
    resolved value - not proof the data is unindexed some other way). That
    data could matter for a sync approach (Option 1) or for understanding
    what a custom property handler (Option 2) would need to replicate.
  - **Where the Explorer column label comes from:** the `<labelInfo
    label="...">` element inside each `<propertyDescription>` in the
    `.propdesc` XML is the single source of truth for the display label -
    nothing else maps name to label. Only 3 of the 24 properties have a
    `<labelInfo>` at all (Description, LastSavedWith, OpenTime). Confirmed
    empirically: the other 21 (Material, Number, Author, Project,
    Configurations, etc., all lacking `<labelInfo>`) do **not** appear
    anywhere in Explorer's 325-entry column list (`ExplorerColumns.csv`) -
    no label means the property isn't offered as a selectable UI column at
    all, even though it's still a valid queryable canonical name
    programmatically (e.g. via `ExtendedProperty`). This is on top of, and
    separate from, the fact that those properties also don't get populated
    with values (see above) - two independent gaps stacked on each other.
  - **Superseded:** an earlier strings-extraction pass over the DLL's raw
    bytes (no `strings.exe` available, so done by hand) found literal
    references to `Material`, `Author`, `Project`, `Configurations`,
    `References`, `CustomProperty`, and a `CONTAINS(*, 'SW-Material')`
    full-text-search test came back with no match, which was read at the
    time as "these DLL strings don't do anything confirmed." The `IFilter`
    dump above shows this was a limitation of the test, not the DLL: it
    tested the *raw unresolved link string*, and only the free-text
    `CHUNK_TEXT` blob (which does contain that exact raw string) feeds
    `CONTAINS()` - the properties themselves are real and populated, just
    not through that specific query path. See "Ground truth" above.
- Earlier name-collision guessing (testing "Category", "Company", "Status",
  "Manufacturer", "Owner", etc. as SW custom property names) is now
  superseded by the above - kept only as a record that the guessing
  approach was tried before the registry/schema was found:
  - Confirmed working via guessing: "Description" (now explained above).
  - Confirmed NOT working via guessing: "Category", "Categories", "Company",
    "Subject", "Status", "Manufacturer", "Model", "Classification",
    "Project", "Language", "Priority" (consistent with the schema finding -
    none of these map to a `Solidworks.Document.*` property SW's filter
    populates).
  - "Owner" looked like it worked but is a false positive: Explorer's Owner
    column is fed by NTFS file-ownership metadata (`PKEY_FileOwner`), not
    file content, so it shows the Windows username on every file regardless
    of custom properties. Methodological takeaway for any future guessing:
    a column that already has a value on every file (ownership, dates,
    size, type, etc.) can't be used to test this behavior at all.

## Files in this folder
- `List-ExplorerColumns.ps1` - no prerequisites. Dumps every named Explorer
  detail column on this PC to `ExplorerColumns.csv`, for cross-referencing
  candidate property names against the SW Custom tab. **Run** - found 325
  named columns.
- `Program.cs` / `ReadSwProperties.csproj` - small console app that reads a
  SolidWorks file's custom properties directly via the Document Manager API
  (`SolidWorks.Interop.swdocumentmgr`), independent of Explorer. **Built and
  run successfully** against `220-320612 WalkAir_WheelAxle.SLDPRT`. Required
  fixes, now done:
  1. Document Manager API license key supplied. The key is **not** in source -
     `Program.cs` reads it at runtime from the `SWDM_LICENSE_KEY` environment
     variable, and the key itself (plus the exact commands to set it) lives in
     `SwDmLicenseKey.md`, which `.gitignore` excludes. **Read that file when a
     run of this app needs the key**, and set the variable for that shell only -
     never paste the key back into a tracked file, a commit, or terminal output.
  2. `HintPath` in `ReadSwProperties.csproj` corrected to
     `C:\Program Files\Common Files\SOLIDWORKS Shared\SolidWorks.Interop.swdocumentmgr.dll`
     (the guessed default path was wrong).
  3. The code originally called a nonexistent method
     (`GetAllCustomPropertyNamesAndValues`); fixed to use the real API
     (`GetCustomPropertyNames()` + `GetCustomProperty(name, out type)`),
     found via reflection against the actual interop DLL.
  4. `Console.ReadKey()` at the end crashed when input was redirected
     (non-interactive runs); guarded with `Console.IsInputRedirected`.
  Builds as x64 / net48 - SolidWorks 2020+ is 64-bit only.
- `SwFilterDump/` - separate project (its own subfolder, so SDK-style file
  globbing doesn't collide with `ReadSwProperties.csproj`). Drives
  `sldsearchifilter.dll`'s `IFilter` COM interface directly (`Init`/
  `GetChunk`/`GetValue`), the same low-level mechanism the Windows Search
  crawler uses - shows exactly what the filter extracts, ground truth
  rather than guessing from names. **Built and run successfully.** Uses
  `NativeMethods.cs`, vendored from the MIT-licensed
  [IFilterTextReader](https://github.com/Sicos1977/IFilterTextReader)
  project (only the raw COM interop declarations, not its higher-level
  `FilterReader`/`FilterLoader` classes - those wrap loading in a custom
  `IStream` and silently swallow the real COM error when that fails,
  which is what happened here; this project uses `IPersistFile.Load`
  directly instead since a real file path is available). CLSID and DLL
  path are hardcoded from values already confirmed in the registry, so no
  runtime registry lookup is needed. Also has a `--refresh-schema` mode
  (`PSUnregisterPropertySchema` + `PSRegisterPropertySchema` against
  `solidworksproperties.propdesc`) used to test schema edits - see the
  label experiment above; this was used and the schema is back to its
  original, unedited state. Also has `TestInfoTips` (drives
  `sldwinshellextu.dll`'s InfoTips CLSID via `IQueryInfo::GetInfoTip`) and
  `TestPropertyHandler` (drives `sldpropertyhandler.dll`'s
  `CSolidworkPropertyStore` via `IInitializeWithFile` + `IPropertyStore` -
  the component that turned out to actually serve Description/LastSavedWith
  /OpenTime's live values; see "SOLVED" above). Prepends both SolidWorks
  install directories to its own process `PATH` in `Main` before any
  `LoadLibrary` call, since `sldpropertyhandler.dll` depends on sibling
  DLLs not resolvable otherwise outside a real SolidWorks process. Builds
  as x64 / net48.
- `SwDmLicenseKey.md` - **secret, git-ignored.** Holds the SolidWorks Document
  Manager API license key and how to set `SWDM_LICENSE_KEY` from it. Not in the
  repo; exists only on this machine.
- `FieldListEditor/` - Phase 1. WinForms list editor (Add/Remove) for the
  tracked SolidWorks custom-property names in
  `C:\ProgramData\SwFileExplorerCustomColumns\fields.json`. No elevation,
  no registry/COM work. **Built, run, verified interactively.** Own
  subfolder for the same file-globbing reason as `SwFilterDump`.
- `SchemaApplyTool/` - Phase 2. Elevated WinForms tool
  (`app.manifest` requires administrator) with three actions: Apply Schema
  (generate/register our own `.propdesc` under one permanent shared FMTID),
  Test Repoint (write a placeholder CLSID to `PropertyHandlers` for all
  three SolidWorks extensions), Revert (write back the hardcoded original
  SolidWorks CLSID). **Built, run, verified** - schema add/remove/re-add
  confirmed via real `List-ExplorerColumns.ps1` column-count changes, and
  the full repoint/revert cycle confirmed live against this machine's real
  registry (see Phase 2 below for detail). Must be launched via
  `ShellExecute` (e.g. `Start-Process -Verb RunAs`), not a plain
  `CreateProcess` call, or elevation silently fails with
  `ERROR_ELEVATION_REQUIRED` and the exe never starts.
- `SwPropertyHandler/` - Phase 3. The actual property handler: a managed
  COM class (`SwPropertyStore`, CLSID
  `{E558E17D-51E7-4043-89D8-5EDB8498454F}`) implementing
  `IInitializeWithFile` + `IPropertyStore` via raw .NET COM interop (no
  SharpShell - see Phase 3 below for why), reading tracked fields from
  `fields.json` and resolved values via SWDM's `GetCustomPropertyValues`.
  Registered via `regasm.exe /codebase` (x64 Framework regasm), also
  requiring `ShellExecute`-based elevation. `LicenseKey.cs` is
  **secret, git-ignored** (added in the same commit as this project) - the
  SWDM license key baked in as a compiled constant. **Built, core logic
  verified** against the real test part; the broader risk-test matrix
  (fixtures, locked files, concurrency, performance) is deferred - see
  Phase 3 below.
- `SwPropertyHandlerTest/` - throwaway-style console harness for
  `SwPropertyHandler`, same pattern as `SwFilterDump`'s
  `TestPropertyHandler`: forces genuine COM activation via
  `Type.GetTypeFromCLSID` + `Activator.CreateInstance` and exercises
  `IInitializeWithFile`/`IPropertyStore` directly, independent of Explorer.
- `README.md` - fuller run instructions for both scripts.

## Not yet done / open questions
- **Fully resolved:** which properties show as live Explorer *columns*
  (gated by `<labelInfo>` in the schema), which the filter can *extract at
  all* (nearly everything, via `IFilter`), and - now - **what actually
  serves live values** (`sldpropertyhandler.dll`'s `CSolidworkPropertyStore`,
  registered per-extension in `HKLM\...\PropertySystem\PropertyHandlers\`,
  hard-capped at exactly 9 properties). See "SOLVED" above. Nothing left
  unexplained in the existing mechanism.
- **Resolved, decision made:** evaluated all three options below against two
  hard constraints - (a) can it create a genuinely *new* named column (e.g.
  "Material"), not just reuse an existing one, and (b) does it depend on
  undocumented/reverse-engineered behavior of SolidWorks's own binaries.
  - **Option 1 (sync into existing fields) dismissed.** Explorer only ever
    asks the one registered `PropertyHandlers\.sldprt` component for a
    property value; a brand-new schema name with no handler behind it
    always renders blank. The sync approach can only ever repurpose the
    ~5 existing writable Summary Info fields (`Title`/`Author`/`Subject`/
    `Comments`/`Keywords`) that `sldpropertyhandler.dll` already serves -
    it cannot create a column literally labeled "Material". Since the goal
    requires real new column names, this option doesn't meet it.
  - **Option 3 (Xarial commercial tool) dismissed** (user call, not
    re-litigated here).
  - **Option 2 (real property handler) is the direction**, but redesigned
    to be safer than originally scoped: instead of reading
    `sldsearchifilter.dll`/`sldpropertyhandler.dll`'s undocumented internal
    behavior, source every property from the licensed, documented
    **SolidWorks Document Manager (SWDM) API** - the same API
    `ReadSwProperties`/`Program.cs` already uses successfully. Checked
    directly (see below) that SWDM alone covers everything the existing
    handler serves, so our handler can fully replace
    `sldpropertyhandler.dll`'s registration with **no forwarding/wrapper
    dependency on it** - removing the "might silently drop something we
    didn't know `sldpropertyhandler.dll` did" risk entirely, since we're
    not relying on it at runtime at all.

### SWDM API coverage check (confirms Option 2 can be fully self-sufficient)
Probed `SolidWorks.Interop.swdocumentmgr.dll` via reflection (all
`ISwDMDocument`/`ISwDMDocument2`...`25` interfaces) and then live, against
`220-320612 WalkAir_WheelAxle.SLDPRT`, to check whether SWDM alone can
supply every property `sldpropertyhandler.dll`'s `CSolidworkPropertyStore`
reports (see "SOLVED" above), without touching that DLL at all:

| Handler's property | SWDM source | Verified result |
|---|---|---|
| `System.Title`/`Author`/`Subject`/`Comments`/`Keywords` | `ISwDMDocument.Title`/`Author`/`Subject`/`Comments`/`Keywords` (also have `set_*` - full read/write) | Blank on the test file both ways - matches |
| `OpenTime` | `ISwDMDocument25.GetFileAvgTime(out bsFileTime, out bsLWFileTime)` | Returned `"0 mins 01 secs"` - same value as the handler's `"0:01"`, different formatting only |
| `Description` | **Not Summary Info** - turned out to be the literal custom property named `"Description"` | File has a real custom property `Description = WalkAir_WheelAxle` (set during earlier guessing-phase testing) - exact match. Closes the "SOLVED" section's remaining ambiguity: the handler special-cases one specific custom-property name, same table `Material`/`Number` live in, it just doesn't report those other names through this interface. |
| `LastSavedWith` | `ISwDMDocument.GetVersion()` | Returned raw int `12000` on this file. Confirmed against SolidWorks's own published API docs' version table: `12000 = SOLIDWORKS 2019` - exact match with the known `LastSavedWith` value. Full table (file-format version code -> product year), for any future file: 1500=2000, 1750=2001, 1950=2001Plus, 2200=2003, 2500=2004, 2800=2005, 3100=2006, 3400=2007, 3800=2008, 4100=2009, 4400=2010, 4700=2011, 5000=2012, 6000=2013, 7000=2014, 8000=2015, 9000=2016, 10000=2017, 11000=2018, 12000=2019, 13000=2020, 14000=2021, 15000=2022, 16000=2023, 17000=2024, 18000=2025, 19000=2026. |

**Bigger finding - resolved values for linked custom properties, no `IFilter` needed:**
`GetCustomProperty`/`GetCustomProperty2` only return the raw unresolved
formula-link string for a linked property (e.g. `Material` ->
`"SW-Material@...SLDPRT"`), matching what `ReadSwProperties` already showed.
But `ISwDMDocument25.GetCustomPropertyValues(name, out type, out linkedTo)`
returns the **resolved** value directly - tested against `Material` and got
`"10B21"`, with `linkedTo` holding the raw formula string separately. This
is the exact same resolved value the `IFilter` dump needed driving
`sldsearchifilter.dll` directly to obtain (see "Ground truth" above) - SWDM
alone gets us there instead, through the documented/licensed API.

**Conclusion: nothing about our own property handler needs to depend on, or
read, SolidWorks's own `sldsearchifilter.dll`/`sldpropertyhandler.dll`
behavior at runtime.** All of it - every custom property (resolved),
Summary Info fields, OpenTime, LastSavedWith - is available through SWDM.

## Longer-term options under consideration
1. ~~Sync approach~~ - **dismissed**, see above (can't create new named
   columns).
2. **Real Windows property handler, sourced entirely from SWDM - chosen
   direction.** Implement `IInitializeWithFile` + `IPropertyStore` (both
   already prototyped in `SwFilterDump`'s `TestPropertyHandler`), backed by
   SWDM reads/writes instead of reverse-engineered DLL behavior. Register
   our own CLSID at `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\
   PropertySystem\PropertyHandlers\.sldprt` (and `.sldasm`/`.slddrw`),
   replacing `sldpropertyhandler.dll`'s current
   `{6A921E8A-C58C-4941-9E71-7946D9DCE941}` entry - fully, not as a
   wrapper, since SWDM covers everything that DLL served (see coverage
   check above). Also needs our own `.propdesc` schema (separate from
   SolidWorks's, our own namespace) with `<labelInfo>` for each property we
   want selectable as a column. Remaining real risk is unchanged from
   before: this component loads inside `explorer.exe`, so a bug there can
   hang/crash the shell machine-wide - mitigate with defensive error
   handling/timeouts, and test on a non-production machine/VM first,
   exercising normal SolidWorks workflows (not just Explorer) after
   repointing the registry key, since the key's blast radius isn't fully
   known (see risk discussion - nothing deletes/unregisters
   `sldpropertyhandler.dll`'s own CLSID, only this one lookup entry, so
   reverting is a single registry value).
3. ~~Xarial CAD+ Toolset~~ - **dismissed** (user call).

## Implementation plan (current - build in this order)
Each phase has a defined set of tests that must pass before moving to the
next phase - do not start the next phase until the current one's tests pass.
Risks noted per phase are addressed when that phase is actually worked on,
not before.

### Plan review - holes found and resolved (session 3)
Went over the plan item by item looking for gaps before starting Phase 0.
Ten items came up; outcomes below are folded into the relevant phases
further down, this section just records the reasoning (several were settled
by checking the real `solidworksproperties.propdesc` file or this machine's
actual registry instead of guessing):

1. **PROPERTYKEY stability - resolved, later simplified in Phase 0.**
   Checked the real `solidworksproperties.propdesc` rather than inventing a
   scheme: SolidWorks uses **one shared FMTID** for its entire schema
   (`{6A9EEB69-672C-4B73-B1F3-A6EF662CF3C2}`) with a plain incrementing
   `propID` per property (100, 101, 102...). We do the same: one FMTID
   generated once for our schema, plus a name->PID assignment per field so
   Explorer's saved per-folder column layouts (keyed by the full
   `{FMTID, PID}` pair) don't collide across different properties. Original
   plan kept retired PIDs reserved forever via a separate append-only
   registry file; collapsed in Phase 0 to a single `fields.json` instead,
   accepting a narrow residual risk instead (see Phase 0 below) - judged
   low-severity enough not to justify the second file. This is also why two
   Explorer columns can both be labeled "Description" with no conflict
   (SolidWorks's own vs.
   Windows' generic one) - identity is the key pair, not the label.
2. **Field name vs. schema identifier vs. display label - resolved.**
   SolidWorks's own schema sidesteps this by hand-writing fixed XML: no
   runtime user input. Ours takes free-text field names (which may contain
   spaces/punctuation, e.g. "Part Number") that must become a valid
   propdesc identifier. Resolution: auto-sanitize to build the canonical
   schema name, but keep the original string as both the SWDM
   property-name lookup and the `<labelInfo label="...">` display text.
3. **Property type / sort semantics - resolved, simpler than expected.**
   Checked every `typeInfo` in the real schema file:
   `grep -o 'typeInfo type="[^"]*"' solidworksproperties.propdesc` returned
   **`String` for all 21 matched entries, with zero exceptions** - including
   `OpenTime`, which holds a literal duration string (`"0:01"`) and would be
   the obvious candidate for numeric/duration typing. SolidWorks didn't
   bother differentiating at all. We're doing the same: every tracked field
   is registered as `type="String"`, no type picker in the editor. Revisit
   only if a specific field's sort behavior turns out to matter in
   practice.
4. **Licensing inside the shell process - resolved.** The SWDM license key
   is currently read from an environment variable in `Program.cs` purely as
   a *git-hygiene* choice (keep the literal out of tracked source) - that
   constraint is about what's committed, not how a compiled program gets
   the value at runtime. The property handler will have the key **baked in
   as a compiled constant** instead - no environment variable to read at
   all, so no question of whether `explorer.exe`'s or the Search Indexer
   service's account can see it. Tradeoff accepted: rotating the key later
   needs a rebuild, not just a file edit - fine for a solo-machine tool
   with a key that's effectively a static pass/fail check, unlikely to
   change how it's validated.
5. **SolidWorks has the file open while Explorer reads it - queued, not
   solved.** Probably the most common real scenario (browsing while the
   part is open in SolidWorks). Not solved now; queued as a Phase 3 test
   case. Working assumption (SolidWorks uses a separate temp/image file
   mechanism for open files, and SWDM's read-only open mode is documented
   to coexist with a running SolidWorks session) is plausible but untested -
   verify for real once Phase 3's handler exists.
6. **Concurrency/thread-safety of the SWDM COM objects - queued.** Unknown
   whether a shared `ISwDMApplication` instance is safe/fast enough to
   reuse across concurrent `GetValue` calls (Explorer/the indexer may query
   many files in a folder at once), or whether each call needs its own
   instance (with that overhead feeding the performance risk below). Queued
   as a Phase 3 test case - measure directly once there's handler code to
   measure.
7. **Redeploying the handler DLL while it's loaded - noted as a workflow
   step, not solved.** Once loaded into a running `explorer.exe` (or the
   Search Indexer), the DLL file can't just be overwritten. Updating the
   handler during Phase 4 iteration will require killing/restarting
   `explorer.exe` first (disruptive - closes open Explorer windows) as a
   normal part of the deploy step.
8. **`PropertyHandlers` key reverting after a SolidWorks repair - resolved,
   confirmed empirically.** Original plan called for dynamically *backing
   up* the current registry value before our first repoint - fragile (a
   second Apply run could back up our own value instead of SolidWorks's
   original). Fix: **hardcode the known original CLSID**
   (`{6A921E8A-C58C-4941-9E71-7946D9DCE941}`) as the permanent revert
   target instead - no backup needed at all. Checked whether this CLSID is
   actually stable rather than assuming it: this machine has both SW2019
   (`SOLIDWORKS\sldpropertyhandler.dll`, file version 27.5.0.0072) and
   SW2020 (`SOLIDWORKS (2)\sldpropertyhandler.dll`, file version
   28.5.0.0078) installed side by side. Scanned both binaries directly for
   the CLSID string - **identical in both**
   (`6A921E8A-C58C-4941-9E71-7946D9DCE941`). The live
   `PropertyHandlers\.sldprt` registry value currently resolves to the
   SW2020 copy (whichever version's installer/repair ran most recently
   wins the single shared slot). A CLSID is a vendor-chosen constant baked
   into the component at build time, not generated per-machine or
   per-install, so this is expected to hold for any machine running either
   of these versions, and - confirmed now across two real versions, not
   just one - likely for other versions too, though that's not proven
   beyond 2019/2020. Practical result: revert always means "write
   `{6A921E8A-C58C-4941-9E71-7946D9DCE941}`" - the same action whether
   we're deliberately uninstalling or just re-asserting our own repoint
   after something else (e.g. a SolidWorks repair) silently changed it
   back. **Ongoing maintenance note:** re-run Apply after any SolidWorks
   update/repair to confirm the key still points at our handler.
9. **Test fixture set too narrow - queued.** Every test described in this
   plan references one single file
   (`220-320612 WalkAir_WheelAxle.SLDPRT`). Before Phase 3/4 testing is
   meaningful, the fixture set needs: an assembly (`.sldasm`), a drawing
   (`.slddrw`), and a part with zero custom properties, in addition to the
   existing part (which already covers a resolved linked property -
   Material). Build this out when reaching that testing stage, not before.
10. **Explorer live-refresh after Apply - queued.** Phase 2 assumes
    `SHChangeNotify` (or equivalent) makes new columns selectable in
    already-open Explorer windows without a restart. That's carried over
    from general shell-extension knowledge, not verified for property
    schema registration specifically. Needs to be an explicit Phase 2 test;
    if it doesn't hold, the fallback is simply documenting that Explorer
    windows need to be reopened after Apply.

### Phase 0 - shared config format (done, tested)
One file, `%ProgramData%\SwFileExplorerCustomColumns\fields.json`, both an
unprivileged GUI (Phase 1) and a shell-loaded COM component (Phase 3) need
to read. Originally planned as two files (an editable list plus a separate
append-only name->PID registry), collapsed to one on review: easier to
hand-edit, and "behind the scenes, 1 file or 10 doesn't matter" - the
two-file split only existed to guarantee a removed field's PID is never
reused. Decided the simpler one-file model is worth the narrow residual
risk that entails (see item 1 below).

Flat JSON object, name -> PID, under our one shared FMTID (item 1 above):
```json
{
  "Material": 100,
  "Weight": 101,
  "Thickness": 102,
  "Project": 103
}
```
- The object's keys *are* the active field list - no separate list to keep
  in sync.
- Adding a field: compute `max(existing values, default 99) + 1`, add the
  key with that PID.
- Removing a field: delete the key outright. **Accepted tradeoff:** since
  PIDs aren't retired, if the removed field held the current max PID, a
  later new field can be assigned that same number. Residual impact judged
  low-severity: a PROPERTYKEY's label/value always resolves from the
  *current* schema, not a cached one, so an old Explorer folder view that
  had that column enabled would just start showing the new field's data
  under that slot (a cosmetic surprise, not wrong/corrupted data) rather
  than anything breaking.
- Names are stored as free text (e.g. "Part Number" is fine) - sanitizing
  into a valid schema identifier happens in Phase 2 at `.propdesc`
  generation time, not here (item 2 above). No type field - everything is
  `String` (item 3 above).
- **Tests passed:** hand-written example file parses correctly; simulated
  Add computes the correct next PID and persists it; simulated Remove
  deletes the key and persists that; re-reading after each operation
  reflects the change. Verified via a throwaway round-trip script - format
  is settled.

### Phase 1 - list editor (unprivileged GUI) - done, tested
`FieldListEditor/` (its own subfolder, same reasoning as `SwFilterDump` -
keeps its `Program.cs` from colliding with the root-level
`ReadSwProperties.csproj`'s SDK-style file globbing). WinForms, net48,
`<UseWindowsForms>true</UseWindowsForms>`, no extra NuGet dependency -
`System.Web.Script.Serialization.JavaScriptSerializer` (built into .NET
Framework via a `System.Web.Extensions` reference) handles the JSON
read/write. `ListBox` + textbox + Add/Remove buttons, reads/writes
`fields.json` directly. Add computes `max(existing PIDs, default 99) + 1`
and writes the new key; Remove deletes the key outright (free-text names -
sanitization for the schema identifier happens in Phase 2, not here; no
type picker - everything is `String`, per item 3 above; list shows plain
names only, no PID in the UI). No elevation, no registry/COM work.
`fields.json` was seeded from the actual custom property names on the
repo's test file (via `ReadSwProperties`, not guessed) rather than
placeholder examples: `Number`, `Description`, `Material`, `Weight`,
`Thickness`, `Color`, `Company`, `Category`, `Subject`, `Categories`,
`Manufacturer`, `Model`, `Owner`, `Classification`, `Project`, `Language`,
`Priority`, `Status`, PIDs 100-117, at the real destination
(`C:\ProgramData\SwFileExplorerCustomColumns\fields.json`) - confirmed this
write needs no elevation, as planned.
- **Tests passed:** Add a field -> appeared in the list with the correct
  next PID and was written to disk; Remove a field -> disappeared from
  both the list and the file; closed and reopened the app - state
  persisted correctly; verified interactively (Goren tried Add/Remove/
  reopen directly, not just scripted).

### Phase 2 - schema generation + elevated Apply tool - done, tested
`SchemaApplyTool/` (own subfolder, same reasoning as `SwFilterDump`/
`FieldListEditor`). WinForms, net48, with an `app.manifest` setting
`requestedExecutionLevel level="requireAdministrator"` so Windows prompts
for elevation the moment the exe is launched - **note: this only works
when launched via `ShellExecute`** (double-click, or PowerShell
`Start-Process -Verb RunAs`). Launching it via a plain `CreateProcess`
call (e.g. backgrounding it from a bash shell) fails silently with
`ERROR_ELEVATION_REQUIRED` instead of prompting - hit this directly while
testing; the exe never even started, no UAC dialog appeared, no error
visible either. Worth remembering for Phase 4's packaging/launch story.

Three buttons, intentionally split so the risky step is isolated and
explicit rather than bundled into one "Apply":
- **Apply Schema** - reads `fields.json`; if our `.propdesc` already exists
  on disk, calls `PSUnregisterPropertySchema` against the *old* content
  first (skipped on a true first run); regenerates the XML (one shared
  FMTID `{42161C84-EBEC-4753-9E00-9D700D9B4361}` - generated once this
  session, now permanent, same pattern as SolidWorks's own schema; canonical
  names are `SwSync.<sanitized field name>`, e.g. "Part Number" ->
  `SwSync.PartNumber`, with the original string kept as the
  `<labelInfo label="...">` text; every property `type="String"`, per item
  3); writes the file; calls `PSRegisterPropertySchema`; sends
  `SHChangeNotify(SHCNE_ASSOCCHANGED)`. Zero risk to SolidWorks's existing
  columns - entirely our own separate schema/file.
- **Test Repoint (temporary)** - logs the current `PropertyHandlers` CLSID
  for all three extensions, then writes an obviously-fake placeholder
  (`{00000000-0000-0000-0000-000000000000}`) to all three, for testing the
  write mechanics without depending on a real handler (Phase 3 doesn't
  exist yet).
- **Revert PropertyHandlers** - writes the hardcoded original SolidWorks
  CLSID (`{6A921E8A-C58C-4941-9E71-7946D9DCE941}`, item 8) back to all
  three, then `SHChangeNotify`.

**Tests passed, all against the real machine (no VM used - see "how this
differed from the original plan" below):**
- *Schema add/remove/re-add, verified via `List-ExplorerColumns.ps1`
  column counts, not just assumed:* baseline 325 (pre-project) ->
  **344** after Apply Schema with all 19 tracked fields (confirmed every
  single field name - `Number` through `Status`, plus test additions -
  appears as a new column); removed `Weight` from `fields.json` and
  re-ran Apply Schema -> **343** (`Weight` gone, everything else intact -
  proves the unregister-before-overwrite ordering actually removes a
  dropped field, not just additively registers); restored `Weight` (new
  PID 120, since the collapsed single-file design doesn't retain retired
  PIDs - expected) and re-applied -> **344** again, `Weight` back.
- *The repoint/revert cycle - the one test flagged as needing a VM in the
  original plan, run for real instead (see below):* established a clean
  baseline first through the **actual live Explorer property-resolution
  path** (`(New-Object -ComObject Shell.Application).NameSpace(folder)
  .ParseName(file).ExtendedProperty(...)`), not the `SwFilterDump`
  `TestPropertyHandler` harness - that harness hardcodes the real CLSID
  directly and bypasses the `PropertyHandlers` registry lookup entirely, so
  it would never have reflected this test either way. Baseline: `Description
  = WalkAir_WheelAxle`, `OpenTime = 0:01`, `LastSavedWith = SOLIDWORKS 2019`.
  After **Test Repoint**: registry confirmed showing the placeholder CLSID;
  all three properties cleanly returned **empty string** through the same
  live query - no error, no crash, graceful degradation exactly as hoped.
  After **Revert PropertyHandlers**: registry confirmed back to
  `{6A921E8A-C58C-4941-9E71-7946D9DCE941}` on all three extensions; the
  same query returned the **exact original baseline values** again. Full
  round trip confirmed working in practice, not just in theory.
- *Item 10 (live refresh) - resolved:* an already-open Explorer window did
  **not** pick up the new columns in its "Choose Columns" list after Apply;
  a brand-new tab/window opened afterward (no full `explorer.exe` restart)
  **did** see them immediately. Low-severity, documented: after Apply,
  open a new tab/window rather than expecting an already-open one to
  refresh.

**How this differed from the original plan:** the plan called for testing
the `PropertyHandlers` repoint on a non-production machine/VM first, since
it temporarily breaks Description/OpenTime/LastSavedWith on real SolidWorks
files. No VM was available this session; ran it directly on the primary
machine instead, with explicit confirmation before each risky step, a
pre-established baseline to compare against, and the already-proven
hardcoded-CLSID revert as the safety net. The breakage window was seconds
long and fully confirmed reversible before moving on - judged an acceptable
substitute for a VM in this specific case, not a precedent for skipping
that caution on riskier future steps (e.g. Phase 4's full integration with
a real, untested handler).

### Phase 3 - the property handler itself - core implementation done, tested; remaining risk tests deferred
Built as raw .NET COM interop, deliberately **not** SharpShell - unconfirmed
whether SharpShell even supports `IPropertyStore`-based property handlers
(it's mostly built for context menus/thumbnails/preview handlers), and
`SwFilterDump`'s `TestPropertyHandler` already proves the exact interface
contracts work by consuming a real implementation - mirroring that directly
avoids an unverified third-party dependency for uncertain benefit.

**`SwPropertyHandler/`** - the COM class library (net48, x64, `ComVisible`
false at the assembly level, true only on the one class):
- `NativeInterop.cs` - `IInitializeWithFile` and `IPropertyStore` declared
  for *implementing* (no `[ComImport]`), with GUIDs matching the real native
  interfaces exactly (`B7D14566-...` / `886D8EEB-...`, same ones
  `SwFilterDump` already validated by consuming them).
  **Bug caught and fixed here, not copied from `SwFilterDump`:** that
  project's `PROPERTYKEY.pid` is declared as a C# `long` (8 bytes) instead
  of the real native `uint`/DWORD (4 bytes) - harmless there since it only
  *reads* a struct a native callee already wrote (over-allocation + zero
  extension on a little-endian CPU happens to produce the right number),
  but would have been a real buffer-overrun risk here, where *we're* the
  one Explorer calls into and must fill exactly the 20-byte buffer the
  native caller actually allocated. Fixed: `pid` is `uint` in this project.
  `GetValue`/`SetValue` use `object` marshaled as `UnmanagedType.Struct`
  rather than a hand-built `PROPVARIANT` - the CLR's default marshaler
  converts a managed string into a native `VARIANT` (`VT_BSTR`), which is
  binary-compatible with `PROPVARIANT` for that case - the standard
  technique for implementing `IPropertyStore` in managed code.
- `SwPropertyStore.cs` - the class itself, CLSID
  `{E558E17D-51E7-4043-89D8-5EDB8498454F}` (generated once, permanent, same
  treatment as the schema FMTID). `Initialize` opens the SWDM document
  *once* and reads `fields.json` *once*, both cached on the instance -
  `GetCount`/`GetAt`/`GetValue` reuse that rather than re-opening the file
  or re-reading the config per call (a deliberate first-pass performance
  choice, not yet measured under load - see below). `GetValue` calls
  `GetCustomPropertyValues` for the resolved value (not raw
  `GetCustomProperty`, which only returns the unresolved formula-link
  string for linked properties like Material). Every path that can be
  reached from native code is wrapped so a failure degrades to a blank
  value, never a thrown exception - `Initialize` itself never fails either,
  so a file that can't be opened just means every subsequent `GetValue`
  returns blank rather than the whole handler reporting unusable.
  License key is in `LicenseKey.cs`, gitignored like `SwDmLicenseKey.md`
  (added to `.gitignore` in the same commit) - baked in as a compiled
  constant, per item 4's resolution, not read from an environment variable.
- Registered via `regasm.exe /codebase` (the x64 Framework one -
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe`), run
  elevated (`Start-Process -Verb RunAs`, same lesson as Phase 2 about
  `ShellExecute` vs. plain `CreateProcess`).

**`SwPropertyHandlerTest/`** - throwaway-style console harness, same
pattern as `SwFilterDump`'s `TestPropertyHandler`: `Type.GetTypeFromCLSID`
+ `Activator.CreateInstance` to force genuine COM activation (through
`mscoree.dll`'s CLR hosting, CCW/RCW boundary included) rather than any
same-process shortcut, then exercises `IInitializeWithFile`/`IPropertyStore`
directly.

**Tests passed:**
- *Step 3a - proof the hosting mechanism works at all*, before any real
  logic: a hardcoded single property round-tripped correctly through real
  COM activation (`fmtid`/`pid` both correct, confirming the `PROPERTYKEY`
  fix is right; string value returned and read back correctly, confirming
  the `object`/`Struct` marshaling trick works).
- *Step 3b - real logic*: wired up `fields.json` + SWDM. Ran against the
  real test part (`220-320612 WalkAir_WheelAxle.SLDPRT`) - **all 18 tracked
  fields returned correct, resolved values**, matching or improving on
  every previously-known data point (`Material = '10B21'` and
  `Weight = '22.77'` - the *resolved* values, not the raw
  `"SW-Material@..."`/`"SW-Mass@..."` formula strings `ReadSwProperties`
  shows via the non-resolving API).

**Deferred to a future session, from Goren's work computer (not this
machine):** the remaining Phase 3 risk tests still open from the plan -
test fixture set (item 9: an `.sldasm`, a `.slddrw`, a part with zero
custom properties - this machine's repo only has the one test part), a
locked/corrupt/inaccessible file returning blank instead of throwing, a
file open in a running SolidWorks session (item 5), concurrency/
thread-safety under multiple simultaneous calls (item 6), and a timed
batch to check the per-file performance budget. None of these are solved
yet - the core implementation works, but Phase 3 isn't fully closed out
until these run.

### Phase 4 - full integration (registration + real repoint)
Glue Phase 2's Apply tool to Phase 3's real handler CLSID instead of a
placeholder, on a non-production machine/VM first. Any handler rebuild
during this phase's iteration requires restarting `explorer.exe` before the
updated DLL can be deployed (item 7 above) - expected and disruptive
(closes open Explorer windows), not a bug.
- **Tests to pass before trusting this on a real machine:** end-to-end -
  Apply repoints to the real handler, the tracked fields become selectable
  via Explorer's "Choose Columns", and values shown match known-good SWDM
  output across the full fixture set; SolidWorks's own UI (File Properties
  dialog, normal open/save) still behaves normally afterward (regression
  check against the "might break something we're unaware of" risk raised
  earlier); full uninstall/revert tested end-to-end (handler unregistered,
  schema unregistered, `PropertyHandlers` restored to the hardcoded
  original CLSID, Explorer back to the original 3-field behavior).

### Ongoing - testing discipline and maintenance
All of the above happens on a non-production machine/VM with SolidWorks
installed, not the primary machine, until Phase 4's full integration tests
pass there first. Separately, ongoing once deployed for real: re-run Apply
after any SolidWorks update/repair to confirm `PropertyHandlers` still
points at our handler rather than having silently reverted to SolidWorks's
own (item 8 above).

## How Goren likes to work (carry this forward)
- Programming beginner-ish; mainly does .NET SolidWorks and Excel add-ins, a
  little Node.
- Self-described weak spots: QA/testing, state management, UI.
- Wants small, verifiable increments - not large code dumps in one go.
- Wants the approach discussed and agreed on, at both the micro (this step)
  and macro (overall plan) level, before diving into code edits.
- Wants to learn and improve, not have the AI do all the work.

## Sources referenced during earlier research
- https://www.javelin-tech.com/blog/2015/06/showing-descriptions-solidworks-files-windows-explorer-video/
- https://www.eng-tips.com/threads/custom-property-shown-in-windows-explorer.213220/
- https://learn.microsoft.com/en-us/windows/win32/stg/the-documentsummaryinformation-and-userdefined-property-sets
- https://learn.microsoft.com/en-us/windows/win32/properties/props-system-filedescription
- https://www.codestack.net/solidworks-document-manager-api/getting-started/create-connection/
- https://www.codestack.net/solidworks-document-manager-api/document/data-storage/custom-properties/read-all-properties/
- https://cadplus.xarial.com/properties/stand-alone/

## Sources referenced while chasing the "which DLL populates Description/
## LastSavedWith/OpenTime" question (see "SOLVED" above - answer found)
- https://learn.microsoft.com/en-us/windows/win32/search/-search-ifilter-property-filtering
  (official: only 2 ways a filter-only component can expose a property)
- https://learn.microsoft.com/en-us/windows/win32/search/-search-ifilter-implementations
- https://learn.microsoft.com/en-us/windows/win32/api/propsys/ne-propsys-getpropertystoreflags
  (`GPS_BESTEFFORT`)
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitem2-getpropertystore
- https://learn.microsoft.com/en-us/dotnet/api/shell32.folderitem2.extendedproperty
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellfolder2-getdetailsex
- https://learn.microsoft.com/en-us/windows/win32/api/propsys/nf-propsys-pscreatepropertystorefromobject
- https://github.com/reactos/reactos/blob/master/dll/win32/shell32/folders/CFSFolder.cpp
  (`CFSFolder::GetDetailsEx`)
- https://github.com/reactos/reactos/blob/master/dll/win32/shell32/shlfolder.cpp
  (`SH32_GetDetailsOfPKeyAsVariant`, `MapSCIDToShell32FsColumn`)
- https://github.com/reactos/reactos/blob/master/dll/win32/shell32/CFolderItems.cpp
  (`CFolderItem::ExtendedProperty`, `GetExtendedProperty`)
- https://cadplus.xarial.com/properties/stand-alone/
- Multiple SolidWorks forum threads confirming the "two Description columns,
  only one populated" quirk is publicly known (search "SolidWorks Description
  Windows Explorer column" on forum.solidworks.com) - none explain the
  mechanism, only the workaround.
