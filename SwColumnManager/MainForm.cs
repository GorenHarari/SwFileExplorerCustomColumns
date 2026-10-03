using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
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
        public int Pid { get; }

        public FieldItem(string name, int pid)
        {
            Name = name;
            Pid = pid;
        }

        public override string ToString() => Name;
    }

    public class MainForm : Form
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "fields.json");
        private static readonly string KnownColumnsPath = Path.Combine(ConfigDir, "knownColumns.json");

        private readonly ListBox _listBox = new ListBox();
        private readonly TextBox _textBox = new TextBox();
        private readonly Button _addButton = new Button();
        private readonly Button _removeButton = new Button();
        private readonly Button _applyButton = new Button();
        private readonly Button _uninstallButton = new Button();

        private Dictionary<string, int> _fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private const int EM_SETCUEBANNER = 0x1501;

        public MainForm()
        {
            Text = "SolidWorks Explorer Columns";
            Width = 480;
            Height = 430;
            MinimumSize = new System.Drawing.Size(420, 320);

            _listBox.SetBounds(12, 12, 440, 260);
            _listBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _textBox.SetBounds(12, 282, 340, 24);
            _textBox.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _textBox.HandleCreated += (s, e) =>
                SendMessage(_textBox.Handle, EM_SETCUEBANNER, IntPtr.Zero, "Insert property name");

            _addButton.Text = "Add";
            _addButton.SetBounds(360, 281, 92, 26);
            _addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _addButton.Click += (s, e) => AddField();

            _removeButton.Text = "Remove";
            _removeButton.SetBounds(12, 316, 440, 26);
            _removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _removeButton.Click += (s, e) => RemoveSelectedField();

            _applyButton.Text = "Apply Changes";
            _applyButton.SetBounds(12, 352, 214, 32);
            _applyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _applyButton.BackColor = System.Drawing.Color.LightYellow;
            _applyButton.Click += (s, e) => RunElevated("--apply",
                "This will register the property handler and apply your tracked fields to " +
                "Windows Explorer. You'll be prompted for administrator approval.");

            _uninstallButton.Text = "Uninstall";
            _uninstallButton.SetBounds(238, 352, 214, 32);
            _uninstallButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _uninstallButton.BackColor = System.Drawing.Color.MistyRose;
            _uninstallButton.Click += (s, e) => RunElevated("--uninstall",
                "This will remove the property handler and restore SolidWorks's original " +
                "Explorer columns. You'll be prompted for administrator approval.");

            _textBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddField();
                }
            };

            Controls.Add(_listBox);
            Controls.Add(_textBox);
            Controls.Add(_addButton);
            Controls.Add(_removeButton);
            Controls.Add(_applyButton);
            Controls.Add(_uninstallButton);

            LoadFields();
            RefreshListBox();

            // Fire-and-forget on load - a property name that already matches
            // an existing Explorer column needs no entry here at all (see
            // SwPropertyHandler), but it needs this cache to exist and be
            // reasonably fresh. Cheap enough (well under a second for ~325
            // columns, confirmed earlier) to just always redo on launch
            // rather than add a manual refresh button to forget to click.
            Shown += (s, e) => RefreshKnownColumnsCache();
        }

        private void RefreshKnownColumnsCache()
        {
            try
            {
                var columns = ColumnLookup.GetAllColumns();

                var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in columns)
                {
                    // First occurrence wins if a display name has more than
                    // one registered PROPERTYKEY (seen for a few names during
                    // testing) - deterministic, simplest tie-break available.
                    if (!byName.ContainsKey(c.Name))
                    {
                        byName[c.Name] = new Dictionary<string, object>
                        {
                            ["fmtid"] = c.Key.fmtid.ToString(),
                            ["pid"] = c.Key.pid
                        };
                    }
                }

                Directory.CreateDirectory(ConfigDir);
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(KnownColumnsPath, serializer.Serialize(byName));
            }
            catch
            {
                // Best-effort refresh - a stale or missing cache just means less
                // auto-matching until the next successful launch, not a crash.
            }
        }

        private void LoadFields()
        {
            if (!File.Exists(ConfigPath))
            {
                _fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            string json = File.ReadAllText(ConfigPath);
            var serializer = new JavaScriptSerializer();
            _fields = serializer.Deserialize<Dictionary<string, int>>(json);
        }

        private void SaveFields()
        {
            Directory.CreateDirectory(ConfigDir);

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(_fields);
            File.WriteAllText(ConfigPath, json);
        }

        private void RefreshListBox()
        {
            _listBox.Items.Clear();
            foreach (var kvp in _fields.OrderBy(kvp => kvp.Value))
            {
                _listBox.Items.Add(new FieldItem(kvp.Key, kvp.Value));
            }
        }

        // Already always served directly under SolidWorks's own legacy
        // PROPERTYKEYs by the property handler - adding one of these as a
        // tracked field would create a second, confusingly-labeled column
        // reading a *different* data source (the custom property of that
        // name, vs. the Summary tab field these actually serve - except
        // Description, which genuinely is a custom-property-name match,
        // just one SolidWorks itself already bothered to register a label
        // for), not a harmless duplicate. See CLAUDE.md, Phase 4 (Issue 1)
        // and SwPropertyHandler/LegacyProperties.cs. Includes common label
        // variants (Authors/Comments/Tags) since those are what a user is
        // likely to type, not just the exact PKEY name.
        private static readonly string[] ReservedNames =
        {
            "Description", "OpenTime", "LastSavedWith",
            "Title", "Subject", "Author", "Authors", "Comment", "Comments", "Keywords", "Tags"
        };

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
                    "This is one of the properties (Description, OpenTime, LastSavedWith, Title, " +
                    "Subject, Author, Comments, Keywords) that the Explorer column handler always " +
                    "provides automatically, continuing what SolidWorks's own handler used to show " +
                    "for these - adding it here would create a second, confusingly-labeled column " +
                    "reading a different, unrelated piece of data (a custom property of that name, " +
                    "if one exists) rather than this one.",
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

        private void RunElevated(string arg, string confirmMessage)
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
                using (var process = Process.Start(psi))
                {
                    process.WaitForExit();
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
