using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SwColumnManager
{
    // Apply/Uninstall sometimes need to delete or overwrite a DLL
    // (SwPropertyHandler.dll, or a bundled swdocumentmgr.dll) that's locked
    // by something other than this process. Confirmed the real cause
    // directly (Resource Monitor's handle search, not guessed):
    // SearchFilterHost.exe - a sandboxed surrogate process Windows Search
    // uses to host third-party IFilter/property-handler COM components
    // (ours included) in isolation, so a buggy one can't crash the indexer
    // or explorer.exe. It's spawned on demand and holds the DLL loaded
    // until its own idle timeout, independent of both explorer.exe and the
    // SearchIndexer service - neither restarting explorer.exe nor stopping
    // the Windows Search service released the lock; killing
    // SearchFilterHost.exe directly did. It never needs restarting - a new
    // one spawns automatically whenever Explorer/Search next need one.
    internal static class ExplorerUtil
    {
        private const int MaxAttempts = 4;
        private const int RetryDelayMs = 1000;

        public static void RetryOnLock(Action operation, Action<string> log)
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    operation();
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt == MaxAttempts)
                    {
                        throw;
                    }

                    if (attempt == 1)
                    {
                        KillLockingProcesses(log);
                    }
                    else
                    {
                        log($"Still locked (attempt {attempt}/{MaxAttempts}) - waiting a moment and retrying...");
                    }

                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        private static void KillLockingProcesses(Action<string> log)
        {
            log("File is locked - killing SearchFilterHost.exe (Windows Search's sandboxed " +
                "filter host, the confirmed real culprit) and restarting explorer.exe...");

            // SearchFilterHost.exe: a new one spawns on demand - no restart needed.
            Kill("SearchFilterHost");

            // explorer.exe: Windows relaunches it automatically as the
            // interactive user (not elevated) once it exits unexpectedly, so
            // this never explicitly starts a new one itself, which would
            // launch an elevated Explorer instead.
            Kill("explorer");
        }

        private static void Kill(string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Best-effort - if this didn't actually release the handle,
                    // the retries that follow will fail again with their own error.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
