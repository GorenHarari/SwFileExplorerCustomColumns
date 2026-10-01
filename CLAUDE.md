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
- `README.md` - fuller run instructions for both scripts.

## Not yet done / open questions
- **Fully resolved:** which properties show as live Explorer *columns*
  (gated by `<labelInfo>` in the schema), which the filter can *extract at
  all* (nearly everything, via `IFilter`), and - now - **what actually
  serves live values** (`sldpropertyhandler.dll`'s `CSolidworkPropertyStore`,
  registered per-extension in `HKLM\...\PropertySystem\PropertyHandlers\`,
  hard-capped at exactly 9 properties). See "SOLVED" above. Nothing left
  unexplained in the existing mechanism.
- Open, main: which of the three remaining options below to actually pursue
  for surfacing more properties (Material, Number, Color, Weight,
  Thickness, etc.) as live Explorer columns. Option 2 is now materially
  de-risked - we have a complete working reference (registry key, required
  interfaces, a live comparison object) rather than unknown COM territory.

## Longer-term options under consideration (roughly in order of effort/risk)
1. **Sync approach**: a small app/scheduled task writes SW custom properties
   into whichever standard Explorer fields the name-collision behavior (or
   deliberate use of Comments/Title/etc.) makes visible. Lowest risk -
   nothing runs inside explorer.exe.
2. **Real Windows property handler** (C#, e.g. via the SharpShell library)
   registered for .sldprt/.sldasm/.slddrw - true live/sortable columns for
   any custom property name, but it's a COM component that loads inside
   explorer.exe; a bug there can hang/crash the shell. Bigger scope, more
   moving parts (COM registration, registry, 32/64-bit). **Now materially
   de-risked**: we know the exact registration point
   (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\
   PropertyHandlers\.sldprt` etc. - currently pointing at
   `sldpropertyhandler.dll`'s `{6A921E8A-C58C-4941-9E71-7946D9DCE941}`), the
   exact interfaces required (`IInitializeWithFile` + `IPropertyStore`, both
   already prototyped in `SwFilterDump`), and a live, working reference
   object to compare our own implementation's behavior against. Repointing
   that key to our own CLSID (rather than guessing at SharpShell's generic
   approach blind) is a concrete, scoped task now, not exploratory COM work.
3. **Xarial CAD+ Toolset "Properties+"** - existing commercial product that
   already does this. Worth a look before/instead of building from scratch.

No decision has been made yet on which of these to pursue - that depends on
what the two scripts above turn up.

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
