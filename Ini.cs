using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BoothConfig
{
    /// <summary>Sectioned ini as used by nsyscfg.ini and locale.ini: duplicate keys are meaningful and order matters.</summary>
    class Ini
    {
        public static readonly Encoding Enc = Encoding.GetEncoding(28591); // byte-transparent

        readonly Dictionary<string, List<string>> map =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Names = new List<string>();

        public static Ini Load(string path)
        {
            var ini = new Ini();
            List<string> cur = null;
            foreach (var raw in File.ReadAllLines(path, Enc))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.Contains("]"))
                {
                    var name = line.Substring(1, line.IndexOf(']') - 1).Trim();
                    if (!ini.map.TryGetValue(name, out cur))
                    {
                        ini.map[name] = cur = new List<string>();
                        ini.Names.Add(name);
                    }
                }
                else if (cur != null && line.Length > 0) cur.Add(line);
            }
            return ini;
        }

        public bool Has(string section) => section != null && map.ContainsKey(section);

        /// <summary>All non-blank lines, including comments.</summary>
        public List<string> Lines(string section) =>
            section != null && map.TryGetValue(section, out var l) ? l : new List<string>();

        public IEnumerable<KeyValuePair<string, string>> Pairs(string section)
        {
            foreach (var line in Lines(section))
                if (TrySplit(line, out var k, out var v))
                    yield return new KeyValuePair<string, string>(k, v);
        }

        public string Get(string section, string key)
        {
            foreach (var p in Pairs(section))
                if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        public static bool TrySplit(string line, out string key, out string value)
        {
            key = value = null;
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '>') return false;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            key = line.Substring(0, eq).Trim();
            // "value   ; remark" - the remark must be set off by whitespace
            value = Regex.Replace(line.Substring(eq + 1), @"\s+;.*$", "").Trim();
            return true;
        }
    }

    /// <summary>
    /// vsystem.cfg: a flat key=value file. Edits replace only the affected lines; every other
    /// byte (order, duplicates, unknown keys, line endings) is kept as it was.
    /// </summary>
    class CfgFile
    {
        static readonly Regex LineRx = new Regex(@"^(\s*([^=;#\[\s][^=]*?)\s*=)([^\r\n]*)(\r?)$");
        readonly List<string> lines;
        readonly bool crlf;

        public CfgFile(byte[] bytes)
        {
            var text = Ini.Enc.GetString(bytes);
            crlf = text.Contains("\r\n");
            lines = new List<string>(text.Split('\n'));
        }

        public List<string> Keys()
        {
            var seen = new HashSet<string>();
            var keys = new List<string>();
            foreach (var l in lines)
            {
                var m = LineRx.Match(l);
                if (m.Success && seen.Add(m.Groups[2].Value)) keys.Add(m.Groups[2].Value);
            }
            return keys;
        }

        public string Get(string key)
        {
            foreach (var l in lines)
            {
                var m = LineRx.Match(l);
                if (m.Success && m.Groups[2].Value == key) return m.Groups[3].Value.Trim();
            }
            return null;
        }

        /// <summary>Replace every occurrence of the key, or append it.</summary>
        public void Set(string key, string value)
        {
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var m = LineRx.Match(lines[i]);
                if (!m.Success || m.Groups[2].Value != key) continue;
                lines[i] = m.Groups[1].Value + value + m.Groups[4].Value;
                found = true;
            }
            if (found) return;
            var cr = crlf ? "\r" : "";
            int last = lines.Count - 1;
            if (lines[last].Length == 0) lines.Insert(last, key + "=" + value + cr); // file ended in a newline
            else
            {
                lines[last] += cr;
                lines.Add(key + "=" + value + cr);
                lines.Add("");
            }
        }

        public byte[] ToBytes() => Ini.Enc.GetBytes(string.Join("\n", lines));
    }
}
