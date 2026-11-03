namespace LegacyWinForms
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.lblRuntime = new System.Windows.Forms.Label();
            this.txtNote = new System.Windows.Forms.TextBox();
            this.btnCount = new System.Windows.Forms.Button();
            this.txtJson = new System.Windows.Forms.TextBox();
            this.SuspendLayout();
            this.lblRuntime.AutoSize = true;
            this.lblRuntime.Location = new System.Drawing.Point(16, 16);
            this.lblRuntime.Name = "lblRuntime";
            this.lblRuntime.Text = "Runtime: ?";
            this.txtNote.Location = new System.Drawing.Point(16, 44);
            this.txtNote.Name = "txtNote";
            this.txtNote.Size = new System.Drawing.Size(260, 23);
            this.btnCount.Location = new System.Drawing.Point(288, 42);
            this.btnCount.Name = "btnCount";
            this.btnCount.Size = new System.Drawing.Size(110, 27);
            this.btnCount.Text = "Serialize";
            this.btnCount.Click += new System.EventHandler(this.btnCount_Click);
            this.txtJson.Location = new System.Drawing.Point(16, 84);
            this.txtJson.Name = "txtJson";
            this.txtJson.ReadOnly = true;
            this.txtJson.Size = new System.Drawing.Size(382, 23);
            this.ClientSize = new System.Drawing.Size(416, 128);
            this.Controls.Add(this.lblRuntime);
            this.Controls.Add(this.txtNote);
            this.Controls.Add(this.btnCount);
            this.Controls.Add(this.txtJson);
            this.Name = "MainForm";
            this.Text = resources.GetString("$this.Text");
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.Label lblRuntime;
        private System.Windows.Forms.TextBox txtNote;
        private System.Windows.Forms.Button btnCount;
        private System.Windows.Forms.TextBox txtJson;
    }
}
