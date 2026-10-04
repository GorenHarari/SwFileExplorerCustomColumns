namespace SwColumnManager
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this._listBox = new System.Windows.Forms.ListBox();
            this._textBox = new System.Windows.Forms.TextBox();
            this._addButton = new System.Windows.Forms.Button();
            this._removeButton = new System.Windows.Forms.Button();
            this._applyButton = new System.Windows.Forms.Button();
            this._uninstallButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // _listBox
            //
            this._listBox.SetBounds(12, 12, 440, 260);
            this._listBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom
                | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            //
            // _textBox
            //
            this._textBox.SetBounds(12, 282, 340, 24);
            this._textBox.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left
                | System.Windows.Forms.AnchorStyles.Right;
            this._textBox.HandleCreated += this.TextBox_HandleCreated;
            this._textBox.KeyDown += this.TextBox_KeyDown;
            //
            // _addButton
            //
            this._addButton.Text = "Add";
            this._addButton.SetBounds(360, 281, 92, 26);
            this._addButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            this._addButton.Click += this.AddButton_Click;
            //
            // _removeButton
            //
            this._removeButton.Text = "Remove";
            this._removeButton.SetBounds(12, 316, 440, 26);
            this._removeButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left
                | System.Windows.Forms.AnchorStyles.Right;
            this._removeButton.Click += this.RemoveButton_Click;
            //
            // _applyButton
            //
            this._applyButton.Text = "Apply Changes";
            this._applyButton.SetBounds(12, 352, 214, 32);
            this._applyButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left;
            this._applyButton.BackColor = System.Drawing.Color.LightYellow;
            this._applyButton.Click += this.ApplyButton_Click;
            //
            // _uninstallButton
            //
            this._uninstallButton.Text = "Uninstall";
            this._uninstallButton.SetBounds(238, 352, 214, 32);
            this._uninstallButton.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            this._uninstallButton.BackColor = System.Drawing.Color.MistyRose;
            this._uninstallButton.Click += this.UninstallButton_Click;
            //
            // MainForm
            //
            this.Text = "SolidWorks Explorer Columns";
            this.Width = 480;
            this.Height = 430;
            this.MinimumSize = new System.Drawing.Size(420, 320);
            this.Controls.Add(this._listBox);
            this.Controls.Add(this._textBox);
            this.Controls.Add(this._addButton);
            this.Controls.Add(this._removeButton);
            this.Controls.Add(this._applyButton);
            this.Controls.Add(this._uninstallButton);
            this.Shown += this.MainForm_Shown;
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.ListBox _listBox;
        private System.Windows.Forms.TextBox _textBox;
        private System.Windows.Forms.Button _addButton;
        private System.Windows.Forms.Button _removeButton;
        private System.Windows.Forms.Button _applyButton;
        private System.Windows.Forms.Button _uninstallButton;
    }
}
