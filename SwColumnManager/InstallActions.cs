using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SwColumnManager
{
    // The two consolidated elevated workflows - everything SchemaApplyTool
    // used to split across several manual buttons (Apply Schema, register
    // the handler via a hand-run regasm call, Repoint to Real Handler),
    // merged into one idempotent "Apply" a user can re-run after any
    // fields.json edit. See CLAUDE.md, "merge into one shippable tool".
    internal static class InstallActions
    {
        private const string RegAsmPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe";
        private const string RealHandlerClsid = "{E558E17D-51E7-4043-89D8-5EDB8498454F}";

        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SwFileExplorerCustomColumns");

        private const string HandlerDllName = "SwPropertyHandler.dll";

        // No longer installed: SolidWorks's interop DLL isn't on SolidWorks's
        // list of redistributable files, so the handler embeds the SWDM interop types it needs
        // (EmbedInteropTypes in SwPropertyHandler.csproj) and has no runtime
        // dependency on this file. Older versions of this tool *did* copy it
        // here, though, so Apply/Uninstall still delete any leftover copy.
        private const string LegacyInteropDllName = "SolidWorks.Interop.swdocumentmgr.dll";

        private static readonly string InstalledHandlerDllPath = Path.Combine(InstallDir, HandlerDllName);
        private static readonly string LegacyInteropDllPath = Path.Combine(InstallDir, LegacyInteropDllName);

        private static readonly string SourceHandlerDllPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, HandlerDllName);

        private static readonly string SchemaPath = Path.Combine(FieldsStore.ConfigDir, "SwFileExplorerCustomColumns.propdesc");

        public static void Apply(Action<string> log)
        {
            log($"Install directory: {InstallDir}");
            Directory.CreateDirectory(InstallDir);

            log("--- Step 1: make sure a SolidWorks Document Manager is registered ---");
            if (!DocumentManagerSetup.EnsureRegistered(InstallDir, log))
            {
                return;
            }

            log("--- Step 2: install the property handler DLL ---");
            try
            {
                File.Copy(SourceHandlerDllPath, InstalledHandlerDllPath, overwrite: true);
                log($"Copied handler to {InstalledHandlerDllPath}");
            }
            catch (IOException ex)
            {
                log($"ERROR copying handler files: {ex.Message}");
                log("If the handler is already loaded by a running explorer.exe (from a previous");
                log("install), close Explorer windows or restart explorer.exe and try again.");
                return;
            }

            DeleteLegacyInteropDll(log);

            var regResult = RunProcess(RegAsmPath, $"\"{InstalledHandlerDllPath}\" /codebase");
            log($"regasm /codebase exit code: {regResult.ExitCode}");
            LogProcessOutput(log, regResult);

            log("--- Step 3: generate and register the property schema ---");
            var fields = FieldsStore.Load();
            if (fields.Count == 0)
            {
                log("No tracked fields in fields.json - schema will have zero custom entries (legacy/auto-matched properties are unaffected).");
            }

            if (File.Exists(SchemaPath))
            {
                int unregResult = NativeMethods.PSUnregisterPropertySchema(SchemaPath);
                log($"PSUnregisterPropertySchema (old schema) -> 0x{unregResult:X8}");
            }

            string xml = SchemaGenerator.BuildXml(fields);
            File.WriteAllText(SchemaPath, xml);
            log($"Wrote schema XML ({fields.Count} field(s)) to {SchemaPath}");

            int regSchemaResult = NativeMethods.PSRegisterPropertySchema(SchemaPath);
            log($"PSRegisterPropertySchema (new schema) -> 0x{regSchemaResult:X8}");

            log("--- Step 4: point PropertyHandlers at the real handler ---");
            foreach (string ext in PropertyHandlerRegistry.Extensions)
            {
                PropertyHandlerRegistry.SetClsid(ext, RealHandlerClsid);
                log($"  {ext} -> {RealHandlerClsid}");
            }

            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            log("Sent SHChangeNotify(SHCNE_ASSOCCHANGED).");
            log("");
            log("Apply finished. Open a NEW Explorer tab/window to see new columns - an");
            log("already-open window won't refresh its column list on its own.");
        }

        public static void Uninstall(Action<string> log)
        {
            log("--- Step 1: revert PropertyHandlers to SolidWorks's original handler ---");
            foreach (string ext in PropertyHandlerRegistry.Extensions)
            {
                PropertyHandlerRegistry.SetClsid(ext, PropertyHandlerRegistry.OriginalSolidWorksClsid);
                log($"  {ext} -> {PropertyHandlerRegistry.OriginalSolidWorksClsid}");
            }

            log("--- Step 2: unregister and remove the schema ---");
            if (File.Exists(SchemaPath))
            {
                int unregResult = NativeMethods.PSUnregisterPropertySchema(SchemaPath);
                log($"PSUnregisterPropertySchema -> 0x{unregResult:X8}");
                File.Delete(SchemaPath);
                log($"Deleted {SchemaPath}");
            }
            else
            {
                log("No schema file on disk - nothing to unregister.");
            }

            log("--- Step 3: unregister and remove the property handler DLL ---");
            if (File.Exists(InstalledHandlerDllPath))
            {
                var result = RunProcess(RegAsmPath, $"\"{InstalledHandlerDllPath}\" /unregister");
                log($"regasm /unregister exit code: {result.ExitCode}");
                LogProcessOutput(log, result);

                try
                {
                    File.Delete(InstalledHandlerDllPath);
                    log($"Deleted {InstalledHandlerDllPath}");
                }
                catch (IOException ex)
                {
                    log($"Could not delete the handler DLL (likely still loaded by explorer.exe): {ex.Message}");
                }

                DeleteLegacyInteropDll(log);
            }
            else
            {
                log("No installed handler DLL found - nothing to unregister.");
            }

            log("--- Step 4: remove the bundled Document Manager, if this tool registered it ---");
            DocumentManagerSetup.RemoveIfOurs(InstallDir, log);

            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            log("");
            log("Uninstall finished. fields.json and knownColumns.json were left in place");
            log("(your tracked field list) - delete them by hand if you want a totally clean slate.");
        }

        private static void DeleteLegacyInteropDll(Action<string> log)
        {
            if (!File.Exists(LegacyInteropDllPath)) return;

            try
            {
                File.Delete(LegacyInteropDllPath);
                log($"Deleted leftover {LegacyInteropDllPath} from an older install");
            }
            catch (IOException ex)
            {
                log($"Could not delete leftover {LegacyInteropDllName}: {ex.Message}");
            }
        }

        private struct ProcessResult
        {
            public int ExitCode;
            public string Output;
            public string Error;
        }

        // Reads stdout/stderr before WaitForExit, not after - reading after
        // risks a deadlock if the process fills its output buffer while
        // waiting for someone to drain it before it can exit.
        private static ProcessResult RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new ProcessResult { ExitCode = process.ExitCode, Output = output, Error = error };
            }
        }

        private static void LogProcessOutput(Action<string> log, ProcessResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Output)) log(result.Output.Trim());
            if (!string.IsNullOrWhiteSpace(result.Error)) log("stderr: " + result.Error.Trim());
        }
    }
}
