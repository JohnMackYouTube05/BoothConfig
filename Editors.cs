using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BoothConfig
{
    /// <summary>A combo box that leaves the mouse wheel to the page, so scrolling cannot change a setting.</summary>
    class PageCombo : ComboBox
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (DroppedDown) { base.OnMouseWheel(e); return; }
            if (e is HandledMouseEventArgs h) h.Handled = true;
            for (Control c = Parent; c != null; c = c.Parent)
                if (c is Panel p && p.AutoScroll && p.VerticalScroll.Visible)
                {
                    p.AutoScrollPosition = new Point(-p.AutoScrollPosition.X, -p.AutoScrollPosition.Y - e.Delta);
                    return;
                }
        }
    }

    /// <summary>Binds one field to a control.</summary>
    abstract class Editor
    {
        public FieldDef Field;
        public Control Control;
        public Label Caption, HelpLabel;
        public event Action Changed;
        protected Booth Booth;
        protected bool Loading;

        protected void RaiseChanged() => Changed?.Invoke();

        public static Editor Create(Booth booth, FieldDef f)
        {
            Editor e;
            bool menu = f.Menu != null && f.Menu.Items.Count > 0;
            if (f.Name == "vendType" && booth.Meta.Field("current_price") != null && booth.Locale.Countries.Count > 0)
                e = new PriceEditor();
            else if (menu && (f.Flags & Tx.Checklist) != 0) e = new ChecklistEditor();
            else if (menu && (f.Type == 0 || f.Type == 1)) e = new ChoiceEditor();
            else if (menu && f.Type == 3) e = new SuggestEditor();
            else e = new TextEditor();
            e.Booth = booth;
            e.Field = f;
            e.Build();
            e.Control.Name = f.Name;
            e.Control.AccessibleName = f.Label;
            return e;
        }

        protected abstract void Build();
        protected abstract void Show();

        public void RefreshFromBooth()
        {
            Loading = true;
            try { Show(); } finally { Loading = false; }
        }

        public virtual bool IsEdited => Booth.IsEdited(Field.Name);
        public virtual string Problem => Booth.IsEdited(Field.Name) ? Booth.Problem(Booth.Get(Field.Name), Field) : null;

        protected string DefaultText()
        {
            var d = Booth.DefaultOf(Field);
            if (d.Length == 0) return "(not set)";
            return "(not set, default: " + (Field.Menu?.Find(d)?.Name ?? d) + ")";
        }
    }

    class TextEditor : Editor
    {
        protected override void Build()
        {
            var box = new TextBox();
            Control = box;
            box.TextChanged += (s, e) =>
            {
                if (Loading) return;
                Booth.Set(Field.Name, box.Text.Trim());
                RaiseChanged();
            };
        }

        protected override void Show()
        {
            var box = (TextBox)Control;
            var v = Booth.Get(Field.Name) ?? "";
            if (!Field.Hidden && Field.Width > 0) box.MaxLength = Math.Max(Field.Width, v.Length);
            if (box.Text.Trim() != v.Trim() || !box.Focused) box.Text = v;
        }
    }

    class ChoiceEditor : Editor
    {
        class Other { public string Text, Value; public override string ToString() => Text; }

        protected PageCombo Combo => (PageCombo)Control;

        protected override void Build()
        {
            Control = new PageCombo { DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 20 };
            Combo.SelectedIndexChanged += (s, e) =>
            {
                if (Loading || Combo.SelectedItem == null) return;
                Choose(Combo.SelectedItem);
                RaiseChanged();
            };
        }

        protected virtual void Choose(object item)
        {
            var saved = Booth.Saved(Field.Name);
            if (item is Other o) Booth.Set(Field.Name, o.Value);
            // picking what is already saved puts the saved spelling back, so it does not count as a change
            else if (item is MenuItemDef m)
                Booth.Set(Field.Name, saved != null && Field.Menu.Find(saved) == m ? saved : Field.Menu.ValueFor(m, saved));
        }

        protected override void Show()
        {
            var saved = Booth.Saved(Field.Name);
            var value = Booth.Get(Field.Name);
            var items = Field.Menu.Items.Cast<object>().ToList();
            object sel = value == null ? null : Field.Menu.Find(value);
            if (saved == null)
            {
                var none = new Other { Text = DefaultText() };
                items.Insert(0, none);
                if (value == null) sel = none;
            }
            else if (Field.Menu.Find(saved) == null)
                items.Insert(0, new Other { Text = saved.Length == 0 ? "(blank)" : saved + "  (not in menu)", Value = saved });
            if (sel == null)
            {
                sel = items.OfType<Other>().FirstOrDefault(o => o.Value == value);
                if (sel == null) items.Insert(0, sel = new Other { Text = value + "  (not in menu)", Value = value });
            }
            Fill(items, sel);
        }

        protected void Fill(List<object> items, object sel)
        {
            var old = Combo.Items.Cast<object>().Select(i => i.ToString());
            if (!old.SequenceEqual(items.Select(i => i.ToString())))
            {
                Combo.BeginUpdate();
                Combo.Items.Clear();
                Combo.Items.AddRange(items.ToArray());
                Combo.EndUpdate();
            }
            var idx = items.IndexOf(sel);
            if (Combo.SelectedIndex != idx) Combo.SelectedIndex = idx;
        }
    }

    /// <summary>Vend type. The choices are the price list of the selected currency set, identified by name.</summary>
    class PriceEditor : ChoiceEditor
    {
        public override bool IsEdited => Booth.IsEdited("current_price") || base.IsEdited;

        protected override void Choose(object item)
        {
            if (item is Price) Booth.SetPrice(Booth.Currency().Prices[Combo.Items.Cast<object>().OfType<Price>().ToList().IndexOf((Price)item)]);
            else base.Choose(item);
        }

        protected override void Show()
        {
            var prices = Booth.Currency().Prices;
            if (prices.Count == 0)
            {
                // no price list for this country: fall back on the built-in vend types of nsyscfg.ini
                if (Field.Menu != null && Field.Menu.Items.Count > 0) base.Show();
                return;
            }
            var items = prices.Cast<object>().ToList();
            var name = Booth.Get("current_price") ?? "";
            var cur = Booth.CurrentPrice();
            object sel = cur == null ? null : prices.First(p => p.Name == cur.Name && p.Costs.SequenceEqual(cur.Costs));
            if (sel == null) items.Insert(0, sel = name.Length == 0 ? "(not set)" : name + "  (not in price list)");
            Fill(items, sel);
        }
    }

    class SuggestEditor : Editor
    {
        protected override void Build()
        {
            var combo = new PageCombo { DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 20 };
            Control = combo;
            foreach (var i in Field.Menu.Items) combo.Items.Add(i);
            combo.TextChanged += (s, e) =>
            {
                if (Loading) return;
                var m = Field.Menu.Items.FirstOrDefault(i => i.Name == combo.Text);
                Booth.Set(Field.Name, m != null ? Field.Menu.ValueFor(m, Booth.Saved(Field.Name)) : combo.Text);
                RaiseChanged();
            };
        }

        protected override void Show()
        {
            var v = Booth.Get(Field.Name) ?? "";
            var text = Field.Menu.Find(v)?.Name ?? v;
            if (Control.Text != text && !Control.Focused) Control.Text = text;
        }
    }

    class ChecklistEditor : Editor
    {
        protected override void Build()
        {
            var list = new CheckedListBox { CheckOnClick = true, IntegralHeight = true };
            Control = list;
            foreach (var i in Field.Menu.Items) list.Items.Add(i);
            list.Height = list.ItemHeight * Math.Min(6, list.Items.Count) + 6;
            list.ItemCheck += (s, e) =>
            {
                if (Loading) return;
                var on = new List<string>();
                for (int i = 0; i < list.Items.Count; i++)
                    if (i == e.Index ? e.NewValue == CheckState.Checked : list.GetItemChecked(i))
                        on.Add(((MenuItemDef)list.Items[i]).Values[0]);
                Booth.Set(Field.Name, string.Join(",", on));
                list.BeginInvoke((Action)RaiseChanged);
            };
        }

        protected override void Show()
        {
            var list = (CheckedListBox)Control;
            var on = (Booth.Get(Field.Name) ?? "").Split(',').Select(v => Field.Menu.Find(v.Trim())).ToList();
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, on.Contains((MenuItemDef)list.Items[i]));
        }
    }
}
