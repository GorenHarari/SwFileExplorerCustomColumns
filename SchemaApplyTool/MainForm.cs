using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SchemaApplyTool
{
    public class MainForm : Form
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SwFileExplorerCustomColumns");

        private static readonly string FieldsPath = Path.Combine(ConfigDir, "fields.json");
        private static readonly string SchemaPath = Path.Combine(ConfigDir, "SwFileExplorerCustomColumns.propdesc");

        // Obviously-fake CLSID - nothing is registered under it, used only to
        // prove the repoint/revert mechanics without depending on a real
        // handler (Phase 3 doesn't exist yet). See CLAUDE.md, Phase 2.
        private const string PlaceholderClsid = "{00000000-0000-0000-0000-000000000000}";

        private readonly TextBox _log = new TextBox();
        private readonly Button _applySchemaButton = new Button();
        private readonly Button _testRepointButton = new Button();
        private readonly Button _revertButton = new Button();
        private readonly Label _fieldsLabel = new Label();

        public MainForm()
        {
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

            Text = $"SolidWorks Explorer Columns - Schema Apply Tool ({(isAdmin ? "Administrator" : "NOT ELEVATED")})";
            Width = 560;
            Height = 480;
            MinimumSize = new System.Drawing.Size(480, 360);

            _fieldsLabel.SetBounds(12, 12, 520, 60);
            _fieldsLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _fieldsLabel.Text = "(fields not loaded yet)";

            _applySchemaButton.Text = "Apply Schema";
            _applySchemaButton.SetBounds(12, 80, 160, 30);
            _applySchemaButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _applySchemaButton.Click += (s, e) => RunSafely(ApplySchema);

            _testRepointButton.Text = "Test Repoint (temporary)";
            _testRepointButton.SetBounds(182, 80, 180, 30);
            _testRepointButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _testRepointButton.Click += (s, e) => RunSafely(TestRepoint);

            _revertButton.Text = "Revert PropertyHandlers";
            _revertButton.SetBounds(372, 80, 160, 30);
            _revertButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _revertButton.Click += (s, e) => RunSafely(RevertPropertyHandlers);

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Font = new System.Drawing.Font("Consolas", 9f);
            _log.SetBounds(12, 118, 520, 300);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.Add(_fieldsLabel);
            Controls.Add(_applySchemaButton);
            Controls.Add(_testRepointButton);
            Controls.Add(_revertButton);
            Controls.Add(_log);

            if (!isAdmin)
            {
                AppendLog("WARNING: not running elevated - registry/schema calls will fail.");
            }

            LoadFieldsSummary();
        }

        private void RunSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppendLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void AppendLog(string message)
        {
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }

        private Dictionary<string, int> LoadFields()
        {
            if (!File.Exists(FieldsPath))
            {
                return new Dictionary<string, int>();
            }

            string json = File.ReadAllText(FieldsPath);
            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<Dictionary<string, int>>(json);
        }

        private void LoadFieldsSummary()
        {
            var fields = LoadFields();
            _fieldsLabel.Text = fields.Count == 0
                ? $"No fields found in {FieldsPath}"
                : $"{fields.Count} tracked field(s): {string.Join(", ", fields.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key))}";
        }

        private void ApplySchema()
        {
            LoadFieldsSummary();
            var fields = LoadFields();
            if (fields.Count == 0)
            {
                AppendLog("No tracked fields - nothing to register.");
                return;
            }

            if (File.Exists(SchemaPath))
            {
                int unregResult = NativeMethods.PSUnregisterPropertySchema(SchemaPath);
                AppendLog($"PSUnregisterPropertySchema (old schema) -> 0x{unregResult:X8}");
            }
            else
            {
                AppendLog("No existing schema file on disk - skipping unregister (first run).");
            }

            string xml = SchemaGenerator.BuildXml(fields);
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(SchemaPath, xml);
            AppendLog($"Wrote schema XML ({fields.Count} propert{(fields.Count == 1 ? "y" : "ies")}) to {SchemaPath}");

            int regResult = NativeMethods.PSRegisterPropertySchema(SchemaPath);
            AppendLog($"PSRegisterPropertySchema (new schema) -> 0x{regResult:X8}");

            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            AppendLog("Sent SHChangeNotify(SHCNE_ASSOCCHANGED).");

            if (regResult == 0)
            {
                AppendLog("Apply Schema finished - check List-ExplorerColumns.ps1 for the new column names.");
            }
        }

        private void TestRepoint()
        {
            AppendLog("--- Test Repoint: before ---");
            foreach (var kvp in PropertyHandlerRegistry.GetCurrentClsids())
            {
                AppendLog($"  {kvp.Key} -> {kvp.Value ?? "(none)"}");
            }

            foreach (string ext in PropertyHandlerRegistry.Extensions)
            {
                PropertyHandlerRegistry.SetClsid(ext, PlaceholderClsid);
            }

            AppendLog("--- Test Repoint: after (placeholder CLSID written) ---");
            foreach (var kvp in PropertyHandlerRegistry.GetCurrentClsids())
            {
                AppendLog($"  {kvp.Key} -> {kvp.Value ?? "(none)"}");
            }

            AppendLog("Description/OpenTime/LastSavedWith should now be BLANK for .sldprt files in Explorer. Click 'Revert PropertyHandlers' to restore.");
        }

        private void RevertPropertyHandlers()
        {
            AppendLog("--- Revert: before ---");
            foreach (var kvp in PropertyHandlerRegistry.GetCurrentClsids())
            {
                AppendLog($"  {kvp.Key} -> {kvp.Value ?? "(none)"}");
            }

            foreach (string ext in PropertyHandlerRegistry.Extensions)
            {
                PropertyHandlerRegistry.SetClsid(ext, PropertyHandlerRegistry.OriginalSolidWorksClsid);
            }

            AppendLog("--- Revert: after (original SolidWorks CLSID restored) ---");
            foreach (var kvp in PropertyHandlerRegistry.GetCurrentClsids())
            {
                AppendLog($"  {kvp.Key} -> {kvp.Value ?? "(none)"}");
            }

            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            AppendLog("Reverted. Description/OpenTime/LastSavedWith should work again.");
        }
    }
}
