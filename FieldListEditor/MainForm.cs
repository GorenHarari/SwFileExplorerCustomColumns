using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FieldListEditor
{
    // One entry in fields.json: a tracked SolidWorks custom-property name and
    // its permanently-assigned PID (see CLAUDE.md, Phase 0).
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
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns", "fields.json");

        private readonly ListBox _listBox = new ListBox();
        private readonly TextBox _textBox = new TextBox();
        private readonly Button _addButton = new Button();
        private readonly Button _removeButton = new Button();

        private Dictionary<string, int> _fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public MainForm()
        {
            Text = "SolidWorks Explorer Columns - Tracked Fields";
            Width = 420;
            Height = 420;
            MinimumSize = new System.Drawing.Size(320, 300);

            _listBox.SetBounds(12, 12, 380, 300);
            _listBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom
                               | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;

            _textBox.SetBounds(12, 322, 280, 24);
            _textBox.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left
                               | System.Windows.Forms.AnchorStyles.Right;

            _addButton.Text = "Add";
            _addButton.SetBounds(300, 321, 92, 26);
            _addButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            _addButton.Click += (s, e) => AddField();

            _removeButton.Text = "Remove";
            _removeButton.SetBounds(12, 356, 380, 26);
            _removeButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left
                                    | System.Windows.Forms.AnchorStyles.Right;
            _removeButton.Click += (s, e) => RemoveSelectedField();

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

            LoadFields();
            RefreshListBox();
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
            string dir = Path.GetDirectoryName(ConfigPath);
            Directory.CreateDirectory(dir);

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
        // name, vs. the Summary tab field these actually serve), not a
        // harmless duplicate. See CLAUDE.md, Phase 4 (Issue 1) and
        // SwPropertyHandler/LegacyProperties.cs. Includes common label
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
    }
}
