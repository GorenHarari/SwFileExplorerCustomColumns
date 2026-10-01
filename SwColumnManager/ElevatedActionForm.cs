using System;
using System.Security.Principal;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SwColumnManager
{
    internal enum ElevatedAction
    {
        Apply,
        Uninstall
    }

    // Shown only when this exe is re-launched elevated (see MainForm's
    // "Apply Changes"/"Uninstall" buttons and Program.cs's --apply/
    // --uninstall handling). Runs the chosen action once on load and
    // displays a log - the same visibility SchemaApplyTool's log box gave,
    // just auto-triggered instead of needing a manual click per step.
    internal class ElevatedActionForm : Form
    {
        private readonly ElevatedAction _action;
        private readonly TextBox _log = new TextBox();
        private readonly Button _closeButton = new Button();
        private bool _hadError;

        public ElevatedActionForm(ElevatedAction action)
        {
            _action = action;

            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

            Text = $"SolidWorks Explorer Columns - {(action == ElevatedAction.Apply ? "Applying Changes" : "Uninstalling")}" +
                   (isAdmin ? "" : " (NOT ELEVATED - this will fail)");
            Width = 560;
            Height = 420;
            MinimumSize = new System.Drawing.Size(420, 300);

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Font = new System.Drawing.Font("Consolas", 9f);
            _log.SetBounds(12, 12, 520, 320);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _closeButton.Text = "Close";
            _closeButton.SetBounds(456, 340, 76, 28);
            _closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeButton.Click += (s, e) => Close();

            Controls.Add(_log);
            Controls.Add(_closeButton);

            Shown += (s, e) => RunAction();
        }

        private void AppendLog(string message)
        {
            // Only our own explicit "ERROR" messages count - regasm /codebase
            // routinely writes a benign warning to stderr on a non-strong-named
            // assembly (ours), so mere stderr output isn't a reliable failure
            // signal on its own.
            if (message.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                _hadError = true;
            }

            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }

        private void RunAction()
        {
            try
            {
                if (_action == ElevatedAction.Apply)
                {
                    InstallActions.Apply(AppendLog);
                }
                else
                {
                    InstallActions.Uninstall(AppendLog);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
            }

            // Auto-close on a clean run so this doesn't linger as an extra
            // window to dismiss every time - stays open on any error so
            // there's something to actually read.
            if (!_hadError)
            {
                AppendLog("Done - closing automatically in 2 seconds.");
                var timer = new Timer { Interval = 2000 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    Close();
                };
                timer.Start();
            }
        }
    }
}
