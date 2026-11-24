using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BoothConfig
{
    /// <summary>One booth folder and the configuration files in it that can be edited.</summary>
    class Booth
    {
        public const string MetaName = "nsyscfg.ini", CfgName = "vsystem.cfg",
            DebugMetaName = "debug.inf", DebugName = "debug.ini";

        public readonly string Folder;
        public readonly SystemDoc System;
        /// <summary>null if the booth has no usable debug.inf.</summary>
        public readonly ConfigDoc Debug;
        public readonly string DebugError;
        public readonly List<ConfigDoc> Docs = new List<ConfigDoc>();

        public Booth(string folder)
        {
            Folder = Path.GetFullPath(folder);
            if (Folder.Length > 3) Folder = Folder.TrimEnd(Path.DirectorySeparatorChar); // keep the slash of a drive root
            Docs.Add(System = new SystemDoc(Folder));
            if (!File.Exists(Path.Combine(Folder, DebugMetaName))) return;
            // the main configuration stays usable whatever is wrong with the debug files
            try { Docs.Add(Debug = new ConfigDoc(Folder, DebugMetaName, DebugName)); }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                DebugError = ex.Message;
            }
        }

        public bool Dirty => Docs.Any(d => d.Dirty);
        public bool NeedsSave => Docs.Any(d => d.NeedsSave);
        public List<Change> Changes() => Docs.SelectMany(d => d.Changes()).ToList();

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
    }
}
