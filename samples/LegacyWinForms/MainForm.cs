using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace LegacyWinForms
{
    public partial class MainForm : Form
    {
        private int _clicks;

        public MainForm()
        {
            InitializeComponent();
#if LEGACY_SAMPLE
            lblRuntime.Text = "Runtime: " + Environment.Version + " (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit)";
#endif
            Shown += (s, e) => Console.WriteLine("CROSSWIN_READY legacy WinForms window shown");
        }

        private void btnCount_Click(object sender, EventArgs e)
        {
            _clicks++;
            txtJson.Text = JsonConvert.SerializeObject(new Dictionary<string, object> { { "clicks", _clicks }, { "note", txtNote.Text } });
        }
    }
}
