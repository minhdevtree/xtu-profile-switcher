// UI text. The strings live in plain-text language files, not in code:
//
//   lang\<code>.lang           built into the exe (build.ps1 embeds every file in the lang folder)
//   %LOCALAPPDATA%\XtuProfileSwitcher\lang\<code>.lang
//                               optional, read at start: adds a language or overrides single keys
//                               of a built-in one, no rebuild needed
//
// File format (UTF-8):  key = text   one per line, "#" starts a comment, \n is a line break, \\ a backslash.
// Two header lines name the language:  @code = vi   and   @name = Tiếng Việt  (shown in Settings).
// English is the reference: a key missing in another language falls back to English, and a key missing
// everywhere shows the key itself, so a typo in code is visible. "XtuProfileSwitcher.exe --langcheck"
// lists missing/extra keys and placeholder mismatches for every language.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace XtuProfileSwitcher
{
    internal sealed class Language
    {
        public string Code = "";
        public string Name = "";
        public readonly Dictionary<string, string> Text = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<string> Sources = new List<string>();
    }

    internal static class Loc
    {
        public const string Fallback = "en";
        private const string ResourcePrefix = "XtuProfileSwitcher.lang.";

        private static readonly Dictionary<string, Language> langs = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        private static Language current, english;

        public static string Lang { get { return current.Code; } }

        // English first, then the others by name.
        public static List<Language> Languages
        {
            get
            {
                List<Language> list = new List<Language>(langs.Values);
                list.Sort(delegate (Language a, Language b)
                {
                    if (a == english) return -1;
                    if (b == english) return 1;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                return list;
            }
        }

        public static bool Exists(string code) { return code != null && langs.ContainsKey(code); }

        // Switches the language; an unknown code leaves things as they are and returns false.
        public static bool Set(string code)
        {
            Language l;
            if (code == null || !langs.TryGetValue(code.Trim(), out l)) return false;
            current = l;
            return true;
        }

        public static string T(string key)
        {
            string v;
            if (current.Text.TryGetValue(key, out v)) return v;
            if (english.Text.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            string text = T(key);
            try { return string.Format(CultureInfo.InvariantCulture, text, args); }
            catch (FormatException)
            {
                // A translation with a broken placeholder must not crash the window: use the English text.
                string v;
                try { if (english.Text.TryGetValue(key, out v)) return string.Format(CultureInfo.InvariantCulture, v, args); }
                catch (FormatException) { }
                return text;
            }
        }

        static Loc()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string res in asm.GetManifestResourceNames())
                    if (res.StartsWith(ResourcePrefix, StringComparison.Ordinal) && res.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
                        using (Stream s = asm.GetManifestResourceStream(res))
                        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                            Load(r, res.Substring(ResourcePrefix.Length), false);
            }
            catch (Exception ex) { Log.Write("language resources: " + ex.Message); }

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtuProfileSwitcher", "lang");
                if (Directory.Exists(dir))
                    foreach (string file in Directory.GetFiles(dir, "*.lang"))
                        try
                        {
                            using (StreamReader r = new StreamReader(file, Encoding.UTF8))
                                Load(r, file, true);
                        }
                        catch (Exception ex) { Log.Write("language file " + file + ": " + ex.Message); }
            }
            catch (Exception ex) { Log.Write("language folder: " + ex.Message); }

            if (!langs.TryGetValue(Fallback, out english))
            {
                // Built without lang\en.lang: keep running, showing keys.
                english = new Language(); english.Code = Fallback; english.Name = "English";
                langs[Fallback] = english;
            }
            current = english;
        }

        // Reads one language file. A file with a known @code adds to / overrides that language.
        private static void Load(TextReader reader, string source, bool overrideExisting)
        {
            Language target = new Language();
            string line;
            int n = 0;
            while ((line = reader.ReadLine()) != null)
            {
                n++;
                string t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) { Log.Write("language " + source + " line " + n + ": expected key = text"); continue; }
                string key = t.Substring(0, eq).Trim();
                string val = Unescape(t.Substring(eq + 1).Trim());
                if (key == "@code") target.Code = val.Trim().ToLowerInvariant();
                else if (key == "@name") target.Name = val;
                else target.Text[key] = val;
            }

            if (target.Code.Length == 0)
            {
                // No header: take the code from the file name (vi.lang -> vi).
                string name = Path.GetFileNameWithoutExtension(source);
                target.Code = name.ToLowerInvariant();
            }
            if (!Regex.IsMatch(target.Code, "^[a-z]{2,3}(-[a-z0-9]{2,8})*$"))
            {
                Log.Write("language " + source + ": bad code '" + target.Code + "'");
                return;
            }

            Language have;
            if (!langs.TryGetValue(target.Code, out have))
            {
                if (target.Name.Length == 0) target.Name = target.Code;
                target.Sources.Add(source);
                langs[target.Code] = target;
                return;
            }
            // Same code again (user file over the built-in one): keys are overridden one by one.
            if (target.Name.Length > 0) have.Name = target.Name;
            foreach (KeyValuePair<string, string> kv in target.Text) have.Text[kv.Key] = kv.Value;
            have.Sources.Add(source);
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // Compares every language with English: keys that are missing or unknown, and placeholders
        // ({0}, {1}, ...) that differ. Returns true when everything lines up.
        public static bool Check(out string report)
        {
            StringBuilder sb = new StringBuilder();
            bool ok = true;
            sb.AppendLine("English keys: " + english.Text.Count);
            foreach (Language l in Languages)
            {
                if (l == english) continue;
                List<string> missing = new List<string>(), extra = new List<string>(), ph = new List<string>();
                foreach (KeyValuePair<string, string> kv in english.Text)
                {
                    string v;
                    if (!l.Text.TryGetValue(kv.Key, out v)) { missing.Add(kv.Key); continue; }
                    if (Placeholders(kv.Value) != Placeholders(v)) ph.Add(kv.Key);
                }
                foreach (string k in l.Text.Keys) if (!english.Text.ContainsKey(k)) extra.Add(k);

                int done = english.Text.Count - missing.Count;
                sb.AppendLine(l.Code + " (" + l.Name + "): " + done + "/" + english.Text.Count + " translated"
                    + (l.Sources.Count > 0 ? "   [" + string.Join(", ", l.Sources.ToArray()) + "]" : ""));
                if (missing.Count > 0) { sb.AppendLine("  missing (shown in English): " + string.Join(", ", missing.ToArray())); }
                if (extra.Count > 0) { sb.AppendLine("  unknown keys (typo?): " + string.Join(", ", extra.ToArray())); ok = false; }
                if (ph.Count > 0) { sb.AppendLine("  placeholders differ from English: " + string.Join(", ", ph.ToArray())); ok = false; }
            }
            report = sb.ToString();
            return ok;
        }

        // The sorted set of placeholder numbers used by a text, e.g. "0,1".
        private static string Placeholders(string s)
        {
            SortedDictionary<int, bool> set = new SortedDictionary<int, bool>();
            foreach (Match m in Regex.Matches(s, @"\{(\d+)(?:[:,][^}]*)?\}")) set[int.Parse(m.Groups[1].Value)] = true;
            List<string> parts = new List<string>();
            foreach (int k in set.Keys) parts.Add(k.ToString());
            return string.Join(",", parts.ToArray());
        }
    }
}
