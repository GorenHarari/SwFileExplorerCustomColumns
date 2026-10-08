# SwFileExplorerCustomColumns

## Goal
Get SolidWorks (.sldprt / .sldasm / .slddrw) custom properties to show up as
sortable/filterable columns in Windows Explorer.

## Investigation: explorer.exe perf/reliability (session 10) - 3 real fixes done, tested; 1 root cause found but not fixable by us
Goren had no specific repro, just a general impression that explorer.exe
sometimes crashes/freezes and that SolidWorks-related handles might
accumulate over time. Explored `SwPropertyHandler`'s COM lifecycle and
`SwColumnManager`'s process-recovery code for concrete bugs, found and fixed
three, then measured for a leak directly (OS handle count, not just code
review) and found a fourth, bigger one that we cannot fix.

**Fixed (all three independently real, all verified via `--batch`/
`--concurrent`/`--rename-test`, zero regressions):**
1. `SwDmDocument.cs` - `classFactory`/`app`/`_doc` COM objects were never
   explicitly released (`Marshal.ReleaseComObject`), only `_doc.CloseDoc()`
   was called - left for the GC/finalizer to reclaim inside the long-lived
   explorer.exe/SearchFilterHost.exe host. Also: if `app.GetDocument()`
   returns a document that doesn't cast to `ISwDMDocument23`, the opened
   document was dropped with no `CloseDoc()` at all - the same bug class as
   session 9's fix, in an untested path. Fixed: all three objects are now
   explicit fields, released in `Dispose()`, each independently
   best-effort (one failed release doesn't block the others); the
   cast-miss path now closes/releases before returning `null`.
2. `HandlerConfig.cs` - `fields.json`/`knownColumns.json` were read from disk
   and JSON-deserialized on every single `Initialize` call (i.e. per file
   Explorer activates a property store for). Fixed: both are now cached in
   memory, keyed by last-write-time/existence, re-read only when the file
   actually changes - confirmed via a live test (edited the real
   `fields.json`, confirmed the new field appeared without a process
   restart, then restored the original file exactly).
3. `ExplorerUtil.cs` - `RetryOnLock` kills every `explorer.exe` process and
   assumes Windows auto-relaunches it, with no verification. Fixed: after
   the kill, waits up to 5s and logs a clear warning if explorer.exe didn't
   come back, instead of assuming it did. Tested live on this machine: held
   the handler DLL locked with a synthetic exclusive-read lock, ran a real
   elevated Apply - confirmed `explorer.exe` was genuinely killed and
   relaunched correctly (no false warning), and that exhausting all 4
   retries (since killing explorer/SearchFilterHost doesn't release a lock
   *we* synthetically held) fails cleanly (exit code 1, no crash/hang) rather
   than leaving anything stuck. A second live Apply after the lock cleared
   confirmed the normal success path still works and left the installed
   handler fully functional (confirmed via live `ExtendedProperty` reads:
   `Material = '10B21'`, `Description = 'WalkAir_WheelAxle'`).

**Found, NOT fixable from our code: a real OS-handle leak inside
SolidWorks' own Document Manager (SWDM) native library.** Proved this by
direct measurement (OS handle count via `Process.HandleCount`), not just
code review - bisected which calls leak by looping each in isolation
against a reused `Application`/document:
- Looping raw `Application`/`Document` open-close cycles, or `GetVersion()`
  alone: **flat**, no growth at all.
- Looping *any* of: `Title`/`Subject`/`Author`/`Keywords`/`Comments`,
  `GetFileAvgTime()`, `GetCustomPropertyValues()`, or
  `GetCustomPropertyNames()` - each one **alone** leaks ~6 OS handles per
  call, climbing linearly, indefinitely.
- A forced `GC.Collect()` + `WaitForPendingFinalizers()` after 200 leaking
  iterations recovered **zero** handles - ruling out this being any kind of
  .NET/RCW finalizer-timing issue (which is exactly what fix #1 above
  addresses). The leak is native and immediate, not GC-deferred, so no
  amount of `Marshal.ReleaseComObject` correctness on our side touches it.

Since reading custom properties/summary-info is the entire purpose of this
handler, every file Explorer shows (that this handler reads properties from)
leaks a handful of OS handles inside SolidWorks' own DLL, accumulating for
the lifetime of whatever process hosts the handler (explorer.exe or
SearchFilterHost.exe) until that process restarts. This is a strong, now
directly-measured explanation for "things degrade/freeze the longer Explorer
has been browsing SolidWorks files" - and it is outside our ability to fix,
short of SolidWorks fixing it in a future SWDM release.

**What's actually leaking, by type** - went further than the handle *count*:
wrote a throwaway diagnostic (`NtQuerySystemInformation`/`NtQueryObject`, the
same technique Process Explorer/`handle.exe` use internally, removed again
after use) that snapshots this process's own handle table before/after 30
calls to the leaky `summary` accessor (`Title`/`Subject`/`Author`/`Keywords`/
`Comments`), diffs it, and resolves each new handle's type and name. Result
(348 new, never-closed handles for 30 calls - ~11-12/call, same ballpark as
the earlier ~6/call measurement, consistent, not one single resource type):
`Semaphore` (134), `EtwRegistration` (73, a tracing-provider registration),
`Event` (87), `Key`/registry (24 - named examples:
`\REGISTRY\...\Classes`, `\REGISTRY\MACHINE\SOFTWARE\Classes\PackagedCom\
ProgIdIndex`), `ALPC Port` (9 - `\RPC Control\OLE...`), `File` (8 - two
`\Device\KsecDD`, the crypto device, plus a few `.dll.mui` resource files),
`Section` (5 - `\BaseNamedObjects\__ComCatalogCache__`), `Thread` (4),
`Mutant` (2), `Timer`/`IoCompletion` (1 each).

**Working hypothesis for the mechanism (speculation, no source access -
not confirmed, but consistent with every measurement so far):** the named
examples (`PackagedCom\ProgIdIndex`, `__ComCatalogCache__`, `RPC Control\
OLE...`) are all classic **COM activation machinery** - the registry
class-lookup, catalog cache, and RPC channel Windows sets up on a fresh
`CoCreateInstance`. Combined with the fact that `GetVersion()` (pure
file-header metadata, no property-set access) leaks nothing at all, while
*every* property-content accessor leaks the same rough amount regardless of
which field or how many fields are read in that one call, the likely
mechanism is: each property-content call reopens/re-parses the file's
property-set storage from scratch (itself a COM-based subsystem) and never
closes it, rather than caching it once per `Document` open. The
`EtwRegistration`/crypto-device (`KsecDD`) handles suggest this might be
bundled with a license re-validation that also re-runs per call instead of
once per `Application`. Almost certainly not a deliberate design choice (a
real cache would plateau after first use, not grow linearly forever) -
more likely SWDM was built/tested against SolidWorks's own GUI process,
which opens relatively few documents per session and eventually exits
(letting the OS reclaim everything regardless of what SolidWorks' own
cleanup code does), not against a usage pattern like a Windows property
handler: thousands of calls in a row, inside a host process that never
exits on its own.

**What this means for this tool and Goren's day-to-day experience:**
the three fixes above are real and worth having, but they do not resolve
the originally-reported symptom (explorer.exe degrading/freezing over
time) - that symptom is dominated by this separate mechanism, which no
code change on our side (short of not reading properties at all) can
touch. Where it actually bites:
- **Regular Explorer windows, Details view, with our columns visible** -
  the dangerous path. Each file shown leaks directly into the actual
  `explorer.exe` process, which does not self-recycle - handles accumulate
  for as long as that process has been running (normally since the last
  reboot), growing further every time a SolidWorks-heavy folder is
  browsed, refreshed, or revisited.
- **Windows Search indexing** - runs inside the disposable
  `SearchFilterHost.exe` surrogate instead, which Windows itself recycles
  on an idle timeout, capping the damage from that path for free.
- The tool remains functionally correct (right values, no correctness
  bugs here) - this is purely a "things slow down the longer explorer.exe
  has been alive and the more SolidWorks files it has shown columns for"
  concern.
- The only real lever available is restarting `explorer.exe` occasionally
  (Task Manager) to zero out whatever accumulated - a workaround, not a
  fix. Enabling our columns as always-visible defaults across all folders
  (rather than only where SolidWorks files are actually being worked on)
  would widen exposure, since every folder browse anywhere would start
  leaking, not just the ones actually being looked at for SolidWorks data.

Not investigated further this session (e.g. whether a different SWDM DLL
version avoids it, or reporting it to SolidWorks as a Document Manager API
bug) - Goren's call, deferred rather than pursued.

## Bug: explorer.exe held SolidWorks files locked, blocking rename/delete (session 9) - done, tested, deployed to the work computer
Goren reported (on the work computer, where the handler has been live since
session 8): after editing and saving a part in SolidWorks, Explorer sometimes
wouldn't let him rename or delete it - "file is open in another program."
Using Resource Monitor (CPU tab, Associated Handles, search the filename -
the same technique already used in session 8 to find `SearchFilterHost.exe`)
he confirmed `explorer.exe` itself held the handle, and specifically only on
the file he'd just edited - every other file in the folder was fine. He
didn't know what in Explorer's property-reading path would hold a file open
like that, or why only that one file.

**Root cause, found by reading `SwPropertyStore.cs`/`SwDmDocument.cs`
directly, not guessed:** `Initialize` opened the file via SWDM and cached the
open `ISwDMDocument23` handle (`_doc`) on the instance for its entire
lifetime; the only code that ever closed it (`SwDmDocument.Dispose()` ->
`CloseDoc()`) was called from `SwPropertyStore`'s finalizer
(`~SwPropertyStore`) - nothing else, anywhere, ever called it.
`IInitializeWithFile`/`IPropertyStore` have no "done, release the file"
method - Explorer's only lifecycle control is COM `Release()`, which for a
managed CCW only drops the CLR's last reference, making the object merely
*eligible* for GC. The finalizer (and therefore the file's actual OS-level
release) only runs whenever the CLR hosted inside `explorer.exe` happens to
collect it - non-deterministic, and could take a long time in a long-lived,
low-allocation host process.

This also explains the "only the just-edited file" observation: opening the
folder creates one such handle per visible file, and by the time Goren
touched an *older* file again, `explorer.exe` had likely already run a GC
in between and finalized it. Saving in SolidWorks fires a shell-change
notification for that one item, making Explorer re-query it and create a
**brand-new** `SwPropertyStore`/handle right around the time he tried to
delete it - too young to have survived a GC cycle yet. Every file goes
through the same race; it's just rarely noticed because most files aren't
touched again within the same short window.

**Fix** (`SwPropertyHandler/SwPropertyStore.cs`): resolve every property
value once, eagerly, inside `Initialize` (in a `using` block around the
`SwDmDocument`), then let the `using` block close the SWDM document
synchronously before `Initialize` returns. `GetValue` is now a plain
`Dictionary<PROPERTYKEY, object>` lookup against the already-resolved
values - no SWDM calls, no cached document, nothing left for a finalizer to
do, so the finalizer was removed entirely. This closes the race rather than
narrowing it: the handle is never held open longer than the `Initialize`
call itself, regardless of how long Explorer keeps the COM object alive
afterward or how recently it was created.

**Verification, without any GUI/Resource Monitor automation available in
this environment** - two new dev-only modes added to
`DevTestTools/SwPropertyHandlerTest`:
- `--rename-test <file>`: drives `SwPropertyStore` directly (non-COM, so it
  always exercises the just-built code) through
  `Initialize`/`GetCount`/`GetAt`/every `GetValue`, then - same process, no
  `Dispose`, no forced GC - immediately attempts `File.Move` on the same
  path. **Proved the bug first**: stashed the fix, rebuilt, ran this against
  a scratch copy of a real fixture file - failed with
  `IOException: ... being used by another process`, reproducing Goren's
  exact symptom with no SolidWorks or Explorer involved. Restored the fix,
  rebuilt, re-ran - rename succeeded immediately. This is strictly stronger
  proof than the live GC-dependent race the original bug report hit, since
  it doesn't rely on timing to surface.
- `--batch-direct <folder>`: same as the existing `--batch` but via direct
  (non-COM) instantiation, so it tests the just-built code independent of
  what's currently registered/deployed. Run against all 24 real fixture
  files in `C:\Users\GorenHarari\Desktop\TEST\` on the work computer: 303 ms
  total, 12 ms/file average, zero failures, identical resolved values to
  before the fix - matches session 8's `--batch` baseline (272 ms, 11 ms/
  file) closely enough to call it noise, confirming the eager-resolution
  change (resolving every property up front instead of only the ones
  Explorer happens to ask for) doesn't meaningfully change the per-file
  cost, which is dominated by the SWDM open itself either way.

**Deployed for real on the work computer** (confirmed before starting: this
*is* the work computer, `GorenH-laptop`, SW2019 only, with the handler
already live from session 8 - not a separate test machine). Rebuilt
`SwColumnManager` with the fixed handler bundled, ran its elevated Apply
(`SwColumnManager.exe --apply --pause`, via `Start-Process -Verb RunAs` -
Goren approved the UAC prompt) - succeeded, confirmed via the registry
(`PropertyHandlers\.sldprt` still our CLSID, `InprocServer32`'s `CodeBase`
pointing at the freshly-written
`C:\Program Files\SwFileExplorerCustomColumns\SwPropertyHandler.dll`, new
timestamp) and a live COM-activation property read against a fresh scratch
copy of the test part (same resolved values as always - `Material = '10B21'`
etc.) - all working through the real, deployed, registered path, not just
the dev build.

**Goren confirmed it fixed for real afterward**, going back to the actual
original repro (edit/save a real part in SolidWorks with Explorer open, then
rename/delete it) rather than just the synthetic tests above - no more
lock.

## SW2019 work computer deployment (session 8) - done, tested
Picked up the session-7 handoff (below, now resolved) on the work computer.
**Machine-identity correction made during this session**: the work computer
(hostname `GorenH-laptop`) has **SolidWorks 2019 only** - it is not the
machine with both 2019 and 2020 side by side. That dual-version machine is
Goren's separate **personal computer**, which also holds the current
SWDM license key and the 2020 `swdocumentmgr.dll` needed for eventual
public-release bundling (untouched this session).

- **Step 0 (build)**: `git pull`; `SwPropertyHandler\LicenseKey.cs` already
  present (git-ignored, not regenerated this time). `dotnet build
  SwFileExplorerCustomColumns.sln -c Release` succeeded, 0 warnings/errors.
  Output bundled `swdocumentmgr.dll` v27.5.0.0072 - correct for this
  machine (no 2020 copy exists here to accidentally pick up instead).
- **Step 1 (pre-state)**: `%ProgramFiles%\SwFileExplorerCustomColumns\`
  existed but was **completely empty** - no leftover
  `SolidWorks.Interop.swdocumentmgr.dll`, no handler DLL either.
  `PropertyHandlers\.sldprt`/`.sldasm`/`.slddrw` all showed SolidWorks's
  original `{6A921E8A-C58C-4941-9E71-7946D9DCE941}`. Document Manager CLSID
  (`{00AB5D8D-...}`) registered to SolidWorks's own `SOLIDWORKS
  Shared\swdocumentmgr.dll`.
- **Step 2 (Apply)**: ran `SwColumnManager.exe --pause`, clicked Apply
  Changes, approved UAC. Output: Document Manager already registered
  (SolidWorks's own copy) - left alone; handler copied to Program Files;
  `regasm /codebase` succeeded (with the known benign unsigned-assembly
  warning - see "Known gaps" below, left as-is, not worth strong-naming the
  assembly for); schema written (2 tracked fields) and
  `PSRegisterPropertySchema` returned `0x000401A0` - not `0x00000000`. The
  code only logs this value, never checks it, and no prior session
  documented what a non-zero return here means, so rather than trust it
  blindly, verified the actual outcome instead: `PropertyHandlers`
  repointed to our CLSID on all three extensions, and **live Explorer
  property resolution confirmed correct** (a new tracked field plus legacy
  `Description`/`LastSavedWith`/`OpenTime`) on a real SW2019 file. That's
  the real proof registration worked, regardless of what the HRESULT means.
- **Step 3**: left installed, as instructed for the production machine -
  Uninstall not run.
- **Step 4 (optional)**: skipped - no SW2020-saved fixture files available
  on this machine.

**Still open:**
- The leftover-DLL cleanup path (`DeleteLegacyInteropDll`) remains
  never-exercised - Program Files was already empty here too, same gap as
  session 6/7's testing.
- What `PSRegisterPropertySchema`'s `0x000401A0` return actually means is
  still undocumented - not blocking (live resolution proves success), but
  unexplained.
- Public release bundling (2020 license key + DLL, see "End goal" below)
  lives on Goren's personal computer - not touched this session.

## Code quality pass on the shippable tool (session 8) - done, tested
Separate from the deployment work above, same session: a cleanup/
readability/performance pass over `SwColumnManager` + `SwPropertyHandler`
only (the actual shipped product - `SwFilterDump`/`ReadSwProperties`/
`SwPropertyHandlerTest` are dev/test harnesses, explicitly out of scope).
Done in that fixed order - cleanup first, then readability/separation of
concerns, then performance - with a build (and, for anything touching
`SwPropertyHandler`, a `SwPropertyHandlerTest` run against a real file)
after every single sub-step, not just at the end.

**1. Cleanup (dead code & duplication):**
- Removed `PropertyHandlerRegistry.GetCurrentClsid`/`GetCurrentClsids`
  (confirmed via grep - never called from anywhere, a leftover from the old
  `SchemaApplyTool` era) and `FieldItem.Pid` (set, never read - `FieldItem`
  is a UI-only display wrapper for the list box, unrelated to the real
  name->pid data in `fields.json`).
- Eliminated a byte-for-byte duplicate of the reserved-names list that
  existed in both `SwPropertyHandler/LegacyProperties.cs` and
  `SwColumnManager/MainForm.cs`. Made `LegacyProperties` (and its
  `ReservedNames` field) `public` instead of `internal`; `MainForm` now
  reads `SwPropertyHandler.LegacyProperties.ReservedNames` directly via the
  `ProjectReference` that already existed for DLL-bundling purposes. The
  `AddField` rejection message's hand-typed prose list (which had already
  drifted - missing "Authors"/"Tags") is now generated from the same array
  via `string.Join` instead of a second hand-maintained copy.
- Added `SwColumnManager/FieldsStore.cs` as the single `fields.json`
  load/save implementation, replacing separate reimplementations in
  `MainForm` and `InstallActions`. This fixed a real latent inconsistency:
  `MainForm`'s dictionary used `StringComparer.OrdinalIgnoreCase`,
  `InstallActions`'s didn't - a property name differing only by case could
  dedupe differently depending which code path read the file. Both now go
  through one case-insensitive implementation.

**2. Readability / maintainability / separation of concerns:**
- Extracted `SwColumnManager/KnownColumnsCache.cs` (the `ColumnLookup` ->
  `knownColumns.json` write, previously inline in `MainForm`'s `Shown`
  handler) - pairs naturally with `ColumnLookup.cs`, keeps `MainForm`
  focused on UI/business logic.
- Split `MainForm.cs` into `MainForm.cs` (logic) + `MainForm.Designer.cs`
  (control declarations/layout/event wiring), matching the standard VS
  WinForms partial-class template (including the inert `components`
  `IContainer`/`Dispose` boilerplate every new WinForms project gets, for
  parity even though nothing here currently needs it). Inline lambda event
  handlers became named methods (`AddButton_Click`, `TextBox_KeyDown`,
  `MainForm_Shown`, etc.) wired from `InitializeComponent()`, the idiomatic
  designer-generated pattern.
- Decomposed `SwPropertyHandler/SwPropertyStore.cs` (previously 341 lines
  mixing COM lifecycle, config I/O, SWDM document-opening, and auto-match
  logic) via pure moves into three new files: `HandlerConfig.cs`
  (`LoadFields`/`LoadKnownColumns`), `AutoMatcher.cs`
  (`ComputeAutoMatches`), and a new `SwDmDocument.cs` (see next point).
  `SwPropertyStore` is now a thin shell over the `IInitializeWithFile`/
  `IPropertyStore` contract, delegating to the above.
- **Consolidated all `SolidWorks.Interop.swdocumentmgr` access into one
  class** (Goren's explicit ask, after noticing the interop type was
  referenced from three places): `SwDmDocument` (replacing the
  shorter-lived `SwDmDocumentOpener`) now owns the `ISwDMDocument23`
  handle entirely and exposes a plain C#-typed surface (`string`/`string[]`/
  `int` - `Title`, `Subject`, `Author`, `Keywords`, `Comments`,
  `GetCustomProperty(name)`, `GetCustomPropertyNames()`,
  `GetFileAvgTime()`, `GetVersionCode()`, plus the `TryOpen`/`IDisposable`
  lifecycle). `SwPropertyStore` and `AutoMatcher` now depend only on this
  surface - neither has a `using SolidWorks.Interop.swdocumentmgr;` any
  more. Confirmed via grep: that `using` now appears in exactly one file.
- Replaced `GetValue`'s 8-way `if/else if` chain of
  `LegacyProperties.KeyEquals` checks with a
  `Dictionary<PROPERTYKEY, Func<SwDmDocument, object>>` built once as a
  static field (keyed via a small `IEqualityComparer<PROPERTYKEY>` wrapping
  `LegacyProperties.KeyEquals`, since the struct has no built-in one) -
  same resolution, one lookup instead of up to 8 sequential comparisons.

**3. Performance:** concluded there was nothing to do beyond the dispatch
dictionary above. `SwPropertyStore`'s real cost is the SWDM COM calls,
already measured (Phase 3) at ~11-14ms/file with zero failures under
concurrent load - well within budget, not worth optimizing further without
a concrete reason.

**Verification approach:** every `SwPropertyHandler`-touching sub-step was
built, then run through `SwPropertyHandlerTest` (both the direct,
non-COM-activated path and the real `Type.GetTypeFromCLSID` COM-activation
path) against a real file on this machine
(`C:\Users\GorenHarari\Desktop\TEST\220-320612 WalkAir_WheelAxle.SLDPRT`),
confirming byte-identical output (`GetCount() = 12`, same 12 resolved
values) before moving to the next sub-step - zero regressions at any
point. After the full pass, also ran `--batch` across all 24 real files in
that folder (parts, assemblies, and off-the-shelf fasteners/bearings): 272
ms total, 11 ms/file average, zero failures - matching Phase 3's original
numbers, confirming no performance regression either.
`SwColumnManager`-only sub-steps were smoke-tested live through the real
editor GUI (add/remove fields, persistence across restart, reserved-name
rejection text, window resize/anchoring) rather than via a harness, since
that project has no COM/Explorer risk.

**Committed** (`60a3c50`) and pushed. **Then re-Applied for real** on this
machine (needed restarting `explorer.exe` first - the previous,
pre-refactor handler DLL was still loaded and blocked the overwrite with
"being used by another process", the same class of lock issue already
documented elsewhere in this file for uninstall). After the restart, Apply
succeeded cleanly (same `PSRegisterPropertySchema -> 0x000401A0` as every
other run - still unexplained, still not blocking). **Final live check**
via the real `Shell.Application.ExtendedProperty` path confirmed the
refactored handler is genuinely serving values: `SwSync.Material = '10B21'`,
`SwSync.Weight = '22.77'`, `Solidworks.Document.Description =
'WalkAir_WheelAxle'`, `...LastSavedWith = 'SOLIDWORKS 2019'`,
`...OpenTime = '0 mins 01 secs'` (our format, not SolidWorks's own
`'0:01'` - proof this handler, not the old one, answered the query).

## Automatic recovery from a locked handler DLL (session 8) - done, tested
Apply/Uninstall had always required a manual workaround documented
elsewhere in this file - restart `explorer.exe`, sometimes stop the Windows
Search service - when the handler DLL (or a bundled `swdocumentmgr.dll`)
was already loaded by something. Goren asked for this to be automatic
instead, since he kept hitting it (every re-Apply or re-Uninstall after the
tool was already installed once). Found the real cause by actually
diagnosing it this time rather than guessing, since two earlier guesses
both turned out wrong once tested:

- **First guess, wrong: just restart `explorer.exe`.** Added
  `ExplorerUtil.Restart` (kill every `explorer.exe` process, let Windows
  relaunch it automatically as the interactive user - never explicitly
  `Process.Start("explorer.exe")` from this already-elevated process, which
  would launch an elevated Explorer instead) and wrapped it around the
  handler-DLL copy (Apply) and delete (Uninstall), plus the bundled
  `swdocumentmgr.dll` delete in `DocumentManagerSetup.RemoveIfOurs`.
  **Caught a real bug while wiring this in**: the delete call sites only
  caught `IOException`, but a live reproduction of the uninstall lock threw
  `UnauthorizedAccessException` instead - confirmed this is the actual,
  consistent behavior for deleting a currently loaded/mapped executable
  image (`ERROR_ACCESS_DENIED`), different from the `IOException`
  (`ERROR_SHARING_VIOLATION`) that overwriting one throws. Fixed to catch
  both everywhere. **This alone fixed the Uninstall case** - tested live,
  confirmed via the log (`explorer.exe has this file locked - restarting
  it...` then `Deleted ...`), with no manual intervention needed.
- **Second guess, also wrong: it's the Windows Search Indexer
  (`SearchIndexer.exe`).** Goren then hit the same lock on a **re-Apply**
  (add a field, click Apply again without Uninstalling first) and the
  explorer-restart fix didn't help - confirmed directly that the
  freshly-restarted `explorer.exe` did NOT have the DLL loaded at all
  (`Get-Process -Id <pid> | Select Modules`), yet the file stayed locked
  with the same `ERROR_ACCESS_DENIED` pattern. Guessed the Search Indexer
  service next (a suspect already raised earlier in this project, for the
  same symptom class), added a `ServiceController`-based stop/retry/restart
  fallback (`System.ServiceProcess` reference added to
  `SwColumnManager.csproj`) - **tested live, still failed**, ruling this
  guess out too.
- **Found it for real via Windows' own Resource Monitor** (`resmon.exe`,
  CPU tab, "Search Handles", searched `SwPropertyHandler.dll`) rather than
  guessing a third time: **`SearchFilterHost.exe`** - a sandboxed surrogate
  process Windows Search spawns on demand to host third-party
  `IFilter`/property-handler COM components (like ours) in isolation, so a
  buggy one can't crash the indexer or `explorer.exe` itself. It holds the
  DLL loaded until its own idle timeout, independent of both
  `explorer.exe` and the `SearchIndexer` service - explaining why neither
  earlier fix worked. Replaced both previous attempts with a single fix:
  kill `SearchFilterHost.exe` directly (same `Process.GetProcessesByName`
  pattern as `explorer.exe`, no restart needed - Windows spawns a fresh one
  automatically whenever one's next needed) alongside the `explorer.exe`
  restart. Removed the now-unnecessary `ServiceController`/`WSearch` code
  and the `System.ServiceProcess` reference.
- **`ExplorerUtil.RetryOnLock(Action operation, Action<string> log)`** is
  the final shape: try the operation; on `IOException`/
  `UnauthorizedAccessException`, kill `SearchFilterHost.exe` + restart
  `explorer.exe` once, then retry up to 4 total attempts with a 1-second
  delay between them (covers a slower-clearing lock without repeatedly
  killing anything). All three call sites (`InstallActions.Apply`'s copy,
  `InstallActions.Uninstall`'s delete, `DocumentManagerSetup.RemoveIfOurs`'s
  delete) now share this one helper instead of three separately
  hand-written nested try/catch blocks.
- **Tested live, confirmed firing for real**: with `SearchFilterHost.exe`
  confirmed running, clicked Apply again - log showed `File is locked -
  killing SearchFilterHost.exe ... and restarting explorer.exe...`
  immediately followed by a successful copy on the very first retry, no
  manual intervention. Also reproduced Goren's exact original repro
  (add a field, Apply again without Uninstalling) successfully end-to-end
  multiple times in a row.

Also added, same session, a non-technical-user-facing result message:
`Program.RunElevatedAction` now returns whether it succeeded (`!hadError`)
and `Main` propagates that as the elevated process's real exit code
(previously always exited 0 regardless of `hadError` - the console log was
the only signal). `MainForm.RunElevated` checks `process.ExitCode` after
`WaitForExit()` and shows a plain "Apply/Uninstall completed successfully"
or "...did not complete successfully" `MessageBox`, instead of requiring
the user to read the console log (which flashes and closes on success
unless `--pause` is given).

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
    solidworksproperties.propdesc`). That XML file defines **21** properties
    (corrected session 7 - originally written as 24; a recount of every
    2019/2020 copy on this machine gives 21 `<propertyDescription>`
    entries, 3 with `<labelInfo>`. The "24" below in the first-pass test is
    likely the number of names that test queried, not the schema count -
    not re-verified)
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
    nothing else maps name to label. Only 3 of the 21 properties have a
    `<labelInfo>` at all (Description, LastSavedWith, OpenTime). Confirmed
    empirically: the other 18 (Material, Number, Author, Project,
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
- `DevTestTools/List-ExplorerColumns.ps1` - **moved into `DevTestTools/`
  in session 8**, along with every other dev/test tool below (see that
  folder's own bullet further down) - path references elsewhere in this
  file predating that move say just the bare filename. No prerequisites.
  Dumps every named Explorer detail column on this PC to
  `ExplorerColumns.csv` (written next to the script via `$PSScriptRoot`, so
  the move needed no script change), for cross-referencing candidate
  property names against the SW Custom tab. **Run** - found 325 named
  columns.
- `DevTestTools/ReadSwProperties/` (`Program.cs` + `ReadSwProperties.csproj`
  - **moved into their own subfolder in session 5**, having sat at the repo root
  since the very first commit. That root placement turned out to be a real,
  reproducible bug once enough sibling projects existed: an SDK-style
  project with no subfolder globs `**/*.cs` from its own directory
  recursively, so `dotnet build` on this project was silently sweeping up
  every other project's `.cs` files too - harmless while every other file
  just needed the BCL/the same SWDM interop reference this project already
  had, until `SwColumnManager` (needing `System.Windows.Forms`) got added
  and turned it into 60 real compile errors. Exactly the collision every
  *other* project's own subfolder was already deliberately avoiding - this
  one was just never moved) - small console app that reads a SolidWorks
  file's custom properties directly via the Document Manager API
  (`SolidWorks.Interop.swdocumentmgr`), independent of Explorer. **Built and
  run successfully** against the (now-removed, see above) test part.
  Required fixes, now done:
  1. Document Manager API license key supplied. **Changed (session 7):** no
     longer the `SWDM_LICENSE_KEY` environment variable - `ReadSwProperties.csproj`
     now compiles in the same git-ignored `SwPropertyHandler\LicenseKey.cs`
     the handler uses (linked `<Compile>`), so there's one key mechanism
     repo-wide. Never paste the key into a tracked file, a commit, or
     terminal output.
  2. `HintPath` in `ReadSwProperties.csproj` corrected to
     `C:\Program Files\Common Files\SOLIDWORKS Shared\SolidWorks.Interop.swdocumentmgr.dll`
     (the guessed default path was wrong).
  3. The code originally called a nonexistent method
     (`GetAllCustomPropertyNamesAndValues`); fixed to use the real API
     (`GetCustomPropertyNames()` + `GetCustomProperty(name, out type)`),
     found via reflection against the actual interop DLL.
  4. `Console.ReadKey()` at the end crashed when input was redirected
     (non-interactive runs); guarded with `Console.IsInputRedirected`.
  Builds as x64 / net48 - SolidWorks 2020+ is 64-bit only. **Moved again in
  session 8** into `DevTestTools/` (see that bullet further down); its
  `<Compile Include>` link to `SwPropertyHandler\LicenseKey.cs` updated
  from `..\` to `..\..\` accordingly - confirmed still builds.
- `DevTestTools/SwFilterDump/` - separate project (its own subfolder, so
  SDK-style file globbing doesn't collide with `ReadSwProperties.csproj`).
  **Moved into `DevTestTools/` in session 8** (no path fixes needed - no
  relative references). Drives
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
  repo; exists only on this machine. Nothing reads `SWDM_LICENSE_KEY` any
  more (session 7) - kept only as a local copy of the key.
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
  (fixtures, locked files, concurrency, performance) was run later and
  passed - see Phase 3 below.
- `DevTestTools/SwPropertyHandlerTest/` - **moved into `DevTestTools/` in
  session 8**; its `ProjectReference` to `SwPropertyHandler.csproj` updated
  from `..\` to `..\..\` accordingly - rebuilt and re-run against a real
  file after the move, identical output to before. Throwaway-style console
  harness for `SwPropertyHandler`, same pattern as `SwFilterDump`'s
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
  purely so MSBuild copies the handler DLL into this project's own output
  folder (the interop DLL no longer comes along - see "SolidWorks interop
  DLL no longer redistributed" below; since session 7 the native
  `swdocumentmgr.dll` *is* bundled, copied by a separate csproj item -
  see "End goal" below) -
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
- `README.md` - the public project README: build vs. use requirements,
  license key setup, building, using `SwColumnManager`, how it works, and
  the research/dev tools.

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
(also available on `ISwDMDocument23`, which the handler now uses for SW2019
compatibility - see "Known gaps" below)
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
2) - "work computer" is the separate production machine (untouched at the
time; first deployed to later - see "Known gaps" below). Phase 4
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
original plan (none remain deferred - see Phase 3 above). The work
computer (the actual production target, SW2019 only) has since had its
first deployment, which works after the `ISwDMDocument23` fix - see
"Known gaps" below. Separately, ongoing
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
(`FieldListEditor` was later merged into `SwColumnManager` - session 5 -
so read "`FieldListEditor`" below as `SwColumnManager`, where
`ColumnLookup.cs` and the `knownColumns.json` refresh live now.)
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
- **Deployment to the work computer - first deployment done, working.**
  The work computer has only SolidWorks 2019, and the first deploy didn't
  work there: the handler cast its document to `ISwDMDocument25`, an
  interface newer than SW2019's Document Manager provides. The test machine
  never showed this because its SW2020 install does provide it. A failed
  `as` cast returns null, so `TryOpenDocument` "failed" quietly and every
  property came back blank, with no error anywhere. Goren fixed it
  (`10555fa`) by targeting `ISwDMDocument23` instead, which SW2019 supports
  and which still has `GetCustomPropertyValues` and `GetFileAvgTime`. That
  version works on the work computer. **Rule going forward:** target the
  oldest `ISwDMDocumentNN` that has the needed methods, not the newest one
  the local interop DLL exposes, and test on the oldest SolidWorks version
  you need to support.
- **Config-file-based license key for wider binary distribution** - raised
  while prepping the repo to go public (the README no longer mentions it;
  distribution is now tracked under "End goal" below). Not built: `SwPropertyHandler` still needs the key compiled in, so a
  prebuilt binary would carry whoever built it's specific key. Only matters
  if this is ever handed to people who won't build it themselves.

### Critical bug found and fixed during final pre-public review (session 5)
Asked to do one more full pass for holes/bugs/edge cases after the repo
went public. Found a real one by actually re-testing live Explorer
resolution through `SwColumnManager`'s *actual* Apply flow - something that
had only ever been spot-checked via registry state (`CodeBase` pointing at
the right path), not re-verified end-to-end since the Program-Files install
location was introduced.

**The bug:** `InstallActions.Apply()` copied only `SwPropertyHandler.dll`
to `%ProgramFiles%\SwFileExplorerCustomColumns\` - never its
`SolidWorks.Interop.swdocumentmgr.dll` dependency. .NET resolves a
COM-hosted assembly's references from *its own* directory, not
`explorer.exe`'s - so the handler loaded "successfully" (every registry
entry looked completely correct: CLSID registered, `CodeBase` right,
`PropertyHandlers` repointed) but was **silently, completely
non-functional** - every property, new-column or legacy, came back blank,
with no error anywhere a user would see it. Reproduced directly: queried
`SwSync.Material` and `Solidworks.Document.Description` on a real file
through the live Explorer property path, got `''` for both; copied the
missing DLL in by hand, same query immediately returned correct resolved
values (`'AISI 316 Stainless Steel Sheet (SS)'`, `'sensor plate'`).

This had gone unnoticed through all of Phase 4's testing because that
testing used the `SwPropertyHandler`/dev-folder registration (both DLLs
always sitting together there already) - the gap was introduced later,
specifically by the Program-Files consolidation, and `SwPropertyHandlerTest`
couldn't have caught it either, for the same reason (its own build output
folder always has both DLLs via its own project reference).

**Fix:** `InstallActions.cs` now copies `SolidWorks.Interop.swdocumentmgr
.dll` alongside the handler on Apply, and removes it alongside the handler
on Uninstall. Re-verified the full cycle after the fix: clean uninstall of
the broken state, fresh Apply, confirmed both files present, confirmed live
resolution correct across two different real fixture files (new-column and
legacy properties both resolving correctly), clean uninstall again.
**Superseded by the next section** - the interop DLL is no longer copied at
all; the handler no longer needs it at runtime.

### SolidWorks interop DLL no longer redistributed (session 6) - done, tested
We're not allowed to publish/deploy SolidWorks DLLs (correction, session 7:
true for the interop DLL, which isn't on SolidWorks's redistributable list,
but SolidWorks's Document Manager help does allow redistributing the native
`swDocumentMgr.dll` - see "End goal" below), but the fix above
made Apply install `SolidWorks.Interop.swdocumentmgr.dll` into Program
Files, and the build bundled it next to `SwColumnManager.exe`.

**Fix:** `SwPropertyHandler.csproj`'s reference to that DLL is now
`EmbedInteropTypes=true` + `Private=false` ("No-PIA"). The build still
compiles against the builder's own locally installed copy (same
`HintPath`), but the compiler embeds just the SWDM interfaces/enums the
handler uses into `SwPropertyHandler.dll` itself. At runtime the handler
talks straight to the end user's own registered SWDM COM server - no
interop DLL needed anywhere, so the original "silently blank" bug can't
come back. `InstallActions.cs` no longer copies it; Apply and Uninstall
both delete a leftover copy from an older install
(`DeleteLegacyInteropDll`).

**Verified:**
- Clean rebuild: output folders for `SwColumnManager` and
  `SwPropertyHandlerTest` contain no SolidWorks DLL.
- `SwPropertyHandler.dll`'s referenced assemblies are only `mscorlib`,
  `System.Web.Extensions`, `System.Core` - the interop reference is gone.
- Full elevated Apply -> Uninstall cycle on this machine (with
  `fields.json` = `{"Material":100}`): Program Files held only
  `SwPropertyHandler.dll`; live Explorer resolution correct on a part,
  assembly, and drawing (`SwSync.Material = 'AISI 316 Stainless Steel Sheet
  (SS)'`, Description, LastSavedWith, OpenTime in our `'0 mins 01 secs'`
  format proving our handler served it). Uninstall fully clean
  (`PropertyHandlers` back to `{6A921E8A-...}`, CLSID gone, schema gone,
  OpenTime back to SolidWorks's own `'0:01'`).

**Not yet tested:** the leftover-DLL cleanup path (no older install was
present to clean up). **The SW2019 work computer is now tested** - see
"SW2019 work computer deployment (session 8)" near the top of this file;
`ISwDMDocument23` works there as expected, and the leftover-DLL cleanup
still wasn't exercised (Program Files was empty there too).

### End goal (session 7): a published exe anyone can use, for files up to SW2020
Goren's goal: eventually publish a prebuilt `SwColumnManager` that works on
any machine, SolidWorks installed or not, for files saved in SolidWorks
2020 or earlier. Steps:
1. **Licensing decision (Goren, on hold).** Shipping the key compiled in.
   Official DM help: "Do not share this license key with anyone outside your
   company or distribute it with any software that you ship" and "Each user
   ... must have a license key"; CodeStack reads it as binary-only
   redistribution being fine. Ambiguous - Goren is holding off publishing
   for now. Note: the key reads 2020-saved files, so its version is >= 2020
   (key version caps readable files: the key's version or earlier).
2. **Bundle + register the Document Manager - built (session 7), partly
   tested.** Official DM help (local `api\swdocmgrapi.chm`, Getting Started
   -> Installation): "You can redistribute swDocumentMgr.dll". Bundling the
   **2020** DLL (28.5, this machine's `SOLIDWORKS Shared` copy), switched
   from 2019 since the goal is files up to 2020 - sidesteps the
   older-DLL-on-newer-files question below. Never committed to the repo:
   `SwColumnManager.csproj` copies it from `$(SwDocumentMgrPath)` (default
   the builder's `SOLIDWORKS Shared`, same as the interop `HintPath`) next to
   the exe, with a build warning if missing. `DocumentManagerSetup.cs`: Apply
   registers it (copy to Program Files + `regsvr32 /s`) **only** if
   `SwDocumentMgr.SwDMClassFactory` has no registration whose
   `InprocServer32` file exists; stops with `ERROR` (before repointing
   `PropertyHandlers`) if neither registered nor bundled, or if the VC++
   x64 runtime is missing (`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\
   Runtimes\x64` `Installed=1`). Ownership = registered path equals our
   Program Files copy - no marker file. Uninstall runs `regsvr32 /u` only
   then, and deletes our copy either way. **Tested on this machine
   (SolidWorks installed):** Apply logged "already registered (SolidWorks's
   own copy) ... leaving it alone", Uninstall logged "nothing to remove",
   no `ERROR`s; registry afterwards still pointed the DM at `SOLIDWORKS
   Shared`, `PropertyHandlers` back to `{6A921E8A-...}`. Read via the new
   `--pause` flag (`.\SwColumnManager.exe --pause` - forwarded by the
   editor's buttons to the elevated console, which then waits for a key
   even on success). `zlib.dll` deliberately ignored:
   the DM help says to ship it, but this machine's working DM has no
   `zlib.dll` next to it, doesn't import one, and has zlib compiled in.
3. **VC++ runtime check** - built, part of step 2.
4. **Test on a clean machine with no SolidWorks** (VM/spare PC) - not done;
   this machine can't exercise the register path without breaking
   SolidWorks's own registration.
5. **Release packaging** - zip on GitHub Releases; unsigned exe will get
   SmartScreen warnings.
6. **Ongoing:** rebuild/re-release when the key or bundled DLL version moves.

Interop DLL decision: keep the `HintPath` to the builder's SolidWorks
install - not committed to the repo, not NuGet (third-party packages
exist, e.g. `SolidWorks.Interop.swdocumentmgr` by "avidesk", none official).
Anyone building needs a DM key, which needs a subscription, so they have
SolidWorks anyway. Checked: no `xarial`/`codestack-net` GitHub repo hosts
the native `swdocumentmgr.dll`, only the interop DLL.

**Open question (session 7) - does an older `swdocumentmgr.dll` read newer
files?** No longer blocking (bundling the 2020 DLL for a files-up-to-2020
goal), but still unknown. Goren's guess: an older DLL may read newer files
given a newer key. Suspected not, because `SwDmDocumentOpenError` has a
`FutureVersion` code separate from `NoLicense`, but **untested**. Test plan:
load an older DLL directly (`LoadLibrary` + `DllGetClassObject`, like
`SwFilterDump` does for `sldpropertyhandler.dll`, without touching the
registry) and open files saved in a newer version (e.g. a 2019 DLL against
`E:\for testing\A-EYE 2020\`). This machine has no 2019 copy (SW2020
overwrote `SOLIDWORKS Shared`). Whether an older DLL accepts a newer key
needs a newer key to test.

**Minor, non-blocking edge cases noted while reviewing, not acted on:**
- `SchemaGenerator.BuildCanonicalName`'s sanitizer falls back to the literal
  string `"Field"` for a name with no alphanumeric characters at all (e.g.
  tracking a property literally named `"!!!"`) - two such degenerate names
  would collide on the same canonical schema identifier (different PIDs,
  same `name=`). Only reachable with a deliberately symbols-only custom
  property name, not a realistic one.
- `MainForm.RunElevated` only catches `Win32Exception` (the UAC-decline
  case) around the relaunch `Process.Start` - any other exception type
  there would propagate to WinForms' default unhandled-exception path
  instead of a graceful message. Very low likelihood (`Application
  .ExecutablePath` is always valid for a running process) but not
  impossible in principle.

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
