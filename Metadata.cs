using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BoothConfig
{
    [Flags]
    enum Tx { Const = 8, Numeric = 32, Hex = 64, Checklist = 128, Silent = 256, Multiline = 512, NoSave = 1024 }

    class MenuItemDef
    {
        public string Name;
        public List<string> Values = new List<string>(); // first is canonical, the rest are accepted aliases
        public override string ToString() => Name;
    }

    class MenuDef
    {
        public readonly List<MenuItemDef> Items = new List<MenuItemDef>();

        /// <summary>
        /// Entries are "name[,value]". Repeated names are extra stored spellings of one choice (Yes / Yes,1).
        /// Different names sharing one stored value cannot be told apart on disk, so they become one choice.
        /// </summary>
        public void Add(string name, string value)
        {
            var it = Items.FirstOrDefault(i => Same(i.Name, name));
            if (it != null) { if (!it.Values.Any(v => Same(v, value))) it.Values.Add(value); return; }
            it = Items.FirstOrDefault(i => i.Values.Any(v => Same(v, value)));
            if (it != null) { it.Name += " / " + name; return; }
            Items.Add(new MenuItemDef { Name = name, Values = { value } });
        }

        public MenuItemDef Find(string stored) =>
            Items.FirstOrDefault(i => i.Values.Any(v => Same(v, stored))) ??
            Items.FirstOrDefault(i => Same(i.Name, stored));

        /// <summary>What to write for a choice, keeping the 0/1 or lower-case convention already used in the file.</summary>
        public string ValueFor(MenuItemDef item, string previous)
        {
            if (!string.IsNullOrEmpty(previous))
            {
                if (previous.All(char.IsDigit))
                {
                    var num = item.Values.FirstOrDefault(v => v.Length > 0 && v.All(char.IsDigit));
                    if (num != null) return num;
                }
                var old = Find(previous);
                if (old != null && previous == previous.ToLowerInvariant() &&
                    old.Values.Any(v => Same(v, previous) && v != previous))
                    return item.Values[0].ToLowerInvariant();
            }
            return item.Values[0];
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    class FieldDef
    {
        public string Name, Label, Suffix = "", Default, MenuName, Help, FullHelp, OnExit;
        public int Width, Type;
        public Tx Flags;
        public bool Hidden;
        public MenuDef Menu;
        public bool IsConst => (Flags & Tx.Const) != 0;
    }

    /// <summary>A row on a form: either a field, or fixed text.</summary>
    class RowDef { public FieldDef Field; public string Label, Text; }
    class GroupDef { public string Heading; public readonly List<RowDef> Rows = new List<RowDef>(); }
    class PageDef { public string Title; public readonly List<GroupDef> Groups = new List<GroupDef>(); }

    /// <summary>Everything nsyscfg.ini says about the configuration screens.</summary>
    class Metadata
    {
        const int HiddenRow = 1000; // "non-selectable fields starting on line 1000, ie. not visible"
        readonly Ini ini;
        public readonly List<PageDef> Pages = new List<PageDef>();
        public readonly List<FieldDef> Fields = new List<FieldDef>();
        readonly Dictionary<string, MenuDef> menus = new Dictionary<string, MenuDef>(StringComparer.OrdinalIgnoreCase);

        public Metadata(Ini ini)
        {
            this.ini = ini;
            var forms = new List<string>();
            foreach (var s in ini.Names)
                foreach (var p in ini.Pairs(s))
                    if (p.Key == "form")
                    {
                        var parts = Split(p.Value);
                        if (parts.Length > 4 && ini.Has(parts[4]) && !forms.Contains(parts[4])) forms.Add(parts[4]);
                    }
            if (forms.Count == 0)
                forms.AddRange(ini.Names.Where(s => ini.Pairs(s).Any(p => p.Key == "field")));
            foreach (var f in forms) ParseForm(f);
            if (Fields.Count == 0)
                throw new FormatException("nsyscfg.ini does not define any configuration fields.");
        }

        public FieldDef Field(string name) => Fields.FirstOrDefault(f => f.Name == name);

        public MenuDef Menu(string name)
        {
            if (string.IsNullOrEmpty(name) || !ini.Has(name)) return null;
            if (menus.TryGetValue(name, out var m)) return m;
            m = new MenuDef();
            foreach (var p in ini.Pairs(name))
            {
                if (p.Key != "entry") continue;
                int c = p.Value.IndexOf(',');
                var label = (c < 0 ? p.Value : p.Value.Substring(0, c)).Trim();
                m.Add(label, c < 0 ? label : p.Value.Substring(c + 1).Trim());
            }
            return menus[name] = m;
        }

        class Elem { public int X, Y; public string Text; public FieldDef Field; }

        void ParseForm(string section)
        {
            var page = new PageDef { Title = (ini.Get(section, "title") ?? section).Trim(' ', '[', ']') };
            var elems = new List<Elem>();
            int tx = 0, ty = 0, fx = 0, fy = 0;  // texts and fields each keep their own "last position"
            string spareLabel = null;            // hidden fields often have their label commented out
            foreach (var line in ini.Lines(section))
            {
                var cm = Regex.Match(line, @"^[;#]+\s*(text|field)\s*=\s*(.*)$");
                if (cm.Success)
                {
                    var a = Split(cm.Groups[2].Value);
                    if (cm.Groups[1].Value == "field") spareLabel = null;
                    else if (a.Length > 4 && a[4].Contains("..")) spareLabel = Clean(a[4]);
                    continue;
                }
                if (!Ini.TrySplit(line, out var key, out var val)) continue;
                var p = Split(val);
                if (key == "text" && p.Length > 4)
                {
                    tx = Pos(p[0], tx); ty = Pos(p[1], ty);
                    elems.Add(new Elem { X = tx, Y = ty, Text = p[4] });
                    spareLabel = null;
                }
                else if (key == "field" && p.Length > 6)
                {
                    fx = Pos(p[0], fx); fy = Pos(p[1], fy);
                    var f = MakeField(p);
                    f.Label = spareLabel;
                    f.Hidden = fy >= HiddenRow;
                    spareLabel = null;
                    if (f.Name.StartsWith("__") || (f.Flags & Tx.NoSave) != 0) continue;
                    elems.Add(new Elem { X = fx, Y = fy, Field = f });
                }
            }

            GroupDef group = null;
            foreach (var row in elems.GroupBy(e => e.Y).OrderBy(g => g.Key))
            {
                var fields = row.Where(e => e.Field != null).OrderBy(e => e.X).ToList();
                var texts = row.Where(e => e.Field == null).OrderBy(e => e.X).ToList();
                var rows = new List<RowDef>();
                foreach (var fe in fields)
                {
                    var label = texts.LastOrDefault(t => t.X < fe.X);
                    if (label != null) { fe.Field.Label = Clean(label.Text); texts.Remove(label); }
                    if (fe == fields.Last())
                    {
                        var suffix = texts.FirstOrDefault(t => t.X > fe.X);
                        if (suffix != null) { fe.Field.Suffix = Clean(suffix.Text); texts.Remove(suffix); }
                    }
                    if (string.IsNullOrEmpty(fe.Field.Label)) fe.Field.Label = fe.Field.Name;
                    if (Field(fe.Field.Name) != null) continue; // first definition wins
                    Fields.Add(fe.Field);
                    if (!fe.Field.Hidden) rows.Add(new RowDef { Field = fe.Field, Label = fe.Field.Label });
                }
                if (row.Key >= HiddenRow) continue;
                string heading = null;
                if (fields.Count > 0 || texts.Count == 1)
                {
                    if (texts.Count > 0) heading = Clean(texts[0].Text); // text left over beside a field names the group
                }
                else if (texts.Count > 1)
                    rows.Add(new RowDef
                    {
                        Label = Clean(texts[0].Text),
                        Text = string.Join("  ", texts.Skip(1).Select(t => t.Text.Trim()))
                    });

                if (heading != null || (group == null && rows.Count > 0))
                    page.Groups.Add(group = new GroupDef { Heading = heading ?? page.Title });
                if (rows.Count > 0) group.Rows.AddRange(rows);
            }
            if (page.Groups.Count > 0) Pages.Add(page);
        }

        // x, y, dx, dy, string, flags, name, type, menuname, help, fullhelp, onexitscript
        FieldDef MakeField(string[] p)
        {
            string At(int i) => i < p.Length ? p[i] : "";
            var f = new FieldDef
            {
                Width = Int(At(2)), Default = At(4), Flags = (Tx)Int(At(5)), Name = At(6),
                Type = Int(At(7)), MenuName = At(8)
            };
            f.Menu = Menu(f.MenuName);
            // help is free text, so anything after it that is neither a help section nor a script belongs to it
            var help = new List<string> { At(9) };
            for (int i = 10; i < p.Length; i++)
            {
                if (p[i].Length == 0) continue;
                if (ini.Has(p[i]))
                    f.FullHelp = string.Join(Environment.NewLine,
                        ini.Lines(p[i]).Where(l => l.StartsWith(">")).Select(l => l.Substring(1).Trim()));
                else if (p[i].StartsWith("/")) f.OnExit = p[i];
                else help.Add(p[i]);
            }
            f.Help = GuiHelp(string.Join(", ", help.Where(h => h.Length > 0)));
            return f;
        }

        /// <summary>Reword the keyboard instructions of the text-mode tool.</summary>
        static string GuiHelp(string s)
        {
            const string keys = @"^(Press|Use|User) (<F2>|the space bar) ";
            s = Regex.Replace(s, keys + @"for (\w+ )*list\.?", "Choose from the list.");
            s = Regex.Replace(s, keys + @"to (pick|select|toggle)\b", "Choose");
            s = Regex.Replace(s, keys + @"to (\w)", m => m.Groups[3].Value.ToUpperInvariant());
            return s;
        }

        static string[] Split(string v) => v.Split(',').Select(s => s.Trim()).ToArray();
        static string Clean(string s) => s.Trim().TrimEnd('.', ':', ' ').Replace('`', '\'');
        static int Int(string s) => int.TryParse(s, out var n) ? n : 0;

        // -1 means "+1 from the last one", -2 means "+2"; -99 stays on the same line
        static int Pos(string s, int last)
        {
            if (!int.TryParse(s, out int n)) return last;
            return n >= 0 ? n : n == -99 ? last : last - n;
        }
    }

    class Price
    {
        public string Name;
        public int[] Costs; // play, foto1..3, foto1..3 alt; -1 = not offered
        public override string ToString() => Name;
    }

    class CurrencySet
    {
        public string Name, CurrencyFormat, DateFormat, TokenSet;
        public bool Defined;
        public List<string> Coins, Bills;
        public readonly List<Price> Prices = new List<Price>();

        /// <summary>Token values may be amounts, or sums of cost codes such as PF1 (see the notes in locale.ini).</summary>
        public List<string> Tokens(Price price)
        {
            var result = new List<string>();
            if (TokenSet == null) return result;
            string[] codes = { "P", "F1", "F2", "F3", "A1", "A2", "A3" };
            foreach (var t in TokenSet.Split(',').Select(s => s.Trim()))
            {
                if (Regex.IsMatch(t, @"^(P|F[1-3]|A[1-3])+$"))
                {
                    var costs = Regex.Matches(t, "P|F[1-3]|A[1-3]").Cast<Match>()
                        .Select(m => price == null ? -1 : price.Costs[Array.IndexOf(codes, m.Value)])
                        .Where(c => c >= 0).ToList();
                    result.Add(costs.Count == 0 ? "-1" : costs.Sum().ToString());
                }
                else result.Add(int.TryParse(t, out var n) ? n.ToString() : "-1");
            }
            return result;
        }
    }

    /// <summary>The parts of locale.ini that feed the menus: currency sets, their price lists, and languages.</summary>
    class Locale
    {
        readonly Ini ini;
        public readonly List<string> Countries = new List<string>();

        public Locale(Ini ini)
        {
            this.ini = ini;
            if (ini == null) return;
            foreach (var p in ini.Pairs("currency_sets"))
                if (Regex.IsMatch(p.Key, @"^cs\d+$") && p.Value.Length > 0) Countries.Add(p.Value);
        }

        public MenuDef Languages()
        {
            if (ini == null) return null;
            var m = new MenuDef();
            foreach (var p in ini.Pairs("languages"))
                if (p.Key == "lang")
                {
                    var a = p.Value.Split(',');
                    if (a.Length > 1) m.Add(a[0].Trim(), a[1].Trim());
                }
            return m.Items.Count > 0 ? m : null;
        }

        public CurrencySet Find(string name)
        {
            var cs = new CurrencySet { Name = name, Coins = new List<string>(), Bills = new List<string>() };
            if (ini == null || string.IsNullOrEmpty(name) || !ini.Has(name)) return cs;
            cs.Defined = true;
            cs.CurrencyFormat = ini.Get(name, "currency_format");
            cs.DateFormat = ini.Get(name, "date_format");
            cs.TokenSet = ini.Get(name, "token_set");
            cs.Coins = List(ini.Get(name, "coin_set"));
            cs.Bills = List(ini.Get(name, "bill_set"));
            foreach (var p in ini.Pairs(name))
            {
                if (!Regex.IsMatch(p.Key, @"^vp\d+$")) continue;
                var a = p.Value.Split(',').Select(s => s.Trim()).ToArray();
                var costs = Enumerable.Range(1, 7)
                    .Select(i => i < a.Length && int.TryParse(a[i], out var n) ? n : -1).ToArray();
                if (a[0].Length > 0) cs.Prices.Add(new Price { Name = a[0], Costs = costs });
            }
            return cs;
        }

        static List<string> List(string s) =>
            s == null ? new List<string>() : s.Split(',').Select(v => v.Trim()).ToList();
    }
}
