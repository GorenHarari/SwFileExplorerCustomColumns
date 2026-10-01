# Explorer column investigation - two small scripts

I can't run these myself (this session doesn't have access to Windows or your
SolidWorks install), so both are meant for you to run locally and report back
what happens - errors included. That's the next small step either way.

## 1. `List-ExplorerColumns.ps1`

No prerequisites, nothing to configure. Just run it (right-click -> Run with
PowerShell). It writes `ExplorerColumns.csv` with every named Explorer detail
column your system knows about (index + name). "Description", "Category",
"Company" and "Subject" are in there somewhere - useful to confirm the exact
spelling/casing Windows uses, and to spot other plain-English candidates worth
testing (Status, Owner, Rating, Location, Manager, etc.).

## 2. `ReadSwProperties/` (C# console app)

Reads every document-level custom property directly from a SolidWorks file via
the Document Manager API - independent of Explorer entirely, so we can see
exactly what's stored in the file itself.

Before it will build/run, two things need fixing on your machine:

1. **License key** (`SWDM_LICENSE_KEY` environment variable) - the Document
   Manager API needs its own license key, separate from your normal SolidWorks
   license. It's requested through the SOLIDWORKS Customer Portal against your
   serial number; if you can't find the request form there, your reseller/VAR
   should be able to point you to it. The key is a secret, so it is **not** kept
   in source - `Program.cs` reads it from the `SWDM_LICENSE_KEY` environment
   variable at runtime:

   ```powershell
   $env:SWDM_LICENSE_KEY = '<your key>'
   ```

   On this machine the key and that command are stored in `SwDmLicenseKey.md`,
   which is git-ignored and so not part of this repo. Without a key the app
   prints a clear message telling you to set the variable, and with an invalid
   one it prints a "could not start" message rather than failing silently.
2. **DLL reference path** (`ReadSwProperties.csproj`) - points at
   `SolidWorks.Interop.swdocumentmgr.dll`. The path in the file is a guess;
   search your C: drive for that filename and fix the `HintPath` if it's
   somewhere else.

Once both are set, from a command prompt in the `ReadSwProperties` folder:

```
dotnet build
dotnet run -- "C:\path\to\some\part.SLDPRT"
```

(Or open the folder in Visual Studio and run it from there, if that's more
your habit.)

## What to report back

- Did `List-ExplorerColumns.ps1` run cleanly? Anything in the CSV that looks
  like a promising candidate to test?
- Did the C# app build? If not, what was the error (missing license key,
  wrong DLL path, or something else)?
- Once it runs against a real file: does the property list it prints match
  what you see in SolidWorks' own Custom Properties tab?

We'll use whatever comes back to figure out the actual scope of the
"name collision" behavior, and decide from there whether it's worth chasing
further or whether to move to one of the three routes we discussed earlier
(real property handler, sync app, or the Xarial tool).
