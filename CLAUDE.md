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
  - **Noted for the record (session 4), not currently acted on:** the
    official SOLIDWORKS API documentation for `IModelDoc2.SummaryInfo`
    (full SolidWorks API, not SWDM - dated 2019) states file summary
    information "is written as an OLE property set into a stream named
    '\005Summary Information' off the root storage of the SOLIDWORKS
    document's compound file" - i.e. claims the file *is* OLE compound
    storage, contradicting the direct byte-level check above. Read as
    likely stale documentation describing an older SolidWorks file format
    generation (the "Remarks" text references MFC/`DRAWCLI`/`CSummInfo`,
    old Visual C++ sample code) rather than evidence the direct check is
    wrong - the byte-level signature mismatch and `StgOpenStorage` failure
    are repeatable, direct observations of the actual current file, not a
    secondhand claim. Not re-verified further this session; flagged here so
    it isn't missed if it resurfaces or turns out to matter later (e.g. if
    an older SolidWorks version's files genuinely are OLE compound storage
    and this project ever needs to handle both generations).
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
- ~~`FieldListEditor/`~~ - **removed (session 5), superseded by
  `SwColumnManager/`** below. Was Phase 1's unprivileged WinForms list
  editor (Add/Remove) for `fields.json`. Kept here as a record: built, run,
  verified interactively; own subfolder for the same file-globbing reason
  as `SwFilterDump`.
- ~~`SchemaApplyTool/`~~ - **removed (session 5), superseded by
  `SwColumnManager/`** below. Was Phase 2's elevated WinForms tool
  (`app.manifest` requires administrator) with per-step buttons (Apply
  Schema, Test Repoint, Revert, Repoint to Real Handler, Full Uninstall).
  Schema add/remove/re-add was confirmed via real
  `List-ExplorerColumns.ps1` column-count changes, and the full
  repoint/revert cycle confirmed live against this machine's real registry
  (see Phase 2 below for detail) - that verification work stays valid, only
  the tool itself was consolidated away. Confirmed during this project:
  must launch an elevation-requiring exe via `ShellExecute` (e.g.
  `Start-Process -Verb RunAs`), not a plain `CreateProcess` call, or
  elevation silently fails with `ERROR_ELEVATION_REQUIRED` and the exe
  never starts - still true for `SwColumnManager`'s self-elevating relaunch.
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
  Also has `--batch <folder>` and `--concurrent <folder>` modes (see Phase 3
  below) - dev/test only, not part of the shipped tool.
- `SwColumnManager/` - **the shippable tool** (session 5), consolidating
  `FieldListEditor` and `SchemaApplyTool` into one exe. Runs unprivileged by
  default (`app.manifest` requests `asInvoker`), showing the same field
  list editor as before; its "Apply Changes" and "Uninstall" buttons
  re-launch the same exe elevated (`Verb="runas"`, a `--apply`/
  `--uninstall` command-line flag) rather than requiring elevation just to
  open the editor - the elevated relaunch is plain console output
  (`AllocConsole` + `Console.WriteLine`), not a WinForms window: this exe
  stays `WinExe` so the everyday editor launch never flashes a console, but
  the elevated action attaches one on demand. **Revised mid-session** from
  an initial `ElevatedActionForm` (a WinForms log window with a `Timer`
  that auto-closed on success, stayed open on failure) - Goren's call that
  a console window is a better fit for a short, scripted admin action: it
  closes naturally the moment `Main` returns, no "did it succeed, should I
  auto-close" logic to maintain at all - only pausing with
  `Console.ReadKey()` on an actual `ERROR`-prefixed failure so there's
  something to read. A bare non-empty stderr still isn't treated as
  failure either way, since `regasm /codebase` routinely warns there on a
  non-strong-named assembly like ours - that warning is benign, not a sign
  anything failed (this caused the WinForms version to never auto-close
  until that check was narrowed to the explicit `ERROR` prefix only).
  `InstallActions.cs` consolidates what used to be 3 separate manual steps
  (hand-run `regasm`, click Apply Schema, click Repoint to Real Handler)
  into one idempotent `Apply()`. Carries `ColumnLookup.cs` (Issue 2's
  `IShellFolder2` interop) and a `ProjectReference` to `SwPropertyHandler`
  purely so MSBuild copies the handler DLL (and its `SolidWorks.Interop.
  swdocumentmgr.dll` dependency) into this project's own output folder -
  "Apply" then copies that bundled DLL to
  `%ProgramFiles%\SwFileExplorerCustomColumns\SwPropertyHandler.dll` and
  registers *that* path via `regasm /codebase`, not a dev-repo path.
  **Built, tested end-to-end**: Apply confirmed via registry (`CodeBase`
  correctly points at the Program Files path, not this repo) and live
  Explorer property resolution; Uninstall confirmed fully clean (DLL
  removed, CLSID fully unregistered, `PropertyHandlers` reverted, schema
  file deleted and its `PropertySchema` registry entry gone).
  **UX pass (session 5):** field list starts genuinely empty with no
  `fields.json` present (already correct - confirmed by clearing the real
  file, dev-test leftovers, not a code gap); the textbox shows a native
  placeholder ("Insert property name" via `EM_SETCUEBANNER`, not a fake
  always-visible label); the status label explaining the known-columns
  cache refresh was removed entirely - meaningless jargon to someone who
  doesn't already know how the tool works internally. The cache refresh
  itself is unchanged, just silent now (best-effort, no user-facing text
  either way).
- ~~`220-320612 WalkAir_WheelAxle.SLDPRT`~~ - **removed from the repo
  (session 5)** - the real-company test part this entire research log
  references throughout (custom properties, resolved values, etc. all stay
  accurate as history) was a real file with real company/part data, not
  something that belongs committed to what's now a shippable tool's repo.
  Testing going forward uses Goren's local fixture folder instead
  (`E:\for testing\A-EYE 2020\`, not part of this repo).
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

**Correction (session 4): this machine is Goren's non-production machine,
not "the primary machine"** - the plan's "test on a non-production
machine/VM first" requirement is satisfied by working here, full stop, not
a substitute or a risk judgment call as originally written above. Goren's
actual production environment is referred to as "the work computer" -
separate hardware, not yet touched by anything in this project. Everything
through Phase 4 is being built and fully verified on this machine; only
after that passes does any of it move to the work computer. The explicit
confirmation-before-each-risky-step discipline and the pre-established
baseline/hardcoded-revert safety net described above are still accurate
and worth keeping regardless of which machine this is - just not because a
VM was unavailable.

### Phase 3 - the property handler itself - fully done and tested, including all risk tests
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
directly. Also gained `--batch <folder>` (times a fresh COM activation +
full property read per file across every SolidWorks file in a folder) and
`--concurrent <folder>` (same, but via `Parallel.ForEach` - one thread per
file) modes, added to close out Phase 3's deferred performance/concurrency
tests. **One real gotcha hit while using this harness, not a product bug:**
since it statically references `SwPropertyHandler` (for the interface
casts), the CLR loads that referenced copy into the process first; if only
the library project gets rebuilt and not this harness, COM activation
reuses the harness's stale already-loaded copy instead of the freshly
built one at the registry's `CodeBase` path, silently testing old code.
Explorer has no such static reference, so this is specific to testing via
a harness built this way - always rebuild both projects together.

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

**Update (later in session 4): all deferred risk tests run for real, not on
the work computer but on this machine, against a real-world fixture folder**
(`E:\for testing\A-EYE 2020\` - 35 real SolidWorks files Goren provided,
including one deliberately open in a live SolidWorks 2020 session
("sensor plate.SLDPRT"), one read-only on disk ("sensor tube
26.9x2.3.SLDPRT"), and a part created with zero custom properties). Original
plan was to defer these to the work computer; superseded once this fixture
folder became available here.
- **Fixture set (item 9) - closed.** Ran `SwPropertyHandlerTest` against
  an assembly (`A-EYE sensor mounting assem.SLDASM`), a drawing
  (`Arm assem.SLDDRW`), and the zero-properties part - all succeeded
  cleanly (`GetCount` and every `GetValue` call returned without error;
  the zero-properties part returned blank for every tracked field and
  legacy `Description`, as expected, while `LastSavedWith` still correctly
  resolved to `'SOLIDWORKS 2020'` - the version table confirmed working for
  a second SolidWorks version now, not just 2019).
- **File open in a running SolidWorks session (item 5) - closed,
  confirmed rather than assumed.** Ran against `sensor plate.SLDPRT` while
  it was genuinely open in SolidWorks 2020 (its `~$sensor plate.SLDPRT`
  lock file present) - succeeded with correct resolved values
  (`Material = 'AISI 316 Stainless Steel Sheet (SS)'`, etc.). SWDM's
  read-only open mode does coexist with an active SolidWorks session, as
  hoped.
- **Read-only file - closed.** Ran against the read-only
  `sensor tube 26.9x2.3.SLDPRT` - succeeded with correct resolved values.
  (A genuinely *corrupt* file wasn't available to test against, so that
  specific sub-case rests on the existing defensive try/catch around
  `TryOpenDocument`/`GetValue` rather than being directly exercised - the
  code path is the same one already proven for the zero-properties and
  other cases, just not fired by an actually-malformed file.)
- **Performance - closed.** Added a `--batch <folder>` mode to
  `SwPropertyHandlerTest` that times a fresh COM activation + `Initialize`
  + every `GetValue` call per file (matching how Explorer actually drives a
  property handler - a new instance per item), run across all 35 real
  files: **523 ms total, 14 ms/file average**, zero failures. Well within
  an acceptable per-file budget for a folder view.
- **Concurrency/thread-safety (item 6) - closed.** Added a
  `--concurrent <folder>` mode (`Parallel.ForEach` - one thread per file,
  each creating its own COM object independently): all 35 files succeeded
  with **zero failures**, no deadlocks, no corrupted results. The handler
  and the underlying SWDM calls are safe under genuine concurrent
  multi-threaded access, at this real-world scale.

**Phase 3 is now fully closed out** - every test in the original plan has
run and passed, none remain deferred.

### Phase 4 - full integration (registration + real repoint) - done, fully tested
**Correction carried over from this session: this machine is Goren's actual
non-production test machine** (see the correction note at the end of Phase
2) - "work computer" is the separate, untouched production machine. Phase 4
was built and fully tested here for real, not deferred.

**Two design issues raised and resolved before testing the live repoint:**

1. **What happens to Description/OpenTime/LastSavedWith (and more - see
   below) once our handler fully replaces SolidWorks's own?** Initially
   proposed blocking these names in `FieldListEditor` with a message box.
   Better fix, implemented instead: `SwPropertyHandler` now directly serves
   these under SolidWorks's own original PROPERTYKEYs (not duplicated under
   our schema), so they keep working seamlessly after repoint instead of
   going blank. Turned out to be a bigger set than first thought - the
   original `CSolidworkPropertyStore` reported **9** properties, not 3
   (confirmed by re-reading the earlier baseline dump): the 3 Explorer
   columns (`Description`/`OpenTime`/`LastSavedWith`) plus 6 generic
   Windows Summary-tab properties (`System.Title`/`Author`/`Subject`/
   `Comment`/`Keywords`/`Rating`). Implemented 8 of the 9 in
   `SwPropertyHandler/LegacyProperties.cs`:
   - `Description` -> `GetCustomPropertyValues("Description", ...)`,
     `OpenTime` -> `GetFileAvgTime()`, `LastSavedWith` -> `GetVersion()` +
     the version table (all already proven in Phase 3's core work).
   - `Title`/`Subject`/`Author`/`Keywords`/`Comment` -> direct
     `ISwDMDocument.Title`/`.Subject`/`.Author`/`.Keywords`/`.Comments`.
     **Checked an assumption before relying on it**: these 5 share the
     classic `SummaryInformation` FMTID/PIDs (`PIDSI_TITLE=2` etc.), which
     raised the question of whether SolidWorks actually reads a literal
     embedded OLE property-set stream for them (the official
     `IModelDoc2.SummaryInfo` API docs, dated 2019, claim exactly that -
     logged under "Background" above as a discrepancy worth remembering,
     but judged likely stale documentation given the direct byte-level
     proof earlier in this file that current SolidWorks files aren't OLE
     compound storage at all). Since the file can't be read that way,
     `CSolidworkPropertyStore` must source these from its own internal
     Summary-tab data instead - the same data `ISwDMDocument`'s accessors
     already expose (confirmed by matching blank values on the test file) -
     so that's what `SwPropertyHandler` uses too; there's no separate
     native mechanism to replicate.
   - `Rating` excluded - no SWDM equivalent exists, stays unserved (also
     always blank under the original handler).
   - **`FieldListEditor`'s reserved-name list grew accordingly.** First
     pass only blocked `Description`/`OpenTime`/`LastSavedWith`. Caught
     mid-session: the 5 Summary-tab ones needed blocking too, for a
     different reason than `Description` - they don't collide by reading
     the *same* data (a tracked field named "Author" would read the
     *custom property* "Author", a different store than the Summary tab's
     Author field), but they'd still produce a second, confusingly-labeled
     column, which is the actual problem worth preventing. Final reserved
     list: `Description`, `OpenTime`, `LastSavedWith`, `Title`, `Subject`,
     `Author`, `Authors`, `Comment`, `Comments`, `Keywords`, `Tags` (label
     variants included since that's what a user is likely to type).
     **`Description` and `Subject` had to be removed from the real,
     already-seeded `fields.json`** since both were already tracked from
     the original 18-property seed, predating this decision.
2. **Can a tracked field reuse an already-existing Explorer column instead
   of always minting a new `SwSync.*` one** (e.g. mapping a custom property
   called "Creators" onto the generic, already-labeled `System.Author`
   column)? **Yes in principle** - schema registration (what makes a
   property selectable, with a label) and value-serving (which handler
   answers `GetValue` for it) are fully decoupled in the Windows Property
   System, so our handler could serve *any* already-registered PKEY it
   wants, the same way `CSolidworkPropertyStore` itself reused the generic
   Summary-tab PKEYs rather than registering its own. **Marked for later,
   not built**: doing this properly means `fields.json` needs to carry
   *which* PKEY to target per field (reuse an existing one vs. assign a new
   one under our schema), and both `FieldListEditor` and `SwPropertyStore`
   need to understand that distinction - a real, contained scope addition,
   not something to fold into Phase 4's testing.

**`SchemaApplyTool` gained a fourth action**: "Repoint to Real Handler
(Phase 4)" - writes `SwPropertyHandler`'s real CLSID
(`{E558E17D-51E7-4043-89D8-5EDB8498454F}`) to `PropertyHandlers` for all
three extensions, deliberately separate from the existing placeholder-based
"Test Repoint" button used in Phase 2.

**Tests passed, live, on this machine:**
- Repointed to the real handler; registry confirmed showing our CLSID on
  all three extensions.
- Live Explorer property resolution (the same `Shell.Application`
  `ExtendedProperty` check used in Phase 2) confirmed: `Description`/
  `OpenTime`/`LastSavedWith` still resolve correctly (now served by *our*
  handler, not SolidWorks's - zero regression), and new tracked fields
  (`Material`, `Weight`, `Number`) resolve with correct, resolved values.
- Column count: `325` (baseline) + `16` (current tracked fields, after
  removing `Description`/`Subject`) = `341`, confirmed via
  `List-ExplorerColumns.ps1` - matches exactly, confirming the schema
  regeneration dropped the right entries.
- SolidWorks's own UI checked directly (not assumed): opened normally,
  File Properties dialog normal, confirming `PropertyHandlers` is purely an
  Explorer/Shell-level mechanism with no effect on SolidWorks's own
  internal UI.
- **Full revert tested end-to-end, completely clean:** clicked "Revert
  PropertyHandlers" (registry back to SolidWorks's original CLSID on all
  three extensions), then additionally ran `PSUnregisterPropertySchema`
  against our `.propdesc` and `regasm /unregister` against the handler DLL
  (via a one-off elevated script - **not yet a permanent button in
  `SchemaApplyTool`**, worth adding later) - both succeeded (`0x00000000`
  and "Types un-registered successfully"). Confirmed afterward: our CLSID
  is completely gone from the registry (`reg query` returns "not found");
  `OpenTime` reads back as `'0:01'` - SolidWorks's own original format, not
  our `'0 mins 01 secs'`, unambiguous proof the original handler is serving
  it again, not ours; column count back to exactly `325`, zero `SwSync.*`
  entries remaining - the exact pre-project baseline.

### Ongoing - testing discipline and maintenance
Phases 0-4 are now fully built and tested on this machine (Goren's
non-production test machine), including every risk test from Phase 3's
original plan (none remain deferred - see Phase 3 above). Nothing has
touched the work computer (the actual production target) yet - that's the
next step, moving the now fully-verified setup there. Separately, ongoing
once deployed for real anywhere: re-run Apply after any SolidWorks
update/repair to confirm `PropertyHandlers` still points at our handler
rather than having silently reverted to SolidWorks's own (item 8 above).

### Issue 2 (Phase 4) - done, tested live in Explorer
Resolved as **automatic matching**, not manual per-field configuration -
Goren's correction mid-build: "the property handler should serve by default
properties that match existing columns; the list editor is just for fields
that don't exist in the native full list." This generalizes what
`Description` already was a special case of (a real custom property whose
name SolidWorks happened to also register a label for) to *any* custom
property name, for any file type.

**How it works:**
1. **`ColumnLookup.cs`** (in `FieldListEditor` only - deliberately kept out
   of `SwPropertyHandler`, see below) drives `IShellFolder2::MapColumnToSCID`
   directly to get the real PROPERTYKEY for every one of Explorer's ~325
   named columns. Checked first whether the registry alone could answer
   this (`PropertySchema` key only lists third-party registrations - Office,
   SolidWorks - not Microsoft's ~250+ built-in core properties, which are
   compiled into `propsys.dll` with no registry trail) - confirmed it
   can't, so the live API call is necessary.
   **First version crashed** (`AccessViolationException` in
   `MapColumnToSCID`) from a `PROPERTYKEY.pid` type mismatch inherited by
   copy-paste reasoning, not an actual bug in the declared interface order -
   fixed by flattening `IShellFolder`+`IShellFolder2` into one interface
   (not relying on C# interface inheritance to chain the native vtable) and
   reducing every unused method's parameters to plain `IntPtr`, keeping
   only `BindToObject` and `MapColumnToSCID` precisely marshaled. Verified
   correct against every already-known PKEY (`Authors`, `Title`, `Subject`,
   `Comments`, `Tags`, `Description`, `SW Open Time`, `SW Last saved with`)
   before trusting it further - zero failures across all 325 columns.
2. `FieldListEditor` regenerates `knownColumns.json` (name -> `{fmtid,pid}`
   for all ~325 columns) automatically on every launch (cheap - well under
   a second) rather than needing a manual refresh button.
3. **`SwPropertyStore`'s `Initialize` now also calls
   `GetCustomPropertyNames()`** to discover what custom properties *this
   specific file* actually has (not just a fixed, predetermined list). For
   each one (skipping the 8 names `LegacyProperties` already owns): if it
   matches an entry in `knownColumns.json`, serve it under that *existing*
   PROPERTYKEY; otherwise, if it's in `fields.json`, serve it under our own
   schema's assigned PID. `fields.json` only ever needs entries for
   properties with **no existing match** - everything else is automatic.
   The risky `IShellFolder2` COM interop stays confined to
   `FieldListEditor`: a crash there is an annoying standalone-tool crash,
   never an `explorer.exe` one.
4. **`LegacyProperties.ReservedNames` had to widen** from the original 3 to
   all 8 (adding `Title`/`Subject`/`Author`/`Authors`/`Comment`/`Comments`/
   `Keywords`/`Tags`) - a file with a literal custom property named "Title"
   or "Subject" would otherwise auto-match to the *exact same PROPERTYKEY*
   `LegacyProperties` already serves via the Summary tab, a real
   double-serving collision, not just a label collision.

**Cleaned up the real `fields.json`** once this was wired up: of the 16
tracked fields at the time, **11 already had native column matches**
(`Color`, `Company`, `Categories`, `Manufacturer`, `Model`, `Owner`,
`Classification`, `Project`, `Language`, `Priority`, `Status`) and were
removed as redundant - only `Number`, `Material`, `Thickness`, `Category`,
`Weight` have no existing match and still need an explicit entry.

**Tests passed:**
- Direct harness: all three sources (new-column, auto-matched, legacy)
  resolve correctly together - `GetCount()` now varies per file (13-15
  across the 35-file fixture, down from a fixed 24) since auto-match is
  file-specific, confirmed with zero failures in both the batch (11ms/file
  average) and concurrent (zero failures) runs.
- Live, via `Shell.Application.ExtendedProperty`: a new column
  (`SwSync.Material`), an auto-matched one (`System.Company`, now showing
  *our* data under a pre-existing native PKEY), and a legacy one
  (`Solidworks.Document.Description`) all confirmed correct after a real
  repoint. Column count exactly `325 + 5 = 330` - confirms auto-matched
  fields correctly needed no new schema registration at all.
- **Confirmed visually in a real Explorer window** (not just scripted
  queries) against the `E:\for testing\A-EYE 2020` fixture: new columns,
  auto-matched columns, and legacy columns all populated correctly across
  varied real files (including the zero-properties part showing blank
  everywhere, and an assembly/drawing both working); sorting by clicking a
  new and an auto-matched column header both worked; no duplicate/unexpected
  entries in the column chooser.

### Known gaps / follow-ups not yet built
- **Deployment to the work computer** hasn't happened yet - everything
  above is verified on the non-production test machine only.
- **Merging everything into one shippable tool** - currently five separate
  projects (`FieldListEditor`, `SchemaApplyTool`, `SwPropertyHandler`,
  `SwPropertyHandlerTest`, `SwFilterDump`) plus manual multi-step setup
  (register the handler, run Apply Schema, repoint) - not yet packaged as
  something a non-technical user (or future Goren) could install in one
  step. Next thing to tackle.

### `SchemaApplyTool` full-uninstall button - done, tested
Was a known gap (the Phase 4 revert test's schema/COM unregistration steps
were run via a one-off elevated script, not the GUI tool). Added a
"Full Uninstall" button: reverts `PropertyHandlers` (reuses the existing
Revert logic), unregisters and deletes our `.propdesc`, and runs
`regasm /unregister` against the handler DLL (shelled out directly via
`Process.Start` - no separate elevation prompt needed since this tool is
already elevated via its own manifest). Tested against a real
"needs cleaning up" state (schema file present, CLSID registered from the
earlier deferred-test re-registration) - confirmed afterward: schema file
deleted, CLSID completely gone from the registry, `PropertyHandlers` back
to SolidWorks's original CLSID.

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
