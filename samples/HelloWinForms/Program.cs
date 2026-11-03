using System;
using System.Drawing;
using System.Windows.Forms;

namespace HelloWinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "CrossWin WinForms sample", ClientSize = new Size(420, 220) };
        var label = new Label { Text = "Clicks: 0", AutoSize = true, Location = new Point(20, 20), Font = new Font("Segoe UI", 14) };
        var button = new Button { Text = "Click me", Location = new Point(20, 70), Size = new Size(120, 36) };
        var box = new TextBox { Location = new Point(20, 120), Width = 360, PlaceholderText = "Type here" };
        int clicks = 0;
        button.Click += (_, _) => label.Text = $"Clicks: {++clicks}";
        form.Controls.AddRange(new Control[] { label, button, box });
        form.Shown += (_, _) => Console.WriteLine("CROSSWIN_READY WinForms window shown");
        Application.Run(form);
    }
}
