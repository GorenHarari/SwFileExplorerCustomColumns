using System;
using System.Windows.Forms;

namespace SwColumnManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                Application.Run(new ElevatedActionForm(ElevatedAction.Apply));
                return;
            }

            if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Application.Run(new ElevatedActionForm(ElevatedAction.Uninstall));
                return;
            }

            Application.Run(new MainForm());
        }
    }
}
