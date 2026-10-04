using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwColumnManager
{
    // One entry in fields.json: a tracked SolidWorks custom-property name
    // needing a brand-new column, and its permanently-assigned PID (see
    // CLAUDE.md, Phase 0). Properties that already match an existing
    // Explorer column are handled automatically by SwPropertyHandler via
    // the knownColumns.json cache this editor maintains - they never need
    // an entry here at all. See CLAUDE.md, Phase 4 / Issue 2.
    internal class FieldItem
    {
        public string Name { get; }

        public FieldItem(string name)
        {
            Name = name;
        }

        public override string ToString() => Name;
    }

    public partial class MainForm : Form
    {
        private Dictionary<string, int> _fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const int EM_SETCUEBANNER = 0x1501;

        // Already always served directly under SolidWorks's own legacy
        // PROPERTYKEYs by the property handler - adding one of these as a
        // tracked field would create a second, confusingly-labeled column
        // reading a *different* data source (the custom property of that
        // name, vs. the Summary tab field these actually serve - except
        // Description, which genuinely is a custom-property-name match,
        // just one SolidWorks itself already bothered to register a label
        // for), not a harmless duplicate. See CLAUDE.md, Phase 4 (Issue 1).
        // Sourced directly from SwPropertyHandler.LegacyProperties (the real
        // list SwPropertyStore checks against) rather than a second
        // hand-maintained copy here.
        private static readonly string[] ReservedNames = SwPropertyHandler.LegacyProperties.ReservedNames;

        public MainForm()
        {
            InitializeComponent();

            LoadFields();
            RefreshListBox();
        }

        // Fire-and-forget on load - a property name that already matches
        // an existing Explorer column needs no entry here at all (see
        // SwPropertyHandler), but it needs this cache to exist and be
        // reasonably fresh. Cheap enough (well under a second for ~325
        // columns, confirmed earlier) to just always redo on launch rather
        // than add a manual refresh button to forget to click.
        private void MainForm_Shown(object sender, EventArgs e)
        {
            KnownColumnsCache.Refresh();
        }

        private void TextBox_HandleCreated(object sender, EventArgs e)
        {
            SendMessage(_textBox.Handle, EM_SETCUEBANNER, IntPtr.Zero, "Insert property name");
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddField();
            }
        }

        private void AddButton_Click(object sender, EventArgs e) => AddField();

        private void RemoveButton_Click(object sender, EventArgs e) => RemoveSelectedField();

        private void ApplyButton_Click(object sender, EventArgs e) => RunElevated("--apply", "Apply",
            "This will register the property handler and apply your tracked fields to " +
            "Windows Explorer. You'll be prompted for administrator approval.");

        private void UninstallButton_Click(object sender, EventArgs e) => RunElevated("--uninstall", "Uninstall",
            "This will remove the property handler and restore SolidWorks's original " +
            "Explorer columns. You'll be prompted for administrator approval.");

        private void LoadFields()
        {
            _fields = FieldsStore.Load();
        }

        private void SaveFields()
        {
            FieldsStore.Save(_fields);
        }

        private void RefreshListBox()
        {
            _listBox.Items.Clear();
            foreach (var kvp in _fields.OrderBy(kvp => kvp.Value))
            {
                _listBox.Items.Add(new FieldItem(kvp.Key));
            }
        }

        private void AddField()
        {
            string name = _textBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (ReservedNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this,
                    $"'{name}' can't be added as a tracked field.\n\n" +
                    "This is one of the properties (" + string.Join(", ", ReservedNames) + ") that the " +
                    "Explorer column handler always provides automatically, continuing what SolidWorks's " +
                    "own handler used to show for these - adding it here would create a second, " +
                    "confusingly-labeled column reading a different, unrelated piece of data (a custom " +
                    "property of that name, if one exists) rather than this one.",
                    "Reserved field name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_fields.ContainsKey(name))
            {
                MessageBox.Show(this, $"'{name}' is already tracked.", "Duplicate field",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int nextPid = _fields.Count > 0 ? _fields.Values.Max() + 1 : 100;
            _fields[name] = nextPid;
            SaveFields();
            RefreshListBox();
            _textBox.Clear();
        }

        private void RemoveSelectedField()
        {
            if (!(_listBox.SelectedItem is FieldItem selected))
            {
                return;
            }

            _fields.Remove(selected.Name);
            SaveFields();
            RefreshListBox();
        }

        private void RunElevated(string arg, string actionLabel, string confirmMessage)
        {
            var confirm = MessageBox.Show(this, confirmMessage, "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, Program.Pause ? arg + " --pause" : arg)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Cursor = Cursors.WaitCursor;
                int exitCode;
                using (var process = Process.Start(psi))
                {
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }

                if (exitCode == 0)
                {
                    MessageBox.Show(this, $"{actionLabel} completed successfully.", actionLabel,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this,
                        $"{actionLabel} did not complete successfully.\n\n" +
                        "Run the tool again with --pause (or just try again) to see the detailed log.",
                        actionLabel, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Win32Exception)
            {
                // User declined the UAC prompt - not an error, just cancelled.
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
    }
}
