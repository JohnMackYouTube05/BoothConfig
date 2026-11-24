using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BoothConfig
{
    class Change
    {
        public ConfigDoc Doc;
        public string Key, Old, New;
    }

    /// <summary>One configuration file, the metadata that describes it, and the edits not written yet.</summary>
    class ConfigDoc
    {
        public const string BackupDir = "BoothConfig-backups";

        public readonly string Folder, MetaName, FileName;
        public readonly Metadata Meta;
        /// <summary>File the values came from; not FileName if that one is missing, null if there is none.</summary>
        public string LoadedFrom { get; private set; }

        readonly string[] fallbacks;
        readonly Dictionary<string, string> edits = new Dictionary<string, string>();
        CfgFile file;
        byte[] loadedBytes;

        public ConfigDoc(string folder, string metaName, string fileName, params string[] fallbacks)
        {
            Folder = folder;
            MetaName = metaName;
            FileName = fileName;
            this.fallbacks = fallbacks;
            try { Meta = new Metadata(Ini.Load(PathOf(metaName))); }
            catch (FormatException ex) { throw new FormatException(metaName + ": " + ex.Message, ex); }
            Load();
        }

        public string PathOf(string name) => Path.Combine(Folder, name);

        void Load()
        {
            edits.Clear();
            LoadedFrom = new[] { FileName }.Concat(fallbacks).FirstOrDefault(n => File.Exists(PathOf(n)));
            loadedBytes = LoadedFrom == null ? new byte[0] : File.ReadAllBytes(PathOf(LoadedFrom));
            file = new CfgFile(loadedBytes);
        }

        // ---- values ----

        public string Saved(string key) => file.Get(key);
        public string Get(string key) => edits.TryGetValue(key, out var v) ? v : file.Get(key);
        public bool IsEdited(string key) => edits.ContainsKey(key);
        public bool Dirty => edits.Count > 0;
        public List<string> Keys() => file.Keys();

        /// <summary>What applies while a key is not in the file, or "" if that is not known.</summary>
        public virtual string DefaultOf(FieldDef f) => f.Default;

        public List<Change> Changes() => edits
            .Select(e => new Change { Doc = this, Key = e.Key, Old = file.Get(e.Key), New = e.Value })
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Change a value along with any values derived from it. null = back to "not set".</summary>
        public virtual void Set(string key, string value) => Put(key, value);

        protected void Put(string key, string value)
        {
            var saved = file.Get(key);
            if (value == saved || (saved == null && string.IsNullOrEmpty(value))) edits.Remove(key);
            else edits[key] = value;
        }

        protected void Forget(IEnumerable<string> keys)
        {
            foreach (var k in keys) edits.Remove(k);
        }

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

        /// <summary>Whether saving would write this file.</summary>
        public virtual bool NeedsSave => Dirty;

        public bool ChangedOnDisk()
        {
            if (LoadedFrom != FileName) return File.Exists(PathOf(FileName));
            return !File.Exists(PathOf(FileName)) || !File.ReadAllBytes(PathOf(FileName)).SequenceEqual(loadedBytes);
        }

        /// <summary>Apply the edits to what is on disk now, then load the result.</summary>
        public void Save()
        {
            var path = PathOf(FileName);
            var current = File.Exists(path) ? File.ReadAllBytes(path) : loadedBytes;
            var target = new CfgFile(current);
            foreach (var e in edits) target.Set(e.Key, e.Value ?? "");
            Write(current, target.ToBytes(), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Load();
        }

        protected virtual void Write(byte[] current, byte[] bytes, string stamp)
        {
            Backup(FileName, stamp);
            WriteFile(PathOf(FileName), bytes);
        }

        protected void Backup(string name, string stamp)
        {
            if (!File.Exists(PathOf(name))) return;
            var dir = PathOf(BackupDir);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name + "." + stamp);
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, name + "." + stamp + "-" + i);
            File.WriteAllBytes(dest, File.ReadAllBytes(PathOf(name)));
        }

        /// <summary>Replace a file in one step. The booth marks some files read-only; that attribute is put back.</summary>
        protected static void WriteFile(string path, byte[] bytes)
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
