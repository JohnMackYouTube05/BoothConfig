using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BoothConfig
{
    /// <summary>vsystem.cfg, as described by nsyscfg.ini and locale.ini.</summary>
    class SystemDoc : ConfigDoc
    {
        public const string AltName = "vsystem.alt", FactoryName = "zsystem.cfg", LocaleName = "locale.ini";

        static readonly string[] CostKeys =
            { "play_cost", "foto1_cost", "foto2_cost", "foto3_cost", "foto1_alt_cost", "foto2_alt_cost", "foto3_alt_cost" };
        static readonly string[] CountryKeys = { "currency_format", "date_format", "coinvals", "billvals" };

        public readonly Locale Locale;
        readonly CfgFile factory;

        public SystemDoc(string folder) : base(folder, Booth.MetaName, Booth.CfgName, AltName, FactoryName)
        {
            Locale = new Locale(File.Exists(PathOf(LocaleName)) ? Ini.Load(PathOf(LocaleName)) : null);
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

        public override string DefaultOf(FieldDef f)
        {
            var v = factory?.Get(f.Name);
            if (!string.IsNullOrEmpty(v)) return v;
            if (f.Default.Length > 0) return f.Default;
            return f.Menu != null && f.Menu.Items.Count > 0 ? f.Menu.Items[0].Values[0] : "";
        }

        public override void Set(string key, string value)
        {
            if (key == "country" && Meta.Field("country") != null) SetCountry(value);
            else if (key == "printer_name" && Meta.Field("printer_name") != null) SetPrinter(value);
            else base.Set(key, value);
        }

        void SetPrinter(string name)
        {
            Put("printer_name", name);
            if (name == Saved("printer_name")) { Forget(new[] { "printer_type" }); return; }
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

        void ForgetPrice() => Forget(CostKeys.Concat(new[] { "current_price", "vendType", "tokenvals" }));

        void SetCountry(string name)
        {
            var priceName = Get("current_price");
            Put("country", name);
            Forget(CountryKeys);
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
            var cur = Saved(key) ?? factory?.Get(key);
            int n = string.IsNullOrEmpty(cur) ? 8 : cur.Split(',').Length;
            return string.Join(",", vals.Concat(Enumerable.Repeat("-1", n)).Take(Math.Max(n, vals.Count)));
        }

        // ---- saving ----

        /// <summary>A missing vsystem.cfg can be recreated without changing anything first.</summary>
        public override bool NeedsSave => Dirty || LoadedFrom != FileName;

        /// <summary>vsystem.alt is kept identical to vsystem.cfg, as on the booth.</summary>
        protected override void Write(byte[] current, byte[] bytes, string stamp)
        {
            if (File.Exists(PathOf(AltName)) && !File.ReadAllBytes(PathOf(AltName)).SequenceEqual(current)) Backup(AltName, stamp);
            base.Write(current, bytes, stamp);
            WriteFile(PathOf(AltName), bytes);
        }
    }
}
