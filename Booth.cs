using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BoothConfig
{
    class Change { public string Key, Old, New; }

    /// <summary>One booth folder: its metadata, its saved values, and the edits not written yet.</summary>
    class Booth
    {
        public const string MetaName = "nsyscfg.ini", CfgName = "vsystem.cfg", AltName = "vsystem.alt",
            FactoryName = "zsystem.cfg", LocaleName = "locale.ini", BackupDir = "BoothConfig-backups";

        static readonly string[] CostKeys =
            { "play_cost", "foto1_cost", "foto2_cost", "foto3_cost", "foto1_alt_cost", "foto2_alt_cost", "foto3_alt_cost" };
        static readonly string[] CountryKeys = { "currency_format", "date_format", "coinvals", "billvals" };

        public readonly string Folder;
        public readonly Metadata Meta;
        public readonly Locale Locale;
        /// <summary>File the values came from; not vsystem.cfg if that one is missing.</summary>
        public readonly string LoadedFrom;

        readonly CfgFile cfg, factory;
        readonly byte[] loadedBytes;
        readonly Dictionary<string, string> edits = new Dictionary<string, string>();

        public Booth(string folder)
        {
            Folder = Path.GetFullPath(folder);
            if (Folder.Length > 3) Folder = Folder.TrimEnd(Path.DirectorySeparatorChar); // keep the slash of a drive root
            Meta = new Metadata(Ini.Load(PathOf(MetaName)));
            Locale = new Locale(File.Exists(PathOf(LocaleName)) ? Ini.Load(PathOf(LocaleName)) : null);
            LoadedFrom = new[] { CfgName, AltName, FactoryName }.FirstOrDefault(n => File.Exists(PathOf(n)));
            loadedBytes = LoadedFrom == null ? new byte[0] : File.ReadAllBytes(PathOf(LoadedFrom));
            cfg = new CfgFile(loadedBytes);
            factory = File.Exists(PathOf(FactoryName)) ? new CfgFile(File.ReadAllBytes(PathOf(FactoryName))) : null;

            var langs = Locale.Languages();
            if (langs != null)
                foreach (var f in Meta.Fields.Where(f => f.MenuName == "langs-menu")) f.Menu = langs;
            if (Locale.Countries.Count > 0)
                foreach (var f in Meta.Fields.Where(f => f.MenuName == "country-menu"))
                {
                    f.Menu = new MenuDef();
                    foreach (var c in Locale.Countries) f.Menu.Add(c, c);
                }
        }

        public string PathOf(string name) => Path.Combine(Folder, name);

        // ---- finding booth folders ----

        public static bool IsBooth(string dir) => File.Exists(Path.Combine(dir, MetaName));

        /// <summary>The booth folder at or directly below dir (people often pick the folder that holds "vend").</summary>
        public static string Resolve(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            if (IsBooth(dir)) return dir;
            var sub = Path.Combine(dir, "vend");
            return IsBooth(sub) ? sub : null;
        }

        /// <summary>The booth folder that dir is inside of, e.g. when the program sits in vend\bin.</summary>
        public static string FindAround(string dir)
        {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                if (IsBooth(d.FullName)) return d.FullName;
            return null;
        }

        // ---- values ----

        public string Saved(string key) => cfg.Get(key);
        public string Get(string key) => edits.TryGetValue(key, out var v) ? v : cfg.Get(key);
        public bool IsEdited(string key) => edits.ContainsKey(key);
        public bool Dirty => edits.Count > 0;
        public List<string> Keys() => cfg.Keys();

        /// <summary>What the booth falls back on for a key that is not in vsystem.cfg.</summary>
        public string DefaultOf(FieldDef f)
        {
            var v = factory?.Get(f.Name);
            if (!string.IsNullOrEmpty(v)) return v;
            if (f.Default.Length > 0) return f.Default;
            return f.Menu != null && f.Menu.Items.Count > 0 ? f.Menu.Items[0].Values[0] : "";
        }

        public List<Change> Changes() => edits
            .Select(e => new Change { Key = e.Key, Old = cfg.Get(e.Key), New = e.Value })
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToList();

        void Put(string key, string value)
        {
            var saved = cfg.Get(key);
            if (value == saved || (saved == null && string.IsNullOrEmpty(value))) edits.Remove(key);
            else edits[key] = value;
        }

        /// <summary>Change a value along with any values that the booth derives from it. null = back to "not set".</summary>
        public void Set(string key, string value)
        {
            if (key == "country" && Meta.Field("country") != null) SetCountry(value);
            else if (key == "printer_name" && Meta.Field("printer_name") != null) SetPrinter(value);
            else Put(key, value);
        }

        void SetPrinter(string name)
        {
            Put("printer_name", name);
            if (name == cfg.Get("printer_name")) { edits.Remove("printer_type"); return; }
            // printertype-menu maps the stored printer name to the driver type
            var item = Meta.Menu("printertype-menu")?.Items.FirstOrDefault(
                i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Put("printer_type", item?.Values[0] ?? name);
        }

        // ---- money ----

        public CurrencySet Currency() => Locale.Find(Get("country"));

        /// <summary>The price in use, found by name, as the booth does. null if it is not in the country's list.</summary>
        public Price CurrentPrice()
        {
            var name = Get("current_price");
            var named = Currency().Prices.Where(p => p.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            // a few price lists repeat a name with different costs
            return named.FirstOrDefault(p => CostKeys.Select((k, i) => Get(k) == p.Costs[i].ToString()).All(x => x))
                   ?? named.FirstOrDefault();
        }

        void ForgetPrice()
        {
            foreach (var k in CostKeys.Concat(new[] { "current_price", "vendType", "tokenvals" })) edits.Remove(k);
        }

        void SetCountry(string name)
        {
            var priceName = Get("current_price");
            Put("country", name);
            foreach (var k in CountryKeys) edits.Remove(k);
            ForgetPrice();
            if (!IsEdited("country")) return; // back to what is saved, so the saved money settings stand
            var cs = Locale.Find(name);
            if (!cs.Defined) return;          // listed in locale.ini, but with no data to apply
            if (cs.CurrencyFormat != null) Put("currency_format", cs.CurrencyFormat);
            if (cs.DateFormat != null) Put("date_format", cs.DateFormat);
            Put("coinvals", Pad("coinvals", cs.Coins));
            Put("billvals", Pad("billvals", cs.Bills));
            if (cs.Prices.Count > 0)
                SetPrice(cs.Prices.FirstOrDefault(p => p.Name.Equals(priceName ?? "", StringComparison.OrdinalIgnoreCase)) ?? cs.Prices[0]);
            else
                Put("tokenvals", Pad("tokenvals", cs.Tokens(null)));
        }

        public void SetPrice(Price price)
        {
            ForgetPrice();
            bool sameCountry = !IsEdited("country");
            var kept = CurrentPrice(); // with the price edits gone, this is the saved one
            if (sameCountry && kept != null && kept.Name == price.Name && kept.Costs.SequenceEqual(price.Costs)) return;
            Put("vendType", "-1"); // -1: the costs below apply, instead of a built-in vend type
            Put("current_price", price.Name);
            for (int i = 0; i < CostKeys.Length; i++) Put(CostKeys[i], price.Costs[i].ToString());
            var cs = Currency();
            if (!sameCountry || cs.TokenSet != null) Put("tokenvals", Pad("tokenvals", cs.Tokens(price)));
        }

        /// <summary>Pad with -1 to the number of slots this booth uses (7 or 8, depending on version).</summary>
        string Pad(string key, List<string> vals)
        {
            var cur = cfg.Get(key) ?? factory?.Get(key);
            int n = string.IsNullOrEmpty(cur) ? 8 : cur.Split(',').Length;
            return string.Join(",", vals.Concat(Enumerable.Repeat("-1", n)).Take(Math.Max(n, vals.Count)));
        }

        // ---- checks ----

        /// <summary>Why a value cannot be stored, or null.</summary>
        public static string Problem(string value, FieldDef f)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.IndexOfAny(new[] { ';', '#' }) >= 0) return "The characters ; and # start a comment in booth files.";
            if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0) return "Line breaks cannot be stored.";
            if (value.Any(c => c > 255)) return "Contains a character that the booth cannot store.";
            if (f != null && (f.Flags & Tx.Numeric) != 0 && !value.All(c => c >= '0' && c <= '9')) return "Digits only.";
            if (f != null && (f.Flags & Tx.Hex) != 0 && !value.All(c => Uri.IsHexDigit(c) || c == 'x' || c == 'X')) return "Hexadecimal digits only.";
            return null;
        }

        // ---- saving ----

        public bool ChangedOnDisk()
        {
            if (LoadedFrom == null) return File.Exists(PathOf(CfgName));
            if (LoadedFrom != CfgName) return File.Exists(PathOf(CfgName));
            return !File.Exists(PathOf(CfgName)) || !File.ReadAllBytes(PathOf(CfgName)).SequenceEqual(loadedBytes);
        }

        /// <summary>Apply the edits to what is on disk now. vsystem.alt is kept identical to vsystem.cfg, as on the booth.</summary>
        public void Save()
        {
            var path = PathOf(CfgName);
            var current = File.Exists(path) ? File.ReadAllBytes(path) : loadedBytes;
            var file = new CfgFile(current);
            foreach (var e in edits) file.Set(e.Key, e.Value ?? "");
            var bytes = file.ToBytes();

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Backup(CfgName, stamp);
            if (File.Exists(PathOf(AltName)) && !File.ReadAllBytes(PathOf(AltName)).SequenceEqual(current)) Backup(AltName, stamp);

            WriteFile(path, bytes);
            WriteFile(PathOf(AltName), bytes);
        }

        void Backup(string name, string stamp)
        {
            if (!File.Exists(PathOf(name))) return;
            var dir = PathOf(BackupDir);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name + "." + stamp);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, name + "." + stamp + "-" + i);
            File.WriteAllBytes(dest, File.ReadAllBytes(PathOf(name)));
        }

        /// <summary>Replace a file in one step. The booth marks vsystem.cfg read-only; that attribute is put back.</summary>
        static void WriteFile(string path, byte[] bytes)
        {
            var temp = path + ".tmp~";
            File.WriteAllBytes(temp, bytes);
            if (!File.Exists(path)) { File.Move(temp, path); return; }
            var attrs = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                File.Replace(temp, path, null);
            }
            finally
            {
                if (File.Exists(path)) File.SetAttributes(path, attrs);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
