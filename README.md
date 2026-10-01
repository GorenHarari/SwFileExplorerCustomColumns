# SwFileExplorerCustomColumns

Get SolidWorks (`.sldprt` / `.sldasm` / `.slddrw`) custom properties to show
up as real, sortable, filterable columns in Windows Explorer - including
properties Windows Explorer has no built-in column for at all (Material,
Thickness, or any other company-specific custom property).

SolidWorks ships its own Explorer integration, but it only ever serves 3
columns (Description, Last Saved With, Open Time) out of the box. This
project replaces that integration with one that serves everything you
actually track, while keeping those 3 (and a few other built-in ones)
working exactly as before.

## Requirements

- Windows with SolidWorks installed (any recent version - the `Solid
  `SolidWorks.Interop.swdocumentmgr.dll` the handler depends on ships with
  every SolidWorks install).
- .NET Framework 4.8 (already present on current Windows) and the .NET SDK
  to build.
- A SolidWorks **Document Manager API** license key - separate from a
  normal SolidWorks license, requested from the SOLIDWORKS Customer Portal
  against your serial number (ask your reseller/VAR if you can't find the
  request form). Needed to build `SwPropertyHandler` - see below.

## Building

```powershell
dotnet build SwColumnManager -c Release
```

This also builds `SwPropertyHandler` (a project reference) and copies its
DLL, plus its `SolidWorks.Interop.swdocumentmgr.dll` dependency, next to
`SwColumnManager.exe` automatically.

**Before this will build**, create `SwPropertyHandler/LicenseKey.cs` (it's
git-ignored - secret, not part of this repo) with your own key:

```csharp
namespace SwPropertyHandler
{
    internal static class LicenseKey
    {
        public const string Value = "<your Document Manager API license key>";
    }
}
```

If `SolidWorks.Interop.swdocumentmgr.dll` isn't where `SwPropertyHandler
.csproj`'s `HintPath` expects
(`C:\Program Files\Common Files\SOLIDWORKS Shared\...`), fix that path to
wherever your install actually put it.

## Installing / using

1. Run `SwColumnManager.exe`. No elevation needed for this - it just opens
   the field list editor.
2. **Add the property names you want as new columns** - only ones with no
   existing Explorer column already (see "How this tool works" below for
   why most names need no entry at all). Type a name, click **Add**.
3. Click **Apply Changes**. You'll get a UAC prompt - this step registers
   the property handler and points Explorer at it, which needs admin
   rights. A console window flashes briefly with progress and closes on
   its own when done (it stays open if something actually failed).
4. **Open a new Explorer window or tab** (an already-open one won't pick up
   new columns on its own) and add your columns via right-click a column
   header -> "More...".
5. To remove a field later: select it in the list, click **Remove**, then
   **Apply Changes** again to push the change live.
6. **Uninstall** reverts everything - Explorer's columns go back to exactly
   what SolidWorks's own installer set up, as if this tool had never run.

## How this tool works

**One exe, two faces.** `SwColumnManager.exe` runs unprivileged by default
(the field editor). Its "Apply Changes" and "Uninstall" buttons re-launch
the same exe elevated (`Verb=runas`, a hidden `--apply`/`--uninstall` flag)
rather than requiring admin rights just to open the editor - standard
"elevate only when actually needed" behavior. The elevated run shows plain
console output and closes itself.

**You rarely need to add anything.** Most SolidWorks custom properties
already have a matching Explorer column - Windows ships hundreds of generic
ones (Authors, Company, Status, Owner, Priority, Color, and so on). The
property handler checks, for every custom property on every file, whether
its name matches one of those - if it does, it's served under that
existing column automatically, live, no configuration at all. The field
list in `SwColumnManager` is only for properties with **no existing
match** (Material and Thickness are common examples) - those get a
genuinely new column, minted under this tool's own schema.

**What "Apply Changes" actually does, in order:**
1. Installs the property handler DLL to
   `%ProgramFiles%\SwFileExplorerCustomColumns\` and registers it as a COM
   server (`regasm /codebase`).
2. Builds and registers a small property schema (`.propdesc`) covering just
   the tracked fields that need a brand-new column.
3. Points `.sldprt`/`.sldasm`/`.slddrw` at the new handler in the registry
   (`HKLM\...\PropertySystem\PropertyHandlers`), replacing SolidWorks's own
   entry.

**What the property handler itself serves**, for every file Explorer asks
about, in three layers:
- **8 built-in columns**, continuing exactly what SolidWorks's own handler
  always showed (Description, Open Time, Last Saved With, plus 5 generic
  Summary-tab fields) - nothing regresses by switching handlers.
- **Auto-matched columns** - any of *that file's* actual custom properties
  whose name matches an existing Explorer column, resolved automatically.
- **New columns** - whatever's explicitly listed in the field editor.

Every value is read through the **SolidWorks Document Manager API**, a
separate, lightweight, licensed library that reads a file's real data
directly - no need for SolidWorks itself to be running, and it works fine
even if the file is read-only or currently open in a live SolidWorks
session.

**Uninstall** reverses all three Apply steps: the registry key goes back to
SolidWorks's original handler, the custom schema is unregistered and
deleted, and the handler DLL is unregistered and removed.

## SolidWorks's native Explorer-column structure, explained

A fair amount of reverse-engineering (all read-only - registry inspection,
documented public COM interfaces, and binary string-scanning; see
`CLAUDE.md` for the full research log if you want the detailed, warts-and-
all version) went into understanding what SolidWorks already does, so this
tool could replace it correctly rather than guess.

- **Modern SolidWorks files (2019+) are not OLE structured-storage
  files.** The original assumption going in was that they were (like old
  Office documents) - checked directly: the file's first 8 bytes don't
  match the OLE signature, and `StgOpenStorage` fails on it outright.
  Whatever SolidWorks's internal format is now, it's proprietary.
- **The real mechanism is a generic Windows table, not something
  SolidWorks-specific.** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\
  PropertySystem\PropertyHandlers\<extension>` maps a file extension to a
  COM class implementing `IInitializeWithFile` + `IPropertyStore` - the
  same mechanism hundreds of other file types use (`.docx`, `.avi`, `.dng`,
  ...). SolidWorks points `.sldprt`/`.sldasm`/`.slddrw` at
  `sldpropertyhandler.dll`'s `CSolidworkPropertyStore`
  (`{6A921E8A-C58C-4941-9E71-7946D9DCE941}`).
- **That handler is hard-capped at exactly 9 properties**, confirmed by
  driving it directly: `Description`, `OpenTime`, `LastSavedWith` (its own
  3 canonical properties, the only ones with a column label defined) plus
  6 generic Windows ones (`Title`/`Author`/`Subject`/`Comment`/`Keywords`/
  `Rating`). It never reports Material, Weight, or anything else - not a
  bug, just a narrow, fixed list compiled into the DLL.
- **Separately, SolidWorks registers a `.propdesc` schema** defining ~22
  `Solidworks.Document.*` properties (Material, Number, Project, ...),
  all technically queryable - but only 3 of them have the `<labelInfo>`
  element that makes a property selectable as an Explorer column at all,
  which is why the other ~19 are invisible in "Choose Columns" regardless
  of the handler above.
- **The values behind those 9 properties come from different places**:
  `Description` happens to be a literal custom property of that exact name
  (if one exists); `OpenTime`/`LastSavedWith` are computed (average file
  open time; a file-format version code mapped through SolidWorks's own
  published version table); the other 6 come from the file's internal
  Summary tab - a different, independent store from custom properties,
  despite sharing a name-lookalike. None of this is read from a literal
  OLE property-set stream, since (per above) these files aren't OLE
  storage - Windows just reuses the classic `SummaryInformation` FMTID as
  a generic identity, not because that's literally what's being read.
- **Most of Explorer's ~325 generic columns have no registry trail at
  all.** Checked directly: `HKLM\...\PropertySystem\PropertySchema` only
  lists a handful of third-party schema registrations (Office's, three
  SolidWorks versions') - Microsoft's own ~250+ built-in columns (Authors,
  Title, Tags, Company, Status, ...) are compiled into `propsys.dll`
  itself. The only way to discover their real identity is the live
  `IShellFolder2::MapColumnToSCID` API, which is what this tool's
  auto-matching is built on.

## Research, testing, and dev tools

These aren't part of the shipped tool - they're what made building it
possible, kept in the repo for future investigation or debugging rather
than day-to-day use.

- **`List-ExplorerColumns.ps1`** - dumps every named Explorer column this
  PC knows about to `ExplorerColumns.csv` (index + display name), via the
  `Shell.Application` COM object. The simplest possible ground-truth check
  for "does a column with this exact name exist."
- **`ReadSwProperties`** (`Program.cs` / `ReadSwProperties.csproj`, repo
  root) - a small console app reading a SolidWorks file's custom
  properties directly via the Document Manager API, independent of
  Explorer entirely. Needs `SWDM_LICENSE_KEY` set as an environment
  variable (a different approach from `SwPropertyHandler`'s baked-in
  constant - this one predates that decision). Used throughout to confirm
  what a file's custom properties actually are, directly from the source.
- **`SwFilterDump/`** - drives `sldsearchifilter.dll`'s `IFilter` COM
  interface directly (the same low-level mechanism the Windows Search
  crawler uses) to see exactly what SolidWorks's filter can extract, and
  `TestPropertyHandler` mode drives the original `CSolidworkPropertyStore`
  directly via `IInitializeWithFile`/`IPropertyStore` - this is how the
  9-property cap and the resolved-vs-raw-formula distinction (e.g.
  Material resolving to `10B21` instead of the raw linked-formula string)
  were actually discovered, not guessed.
- **`SwPropertyHandlerTest/`** - the equivalent harness for *our own*
  `SwPropertyHandler`, forcing genuine COM activation
  (`Type.GetTypeFromCLSID`) the same way Explorer does. Also has
  `--batch <folder>` and `--concurrent <folder>` modes used to verify
  performance and thread-safety across a real folder of SolidWorks files.

## License

MIT - see `LICENSE`.
