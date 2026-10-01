using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwColumnManager
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase) ||
                                     args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                RunElevatedAction(args[0]);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // Plain console output instead of a WinForms log window - this
        // process is only ever launched for one short, scripted task
        // (Apply or Uninstall), so there's nothing a GUI adds here. Attaching
        // a real console (this exe is WinExe, so none is allocated by
        // default - that's deliberate, so the everyday editor launch never
        // flashes a console window) means the window just closes naturally
        // when Main returns, with no "did it succeed, should I auto-close"
        // logic to maintain - only pausing on an actual failure so there's
        // something to read.
        private static void RunElevatedAction(string arg)
        {
            AllocConsole();

            bool hadError = false;
            Action<string> log = message =>
            {
                if (message.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    hadError = true;
                }
                Console.WriteLine(message);
            };

            try
            {
                if (arg.Equals("--apply", StringComparison.OrdinalIgnoreCase))
                {
                    InstallActions.Apply(log);
                }
                else
                {
                    InstallActions.Uninstall(log);
                }
            }
            catch (Exception ex)
            {
                log($"ERROR: {ex.GetType().Name}: {ex.Message}");
            }

            if (hadError)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close...");
                Console.ReadKey();
            }
        }
    }
}
