using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BoothConfig
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // a folder on the command line, else the booth folder the program was started in
            string folder = args.Length > 0 ? Booth.Resolve(args[0]) : null;
            if (folder == null)
                folder = Booth.FindAround(AppDomain.CurrentDomain.BaseDirectory)
                         ?? Booth.FindAround(Environment.CurrentDirectory);
            Application.Run(new MainForm(folder));
        }
    }

    class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(0, 90, 158);
        readonly float scale;
        readonly Label pathLabel, statusLabel;
        readonly Button openButton, saveButton, revertButton;
        readonly Panel body;
        readonly ErrorProvider errors = new ErrorProvider { BlinkStyle = ErrorBlinkStyle.NeverBlink };
        readonly List<Editor> editors = new List<Editor>();
        Booth booth;
        Label moneyLabel;
        Font bold;
        Action refreshGrid;

        int S(int px) => (int)Math.Round(px * scale);

        public MainForm(string folder)
        {
            Font = new Font("Segoe UI", 9f);
            bold = new Font(Font, FontStyle.Bold);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Booth Config";
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(980), area.Width), Math.Min(S(780), area.Height));
            MinimumSize = new Size(S(680), S(420));
            errors.ContainerControl = this;

            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(S(8)),
                BackColor = SystemColors.Control
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            openButton = MakeButton("&Open booth folder…", "open", (s, e) => PickFolder());
            revertButton = MakeButton("&Revert", "revert", (s, e) => Revert());
            saveButton = MakeButton("&Save", "save", (s, e) => SaveChanges());
            pathLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Name = "path" };
            bar.Controls.Add(openButton, 0, 0);
            bar.Controls.Add(pathLabel, 1, 0);
            bar.Controls.Add(revertButton, 2, 0);
            bar.Controls.Add(saveButton, 3, 0);

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = S(26), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(8), 0, 0, 0), BackColor = SystemColors.Control, AutoEllipsis = true, Name = "status"
            };
            body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(8), 0, S(8), 0) };
            Controls.Add(body);
            Controls.Add(bar);
            Controls.Add(statusLabel);

            ShowWelcome();
            UpdateState();
            if (folder != null) Shown += (s, e) => LoadBooth(folder);
        }

        Button MakeButton(string text, string name, EventHandler click)
        {
            var b = new Button
            {
                Text = text, Name = name, AutoSize = true, MinimumSize = new Size(S(90), S(30)),
                FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Left
            };
            b.Click += click;
            return b;
        }

        // ---- loading ----

        void ShowWelcome()
        {
            ClearBody();
            var l = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText,
                Text = "No booth loaded.\n\nChoose “Open booth folder…” and pick the folder that contains\n" +
                       Booth.MetaName + " and " + Booth.CfgName + " (usually named “vend”)."
            };
            body.Controls.Add(l);
        }

        void ClearBody()
        {
            editors.Clear();
            errors.Clear();
            moneyLabel = null;
            refreshGrid = null;
            while (body.Controls.Count > 0) body.Controls[0].Dispose();
        }

        void PickFolder()
        {
            if (!ConfirmDiscard()) return;
            string picked;
            try { picked = FolderPicker.Show(this, "Pick the folder that contains the booth data", booth?.Folder); }
            catch (Exception ex) { Fail("The folder dialog could not be shown.", ex); return; }
            if (picked == null) return;
            var folder = Booth.Resolve(picked);
            if (folder == null)
            {
                MessageBox.Show(this, "There is no " + Booth.MetaName + " in this folder, so it does not look like booth data:\n\n" + picked,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LoadBooth(folder);
        }

        void LoadBooth(string folder)
        {
            Booth next;
            try { next = new Booth(folder); }
            catch (Exception ex) { Fail("The booth data could not be read:\n" + folder, ex); return; }
            booth = next;
            BuildPages();
            UpdateState();
            if (booth.LoadedFrom == null)
                Status("No saved configuration here yet. Showing defaults; saving will create " + Booth.CfgName + ".");
            else if (booth.LoadedFrom != Booth.CfgName)
                Status(Booth.CfgName + " is missing. Loaded " + booth.LoadedFrom + " instead; saving will create " + Booth.CfgName + ".");
            else Status("Loaded " + Booth.CfgName + ".");
        }

        void Revert()
        {
            if (booth == null || !ConfirmDiscard()) return;
            LoadBooth(booth.Folder);
        }

        // ---- pages ----

        void BuildPages()
        {
            int tab = 0, scroll = 0;
            if (body.Controls.Count > 0 && body.Controls[0] is TabControl old)
            {
                tab = old.SelectedIndex;
                if (old.SelectedTab?.Controls.Count > 0 && old.SelectedTab.Controls[0] is Panel p) scroll = -p.AutoScrollPosition.Y;
            }
            SuspendLayout();
            ClearBody();
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(10), S(5)), Name = "tabs" };
            foreach (var page in booth.Meta.Pages) tabs.TabPages.Add(MakePage(page.Title, page.Groups, null));

            var hidden = booth.Meta.Fields.Where(f => f.Hidden).ToList();
            if (hidden.Count > 0)
            {
                var g = new GroupDef { Heading = "Hidden fields" };
                g.Rows.AddRange(hidden.Select(f => new RowDef { Field = f, Label = f.Label }));
                tabs.TabPages.Add(MakePage("Hidden Fields", new List<GroupDef> { g },
                    "The booth's own tool stores these but never shows them. Prices and currency follow the Money settings."));
            }
            tabs.TabPages.Add(MakeOtherPage());
            body.Controls.Add(tabs);
            ResumeLayout(true);

            RefreshEditors();
            if (tab < tabs.TabCount) tabs.SelectedIndex = tab;
            if (scroll > 0 && tabs.SelectedTab.Controls[0] is Panel np) np.AutoScrollPosition = new Point(0, scroll);
        }

        TabPage MakePage(string title, List<GroupDef> groups, string note)
        {
            var page = new TabPage(title) { BackColor = SystemColors.Window };
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(12), S(4), S(12), S(12)) };
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(175)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(300)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var own = new List<Editor>();

            foreach (var g in groups)
            {
                AddWide(table, new Label
                {
                    Text = g.Heading, AutoSize = true, ForeColor = Accent, Margin = new Padding(0, S(12), 0, S(4)),
                    Font = new Font(Font.FontFamily, 11.5f, FontStyle.Bold)
                });
                if (note != null)
                {
                    AddWide(table, new Label { Text = note, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(S(3), 0, 0, S(4)) });
                    var unlock = new CheckBox { Text = "Allow editing", AutoSize = true, Name = "unlockHidden" };
                    unlock.CheckedChanged += (s, e) => { foreach (var ed in own) ed.Control.Enabled = unlock.Checked; };
                    AddWide(table, unlock);
                }
                foreach (var r in g.Rows)
                {
                    if (r.Field == null) { AddRow(table, MakeCaption(r.Label), new Label { Text = r.Text, AutoSize = true, Margin = new Padding(S(3), S(6), 0, S(6)) }, null); continue; }
                    var ed = Editor.Create(booth, r.Field);
                    ed.Caption = MakeCaption(r.Field.Suffix.Length > 0 ? r.Label + " (" + r.Field.Suffix + ")" : r.Label);
                    ed.HelpLabel = MakeHelp(r.Field);
                    ed.Control.Dock = DockStyle.Top;
                    ed.Control.Margin = new Padding(S(3), S(3), S(18), S(3));
                    ed.Control.Enabled = !r.Field.IsConst && r.Field.Type != 4;
                    ed.Changed += RefreshEditors;
                    ed.Control.Enter += (s, e) => Status(r.Field.Help + (r.Field.FullHelp != null ? "   F1 = more help" : ""));
                    AddRow(table, ed.Caption, ed.Control, ed.HelpLabel);
                    editors.Add(ed);
                    own.Add(ed);
                }
                if (moneyLabel == null && g.Rows.Any(r => r.Field != null && r.Field.Name == "country"))
                {
                    moneyLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(S(3), S(4), 0, S(4)), Name = "money" };
                    AddRow(table, null, moneyLabel, null);
                    table.SetColumnSpan(moneyLabel, 2);
                }
            }
            scroller.Controls.Add(table);
            page.Controls.Add(scroller);
            return page;
        }

        Label MakeCaption(string text) => new Label
        {
            Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(S(3), S(6), S(3), S(6))
        };

        Label MakeHelp(FieldDef f)
        {
            var text = f.Help;
            if (f.FullHelp != null) text += "  (F1: more)";
            if (f.OnExit != null) text += "  Note: on the booth, changing this also runs " + f.OnExit + ", which this program does not do.";
            return new Label
            {
                Text = text.Trim(), AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
                ForeColor = SystemColors.GrayText, Margin = new Padding(S(3), S(6), S(3), S(6))
            };
        }

        static void AddRow(TableLayoutPanel t, Control a, Control b, Control c)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (a != null) t.Controls.Add(a, 0, row);
            if (b != null) t.Controls.Add(b, 1, row);
            if (c != null) t.Controls.Add(c, 2, row);
        }

        static void AddWide(TableLayoutPanel t, Control c)
        {
            AddRow(t, c, null, null);
            t.SetColumnSpan(c, 3);
        }

        /// <summary>Everything in vsystem.cfg that nsyscfg.ini does not describe.</summary>
        TabPage MakeOtherPage()
        {
            var page = new TabPage("Other Settings") { BackColor = SystemColors.Window };
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(S(12), S(8), S(12), S(8)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(220)));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var filter = new TextBox { Dock = DockStyle.Fill, Name = "filter", AccessibleName = "Filter" };
            var unlock = new CheckBox { Text = "Allow editing", AutoSize = true, Name = "unlockOther", Margin = new Padding(S(16), S(4), 0, 0) };
            var note = new Label
            {
                Text = "Values in " + Booth.CfgName + " that " + Booth.MetaName + " does not describe. Most are set by the booth software or its product setup.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(S(3), 0, 0, S(6))
            };
            top.Controls.Add(note, 0, 0);
            top.SetColumnSpan(note, 3);
            top.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Margin = new Padding(S(3), S(6), S(3), 0) }, 0, 1);
            top.Controls.Add(filter, 1, 1);
            top.Controls.Add(unlock, 2, 1);

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Name = "grid",
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            grid.RowTemplate.Height = S(24);
            grid.Columns.Add("key", "Setting");
            grid.Columns.Add("value", "Value");
            grid.Columns[0].FillWeight = 35;
            grid.Columns[1].FillWeight = 65;
            foreach (var key in booth.Keys().Where(k => booth.Meta.Field(k) == null)) grid.Rows.Add(key, booth.Get(key));

            unlock.CheckedChanged += (s, e) => { grid.ReadOnly = !unlock.Checked; grid.Columns[0].ReadOnly = true; };
            filter.TextChanged += (s, e) =>
            {
                grid.EndEdit();
                grid.CurrentCell = null;
                foreach (DataGridViewRow r in grid.Rows)
                    r.Visible = filter.Text.Length == 0 || new[] { r.Cells[0].Value, r.Cells[1].Value }.Any(v =>
                        ((string)v ?? "").IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0);
            };
            bool loading = false;
            grid.CellValueChanged += (s, e) =>
            {
                if (loading || e.RowIndex < 0 || e.ColumnIndex != 1) return;
                booth.Set((string)grid[0, e.RowIndex].Value, ((string)grid[1, e.RowIndex].Value ?? "").Trim());
                RefreshEditors();
            };
            refreshGrid = () =>
            {
                loading = true;
                foreach (DataGridViewRow r in grid.Rows)
                {
                    var key = (string)r.Cells[0].Value;
                    var v = booth.Get(key) ?? "";
                    if ((string)r.Cells[1].Value != v) r.Cells[1].Value = v;
                    bool edited = booth.IsEdited(key);
                    r.DefaultCellStyle.Font = edited ? bold : null;
                    r.DefaultCellStyle.ForeColor = edited ? Accent : Color.Empty;
                    r.Cells[1].ErrorText = edited ? Booth.Problem(v, null) ?? "" : "";
                }
                loading = false;
            };
            page.Controls.Add(grid);
            page.Controls.Add(top);
            return page;
        }

        void RefreshEditors()
        {
            foreach (var ed in editors)
            {
                ed.RefreshFromBooth();
                ed.Caption.Font = ed.IsEdited ? bold : Font;
                ed.Caption.ForeColor = ed.IsEdited ? Accent : SystemColors.ControlText;
                errors.SetError(ed.Control, ed.Problem ?? "");
            }
            refreshGrid?.Invoke();
            if (moneyLabel != null) moneyLabel.Text = MoneyText();
            UpdateState();
        }

        string MoneyText()
        {
            var country = booth.Get("country");
            var cs = booth.Currency();
            if (!cs.Defined)
                return string.IsNullOrEmpty(country) ? "No currency set is selected."
                    : Booth.LocaleName + " has no data for “" + country + "”, so prices and coin values are left as they are.";
            var text = "Price to start: " + Amount("play_cost");
            string[] names = { "foto1_cost", "first print adds", "foto2_cost", "second print", "foto3_cost", "third print",
                "foto1_alt_cost", "premium first", "foto2_alt_cost", "premium second", "foto3_alt_cost", "premium third" };
            for (int i = 0; i < names.Length; i += 2)
                if (int.TryParse(booth.Get(names[i]), out var n) && n >= 0) text += ", " + names[i + 1] + " " + Amount(names[i]);
            text += "\nCoins: " + Values("coinvals") + "     Bills: " + Values("billvals") + "     Tokens: " + Values("tokenvals");
            return text + "\nThese amounts come from " + Booth.LocaleName + " and are saved with the vend type.";
        }

        string Amount(string key) => booth.Get(key) ?? "not set";

        string Values(string key)
        {
            var vals = (booth.Get(key) ?? "").Split(',').Where(v => v.Trim().Length > 0 && v.Trim() != "-1").ToArray();
            return vals.Length == 0 ? "none" : string.Join(", ", vals);
        }

        // ---- saving ----

        bool CanSave => booth != null && (booth.Dirty || booth.LoadedFrom != Booth.CfgName);

        bool SaveChanges()
        {
            if (!CanSave) return true;
            Validate(); // commit a grid cell that is still being edited
            var bad = booth.Changes().Select(c => new { c.Key, Why = Booth.Problem(c.New, booth.Meta.Field(c.Key)) })
                .Where(x => x.Why != null).ToList();
            if (bad.Count > 0)
            {
                MessageBox.Show(this, "Please correct these values first:\n\n" +
                    string.Join("\n", bad.Select(b => LabelOf(b.Key) + ": " + b.Why)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!ConfirmChanges()) return false;
            if (booth.ChangedOnDisk() && MessageBox.Show(this,
                    Booth.CfgName + " was changed by another program since it was loaded (is the booth running?).\n\n" +
                    "Apply your changes to the newer file? Its other values will be kept.",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
            try { booth.Save(); }
            catch (Exception ex) { Fail("The configuration could not be saved. Your changes are still here.", ex); return false; }
            LoadBooth(booth.Folder);
            Status("Saved. The previous file is in " + Booth.BackupDir + ". Restart the booth program if it is running.");
            return true;
        }

        string LabelOf(string key)
        {
            var f = booth.Meta.Field(key);
            return f == null || f.Label == key ? key : f.Label + " (" + key + ")";
        }

        bool ConfirmChanges()
        {
            using (var dlg = new Form
            {
                Text = "Save changes", Font = Font, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
                MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(S(680), S(380)), AutoScaleMode = AutoScaleMode.None
            })
            {
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, Name = "changes", ShowItemToolTips = true };
                list.Columns.Add("Setting", S(280));
                list.Columns.Add("From", S(185));
                list.Columns.Add("To", S(185));
                foreach (var c in booth.Changes())
                    list.Items.Add(new ListViewItem(new[] { LabelOf(c.Key), c.Old ?? "(not set)", string.IsNullOrEmpty(c.New) ? "(blank)" : c.New }));
                var head = new Label
                {
                    Dock = DockStyle.Top, Height = S(34), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, 0, 0),
                    Text = booth.Dirty ? "These values will be written to " + Booth.CfgName + " and " + Booth.AltName + ":"
                        : Booth.CfgName + " will be created."
                };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(6)) };
                var cancel = MakeButton("Cancel", "cancelSave", null);
                cancel.DialogResult = DialogResult.Cancel;
                var ok = MakeButton("Save", "confirmSave", null);
                ok.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                dlg.Controls.Add(list);
                dlg.Controls.Add(head);
                dlg.Controls.Add(buttons);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                return dlg.ShowDialog(this) == DialogResult.OK;
            }
        }

        bool ConfirmDiscard()
        {
            if (booth == null || !booth.Dirty) return true;
            return MessageBox.Show(this, "Discard your unsaved changes?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || booth == null || !booth.Dirty || e.CloseReason == CloseReason.WindowsShutDown) return;
            var r = MessageBox.Show(this, "Save your changes before closing?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel || (r == DialogResult.Yes && !SaveChanges())) e.Cancel = true;
        }

        // ---- misc ----

        void UpdateState()
        {
            int n = booth?.Changes().Count ?? 0;
            saveButton.Enabled = CanSave;
            revertButton.Enabled = n > 0;
            pathLabel.Text = booth == null ? "" : booth.Folder + (n > 0 ? "   (" + n + " unsaved change" + (n == 1 ? ")" : "s)") : "");
            Text = "Booth Config" + (booth == null ? "" : " - " + booth.Folder) + (n > 0 ? " *" : "");
        }

        void Status(string s) => statusLabel.Text = s.Trim();

        void Fail(string what, Exception ex) =>
            MessageBox.Show(this, what + "\n\n" + ex.Message, "Booth Config", MessageBoxButtons.OK, MessageBoxIcon.Error);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.O: PickFolder(); return true;
                case Keys.Control | Keys.S: SaveChanges(); return true;
                case Keys.F1:
                    var ed = editors.FirstOrDefault(x => x.Control.ContainsFocus);
                    if (ed == null) break;
                    var f = ed.Field;
                    var help = f.FullHelp ?? (f.Help.Length > 0 ? f.Help : "No help is available for this field.");
                    MessageBox.Show(this, help + "\n\nStored as: " + f.Name, f.Label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
