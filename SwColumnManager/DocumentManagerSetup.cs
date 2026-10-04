using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SwColumnManager
{
    // Makes sure a SolidWorks Document Manager (swdocumentmgr.dll) is
    // registered, since the property handler reads every file through it.
    // A SolidWorks install registers its own copy - in that case this never
    // touches anything. Only on a machine with no registered Document
    // Manager does it register the copy bundled with this tool
    // (SolidWorks's Document Manager help: "You can redistribute
    // swDocumentMgr.dll"). Ownership is decided by the registered path: if
    // it points at our install folder, we registered it and Uninstall may
    // undo it; anything else belongs to SolidWorks and is left alone.
    internal static class DocumentManagerSetup
    {
        public const string DllName = "swdocumentmgr.dll";

        private const string ProgId = "SwDocumentMgr.SwDMClassFactory";

        private const string VcRuntimeKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";

        private const string VcRuntimeDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        private static readonly string RegSvr32Path = Path.Combine(Environment.SystemDirectory, "regsvr32.exe");

        // Returns false if Apply must stop - without a Document Manager the
        // handler would serve blank values for every property, including the
        // 3 SolidWorks columns that worked before this tool was installed.
        public static bool EnsureRegistered(string installDir, Action<string> log)
        {
            string installedPath = Path.Combine(installDir, DllName);
            string registeredPath = GetRegisteredPath();

            if (registeredPath != null && File.Exists(registeredPath))
            {
                bool ours = IsSamePath(registeredPath, installedPath);
                log(ours
                    ? $"Document Manager already registered (installed by this tool): {registeredPath}"
                    : $"Document Manager already registered (SolidWorks's own copy): {registeredPath} - leaving it alone");
                return true;
            }

            log("No registered Document Manager found - installing the bundled copy.");

            string sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName);
            if (!File.Exists(sourcePath))
            {
                log($"ERROR: no bundled {DllName} next to SwColumnManager.exe, and none registered on this machine.");
                log("Install SolidWorks, or rebuild this tool on a machine that has SolidWorks installed.");
                return false;
            }

            if (!IsVcRuntimeInstalled())
            {
                log("ERROR: the Document Manager needs the Microsoft Visual C++ Redistributable (x64), which isn't installed.");
                log($"Download and install it from {VcRuntimeDownloadUrl}, then run Apply again.");
                return false;
            }

            try
            {
                File.Copy(sourcePath, installedPath, overwrite: true);
                log($"Copied {DllName} to {installedPath}");
            }
            catch (IOException ex)
            {
                log($"ERROR copying {DllName}: {ex.Message}");
                return false;
            }

            int exitCode = RunRegSvr32($"/s \"{installedPath}\"");
            if (exitCode != 0)
            {
                log($"ERROR: regsvr32 failed to register {installedPath} (exit code {exitCode}).");
                return false;
            }

            log($"Registered {installedPath}");
            return true;
        }

        // Only undoes our own registration. If SolidWorks was installed after
        // us and re-registered its copy, the registration isn't ours any more,
        // but our leftover file is still deleted.
        public static void RemoveIfOurs(string installDir, Action<string> log)
        {
            string installedPath = Path.Combine(installDir, DllName);
            if (!File.Exists(installedPath))
            {
                log("No bundled Document Manager installed by this tool - nothing to remove.");
                return;
            }

            string registeredPath = GetRegisteredPath();
            if (registeredPath != null && IsSamePath(registeredPath, installedPath))
            {
                int exitCode = RunRegSvr32($"/u /s \"{installedPath}\"");
                log($"regsvr32 /u exit code: {exitCode}");
            }
            else
            {
                log($"Document Manager registration points elsewhere ({registeredPath ?? "none"}) - leaving it alone.");
            }

            try
            {
                ExplorerUtil.RetryOnLock(() => File.Delete(installedPath), log);
                log($"Deleted {installedPath}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log($"Could not delete {installedPath}: {ex.Message}");
            }
        }

        // ProgID -> CLSID -> InprocServer32 path, or null if any link is missing.
        private static string GetRegisteredPath()
        {
            string clsid;
            using (var key = Registry.ClassesRoot.OpenSubKey(ProgId + @"\CLSID"))
            {
                clsid = key?.GetValue(null) as string;
            }
            if (string.IsNullOrEmpty(clsid)) return null;

            using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32"))
            {
                string path = key?.GetValue(null) as string;
                return string.IsNullOrEmpty(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim('"'));
            }
        }

        private static bool IsVcRuntimeInstalled()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(VcRuntimeKey))
            {
                return key?.GetValue("Installed") is int installed && installed == 1;
            }
        }

        private static bool IsSamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static int RunRegSvr32(string arguments)
        {
            var psi = new ProcessStartInfo(RegSvr32Path, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
