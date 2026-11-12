// Minimal WinForms app used only by the automated tests: gives the UIA layer
// a predictable set of named controls (text fields, button, checkbox, combo,
// modal dialog) and a file-output button. ASCII-only on purpose (the bridge
// toolchain and PS 5.1 cannot be trusted with non-ASCII sources).
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

public class TestForm : Form
{
    public TextBox NameBox;
    public TextBox NotesBox;
    public Label Out;
    public CheckBox Chk;
    public ComboBox Cmb;
    public Button Btn;

    public TestForm()
    {
        Text = "RDP Test App";
        ClientSize = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Name = "frmMain";

        Label lbl = new Label();
        lbl.Text = "Name:";
        lbl.Left = 20; lbl.Top = 24; lbl.Width = 60;
        Controls.Add(lbl);

        NameBox = new TextBox();
        NameBox.Left = 90; NameBox.Top = 20; NameBox.Width = 300;
        NameBox.Name = "txtName";
        NameBox.AccessibleName = "Name input";
        Controls.Add(NameBox);

        Btn = new Button();
        Btn.Text = "Say Hello";
        Btn.Left = 410; Btn.Top = 18; Btn.Width = 110;
        Btn.Name = "btnHello";
        Btn.Click += delegate { Out.Text = "Hello, " + NameBox.Text + (Chk.Checked ? " [opt]" : "") + " / " + Cmb.Text; };
        Controls.Add(Btn);

        Chk = new CheckBox();
        Chk.Text = "Enable option";
        Chk.Left = 90; Chk.Top = 60; Chk.Width = 200;
        Chk.Name = "chkOption";
        Controls.Add(Chk);

        Cmb = new ComboBox();
        Cmb.Left = 300; Cmb.Top = 58; Cmb.Width = 220;
        Cmb.DropDownStyle = ComboBoxStyle.DropDownList;
        Cmb.Name = "cmbChoice";
        Cmb.AccessibleName = "Choice combo";
        Cmb.Items.AddRange(new object[] { "Alpha", "Beta", "Gamma" });
        Controls.Add(Cmb);

        NotesBox = new TextBox();
        NotesBox.Left = 20; NotesBox.Top = 100; NotesBox.Width = 720; NotesBox.Height = 250;
        NotesBox.Multiline = true;
        NotesBox.ScrollBars = ScrollBars.Vertical;
        NotesBox.Name = "txtNotes";
        NotesBox.AccessibleName = "Notes input";
        Controls.Add(NotesBox);

        Out = new Label();
        Out.Text = "(output)";
        Out.Left = 20; Out.Top = 362; Out.Width = 720; Out.Height = 30;
        Out.Name = "lblOut";
        Controls.Add(Out);

        Button open = new Button();
        open.Text = "Open Dialog";
        open.Left = 20; open.Top = 410; open.Width = 120;
        open.Name = "btnDialog";
        open.Click += delegate
        {
            using (DialogForm d = new DialogForm())
            {
                d.ShowDialog(this);
                Out.Text = "dialog:" + d.Result;
            }
        };
        Controls.Add(open);

        Button save = new Button();
        save.Text = "Save File";
        save.Left = 150; save.Top = 410; save.Width = 120;
        save.Name = "btnSave";
        save.Click += delegate
        {
            string p = Path.Combine(Path.GetTempPath(), "rdp-test-output.txt");
            File.WriteAllText(p, NotesBox.Text, Encoding.UTF8);
            Out.Text = "saved:" + p;
        };
        Controls.Add(save);

        Button quit = new Button();
        quit.Text = "Quit";
        quit.Left = 280; quit.Top = 410; quit.Width = 120;
        quit.Name = "btnQuit";
        quit.Click += delegate { Close(); };
        Controls.Add(quit);
    }
}

public class DialogForm : Form
{
    public string Result = "none";

    public DialogForm()
    {
        Text = "Test Dialog";
        ClientSize = new Size(340, 160);
        StartPosition = FormStartPosition.CenterParent;
        Name = "frmDialog";

        Label l = new Label();
        l.Text = "Confirm action?";
        l.Left = 20; l.Top = 24; l.Width = 300;
        Controls.Add(l);

        Button ok = new Button();
        ok.Text = "Dialog OK";
        ok.Left = 40; ok.Top = 90; ok.Width = 110;
        ok.Name = "btnDialogOk";
        ok.Click += delegate { Result = "ok"; Close(); };
        Controls.Add(ok);

        Button no = new Button();
        no.Text = "Cancel";
        no.Left = 180; no.Top = 90; no.Width = 110;
        no.Name = "btnDialogCancel";
        no.Click += delegate { Result = "cancel"; Close(); };
        Controls.Add(no);
    }
}

public class Program
{
    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new TestForm());
    }
}
