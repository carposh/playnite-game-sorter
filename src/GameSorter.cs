using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Game Sorter for Playnite")]
[assembly: AssemblyProduct("Game Sorter")]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

namespace GameSorter
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 4 && args[1] == "--dry-run")
            {
                var sb = new StringBuilder();
                new Syncer(l => { lock (sb) sb.AppendLine(l); }, (a, b, t) => { }, q => false)
                    .Run(args.Skip(3).ToList(), false, true);
                File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(true));
                return;
            }
            if (args.Length >= 4 && args[1] == "--sync")
            {
                var sb = new StringBuilder();
                try
                {
                    new Syncer(l => { lock (sb) sb.AppendLine(l); }, (a, b, t) => { }, q => true)
                        .Run(args.Skip(3).ToList(), true);
                }
                catch (Exception ex) { sb.AppendLine("Something went wrong: " + ex); }
                File.WriteAllText(args[2], sb.ToString(), new UTF8Encoding(true));
                return;
            }
            Application.Run(new MainForm());
        }
    }

    static class Paths
    {
        static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string Data = Path.Combine(Roaming, "GameSorter");
        public static readonly string State = Path.Combine(Data, "state.json");
        public static readonly string Pending = Path.Combine(Data, "pending.json");
        public static readonly string Result = Path.Combine(Data, "result.json");
        public static readonly string Cache = Path.Combine(Data, "cache");
        public static readonly string Logs = Path.Combine(Data, "logs");
        public static readonly string Backups = Path.Combine(Data, "playnite-backups");
        public static readonly string PlayniteLibrary = Path.Combine(Roaming, "Playnite", "library");
        public static readonly string Extension = Path.Combine(Roaming, "Playnite", "Extensions", "GameSorter");

        public static string PlayniteExe
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\playnite\shell\open\command"))
                    {
                        var cmd = k == null ? null : k.GetValue("") as string;
                        if (cmd != null)
                        {
                            var m = Regex.Match(cmd, "\"([^\"]+\\.exe)\"");
                            if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
                        }
                    }
                }
                catch { }
                var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Playnite.DesktopApp.exe");
                return File.Exists(guess) ? guess : null;
            }
        }
    }

    static class Json
    {
        static JavaScriptSerializer Make()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }
        public static object Parse(string text) { return string.IsNullOrEmpty(text) ? null : Make().DeserializeObject(text); }
        public static T Parse<T>(string text) { return Make().Deserialize<T>(text); }
        public static string Write(object o) { return Make().Serialize(o); }

        public static object Get(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }
        public static string Str(object o, string key)
        {
            var v = Get(o, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static object[] Arr(object o, string key)
        {
            var v = Get(o, key);
            var a = v as object[];
            if (a != null) return a;
            var l = v as ArrayList;
            return l != null ? l.ToArray() : new object[0];
        }
    }

    static class Web
    {
        static WebClient Client()
        {
            var wc = new WebClient();
            wc.Encoding = Encoding.UTF8;
            wc.Headers[HttpRequestHeader.UserAgent] = "GameSorter/2.0 (Playnite helper)";
            return wc;
        }

        public static string GetString(string url)
        {
            for (int i = 0; i < 3; i++)
            {
                try { using (var wc = Client()) return wc.DownloadString(url); }
                catch { Thread.Sleep(1500 * (i + 1)); }
            }
            return null;
        }

        public static bool Download(string url, string path)
        {
            if (string.IsNullOrEmpty(url)) return false;
            for (int i = 0; i < 2; i++)
            {
                try { using (var wc = Client()) wc.DownloadFile(url, path); return true; }
                catch { try { File.Delete(path); } catch { } Thread.Sleep(1000); }
            }
            return false;
        }
    }

    class FoundGame
    {
        public string Folder, Exe, Key, Name, VersionText, Fingerprint;
        public List<string> Names = new List<string>();
        public int AppId;
        public double Score;
        public Version Ver;
        public DateTime BuildDate = DateTime.MinValue;
        public DateTime FilesTime = DateTime.MinValue;
    }

    class GameMeta
    {
        public string Name, Description, ReleaseDate, CoverUrl, BackgroundUrl, IconUrl;
        public List<string> Genres = new List<string>(), Developers = new List<string>(), Publishers = new List<string>(), Features = new List<string>();
    }

    public class GameState
    {
        public string Name, Folder, Exe, Version, Fingerprint;
        public int AppId;
    }

    public class MatchEntry
    {
        public int AppId;
        public string Name;
        public double Score;
    }

    public class AppState
    {
        public List<string> Roots = new List<string>();
        public bool AutoRestart = true;
        public Dictionary<string, GameState> Games = new Dictionary<string, GameState>();
        public Dictionary<string, MatchEntry> Matches = new Dictionary<string, MatchEntry>();

        public static AppState Load()
        {
            try
            {
                if (File.Exists(Paths.State))
                {
                    var s = Json.Parse<AppState>(File.ReadAllText(Paths.State, Encoding.UTF8));
                    if (s.Roots == null) s.Roots = new List<string>();
                    if (s.Games == null) s.Games = new Dictionary<string, GameState>();
                    if (s.Matches == null) s.Matches = new Dictionary<string, MatchEntry>();
                    return s;
                }
            }
            catch { }
            return new AppState();
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.State, Json.Write(this), new UTF8Encoding(false));
        }
    }

    static class Scanner
    {
        const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        static readonly Regex BadExe = new Regex(@"redist|crash|unins|setup|install|dxweb|oalinst|vcredist|vc_redist|dotnet|ndp\d|physx|createdump|crs-|verify|lumaplay|\.console$|workshoputility|qwentts|unitywebbrowser|epiconlineservices|^launcher$|^ue4prereq|^cefsharp|helper|reporter|^python|^7z|^notification", I);
        static readonly Regex BadDir = new Regex(@"^(_?commonredist|_?common redist|~common redist|_?redist|redist|directx|_uninstall|_verify|_lumaplay|_steam\d+|engine|__installer|support|prereq.*|.*_data|monobleedingedge|crashpad|tools?)$", I);
        static readonly Regex GenericDir = new Regex(@"^(game|bin|binaries|win64|win32|x64|windows|release|shipping|app|data|build|.*-steamgg\.net)$", I);
        static readonly Regex NotGameRoot = new Regex(@"^(saves?|steamlibrary|steamapps|windowsapps|xboxgames|wpsystem|program files.*|\$recycle\.bin|system volume information|windows|users|programdata|perflogs|recovery|msocache)$", I);
        static readonly Regex Shipping = new Regex(@"-(win64|win32|wingdk)-shipping$", I);

        public static string CleanName(string raw)
        {
            string n = raw ?? "";
            n = Regex.Replace(n, @"[\s\-._]*(steamrip\.com|ankergames|ofme|zeigames\.com|steamgg\.net|fitgirl.*|dodi.*|elamigos|gog|repack|portable)\b.*$", "", I);
            n = Regex.Replace(n, @"[\s._-]+build[\s._-]*\d+.*$", "", I);
            n = Regex.Replace(n, @"[\s._-]+v\d+(\.\d+)+.*$", "", I);
            n = Regex.Replace(n, @"\s*\(\d+\)", "");
            n = Regex.Replace(n, @"\s+(release|final|full ?game)$", "", I);
            if (!Regex.IsMatch(n, @"\s")) n = Regex.Replace(n, @"[._]", " ");
            if (!Regex.IsMatch(n, @"\s") && Regex.IsMatch(n, "[a-z][A-Z]")) n = Regex.Replace(n, "([a-z])([A-Z])", "$1 $2");
            n = Regex.Replace(n, "fullgame$", "", I);
            return Regex.Replace(n, @"\s+", " ").Trim(' ', '-', '.', '_');
        }

        public static string Norm(string s)
        {
            if (s == null) return "";
            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"&|\band\b", "");
            return Regex.Replace(s, "[^a-z0-9]", "");
        }

        public static double Similarity(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1.0;
            int shortLen = Math.Min(a.Length, b.Length), longLen = Math.Max(a.Length, b.Length);
            if ((a.Contains(b) || b.Contains(a)) && shortLen >= 4) return 0.8 + 0.15 * shortLen / longLen;
            var prev = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                prev = cur;
            }
            return 1.0 - (double)prev[b.Length] / longLen;
        }

        public static bool IsGameFolderCandidate(string dir)
        {
            var name = Path.GetFileName(dir);
            if (NotGameRoot.IsMatch(name)) return false;
            try { if ((new DirectoryInfo(dir).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) return false; }
            catch { return false; }
            return true;
        }

        public static string FindGameExe(string folder)
        {
            string folderClean = CleanName(Path.GetFileName(folder));
            string best = null;
            double bestScore = double.MinValue;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(folder, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                string dir = item.Key;
                int depth = item.Value;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.exe"); } catch { continue; }
                foreach (var path in files)
                {
                    string bas = Path.GetFileNameWithoutExtension(path);
                    if (BadExe.IsMatch(bas)) continue;
                    double score = 0.0;
                    if (Shipping.IsMatch(bas)) { bas = Shipping.Replace(bas, ""); score -= 2; }
                    string parent = Path.GetFileName(Path.GetDirectoryName(path));
                    score += 3 * Similarity(bas, CleanName(parent));
                    score += 3 * Similarity(bas, folderClean);
                    score -= 0.6 * depth;
                    try { if (new FileInfo(path).Length > 500 * 1024) score += 0.5; } catch { }
                    if (Regex.IsMatch(bas, "tmp|temp|server|editor|config|settings", I)) score -= 1;
                    if (score > bestScore) { bestScore = score; best = path; }
                }
                if (depth < 5)
                {
                    string[] subs;
                    try { subs = Directory.GetDirectories(dir); } catch { continue; }
                    foreach (var s in subs)
                        if (!BadDir.IsMatch(Path.GetFileName(s))) queue.Enqueue(new KeyValuePair<string, int>(s, depth + 1));
                }
            }
            return best;
        }

        public static List<string> NameCandidates(string root, string exe)
        {
            var names = new List<string>();
            root = root.TrimEnd('\\');
            string dir = Path.GetDirectoryName(exe);
            var ue = Regex.Match(dir, @"^(.+)\\[^\\]+\\Binaries\\Win(64|32|GDK)$", I);
            if (ue.Success && ue.Groups[1].Value.Length >= root.Length) dir = ue.Groups[1].Value;
            while (dir.Length > root.Length && GenericDir.IsMatch(Path.GetFileName(dir))) dir = Path.GetDirectoryName(dir);
            while (dir.Length >= root.Length)
            {
                string c = CleanName(Path.GetFileName(dir));
                if (c.Length > 0 && !Regex.IsMatch(c, "^new folder", I) && !names.Contains(c)) names.Add(c);
                if (dir.Length == root.Length) break;
                dir = Path.GetDirectoryName(dir);
            }
            if (names.Count == 0) names.Add(CleanName(Path.GetFileNameWithoutExtension(exe)));
            return names;
        }

        public static void ReadVersion(FoundGame g)
        {
            string rel = g.Exe.Substring(Math.Max(0, (Path.GetDirectoryName(g.Folder) ?? "").Length));
            var v = Regex.Match(rel, @"(?:^|[\s._\-\\])v(\d+(?:\.\d+){1,3})", I);
            Version ver;
            if (v.Success && Version.TryParse(v.Groups[1].Value, out ver)) { g.Ver = ver; g.VersionText = "v" + v.Groups[1].Value; }
            var b = Regex.Match(rel, @"build[\s._-]*(\d{8})", I);
            DateTime bd;
            if (b.Success && DateTime.TryParseExact(b.Groups[1].Value, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out bd))
            {
                g.BuildDate = bd;
                if (g.VersionText == null) g.VersionText = "build " + bd.ToString("yyyy-MM-dd");
            }
            try
            {
                var exe = new FileInfo(g.Exe);
                DateTime newest = exe.LastWriteTimeUtc;
                long size = exe.Length;
                foreach (var f in exe.Directory.GetFiles())
                    if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                foreach (var d in exe.Directory.GetDirectories("*_Data"))
                    foreach (var f in d.GetFiles())
                        if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                g.FilesTime = newest;
                g.Fingerprint = size + "|" + newest.Ticks;
            }
            catch { g.Fingerprint = "?"; }
            if (g.VersionText == null && g.FilesTime > DateTime.MinValue) g.VersionText = "files from " + g.FilesTime.ToLocalTime().ToString("yyyy-MM-dd");
        }

        static bool LooksLikeCopy(string folder)
        {
            return Regex.IsMatch(Path.GetFileName(folder), @"\(\d+\)|^new folder", I);
        }

        public static int CompareNewness(FoundGame a, FoundGame b)
        {
            if (a.Ver != null && b.Ver != null && a.Ver != b.Ver) return a.Ver.CompareTo(b.Ver);
            if (a.BuildDate > DateTime.MinValue && b.BuildDate > DateTime.MinValue && a.BuildDate != b.BuildDate) return a.BuildDate.CompareTo(b.BuildDate);
            int c = a.FilesTime.CompareTo(b.FilesTime);
            if (c != 0) return c;
            return LooksLikeCopy(b.Folder).CompareTo(LooksLikeCopy(a.Folder));
        }
    }

    static class Steam
    {
        const double Threshold = 0.72;
        const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        public static MatchEntry Match(List<string> names)
        {
            MatchEntry best = null;
            double bestScore = 0.0;
            bool wantDemo = names.Any(n => Regex.IsMatch(n, "demo|prologue", I));
            foreach (var n in names)
            {
                var res = Json.Parse(Web.GetString("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(n)));
                foreach (var it in Json.Arr(res, "items"))
                {
                    string name = Json.Str(it, "name");
                    int id;
                    if (name == null || !int.TryParse(Json.Str(it, "id"), out id)) continue;
                    double score = 0.0;
                    for (int k = 0; k < names.Count; k++) score = Math.Max(score, Scanner.Similarity(names[k], name) * (1.0 - 0.06 * k));
                    if (!wantDemo && Regex.IsMatch(name, @"\b(demo|soundtrack|ost|playtest|prologue|dlc)\b", I)) score -= 0.15;
                    if (score > bestScore) { bestScore = score; best = new MatchEntry { AppId = id, Name = name, Score = Math.Round(score, 2) }; }
                }
                if (bestScore >= 0.99) break;
            }
            return best != null && bestScore >= Threshold ? best : null;
        }

        static readonly string[] DateFormats = { "d MMM, yyyy", "MMM d, yyyy", "d MMMM, yyyy", "MMMM d, yyyy", "MMM yyyy", "yyyy" };

        public static GameMeta Details(int appId)
        {
            var meta = new GameMeta();
            var root = Json.Parse(Web.GetString("https://store.steampowered.com/api/appdetails?l=english&cc=US&appids=" + appId));
            var data = Json.Get(Json.Get(root, appId.ToString()), "data");
            if (data != null)
            {
                meta.Name = Json.Str(data, "name");
                meta.Description = Json.Str(data, "about_the_game");
                foreach (var d in Json.Arr(data, "developers")) meta.Developers.Add(Convert.ToString(d));
                foreach (var p in Json.Arr(data, "publishers")) meta.Publishers.Add(Convert.ToString(p));
                foreach (var g in Json.Arr(data, "genres")) meta.Genres.Add(Json.Str(g, "description"));
                foreach (var c in Json.Arr(data, "categories"))
                {
                    var d = Json.Str(c, "description");
                    if (d != null && Regex.IsMatch(d, "single-player|multi-player|co-op|online|controller", I)) meta.Features.Add(d);
                }
                DateTime rd;
                var date = Json.Str(Json.Get(data, "release_date"), "date");
                if (date != null && DateTime.TryParseExact(date.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out rd))
                    meta.ReleaseDate = rd.ToString("yyyy-MM-dd");
                meta.CoverUrl = Json.Str(data, "header_image");
                meta.BackgroundUrl = Json.Str(data, "background_raw");
            }

            string input = "{\"ids\":[{\"appid\":" + appId + "}],\"context\":{\"language\":\"english\",\"country_code\":\"US\"},\"data_request\":{\"include_assets\":true}}";
            var items = Json.Arr(Json.Get(Json.Parse(Web.GetString("https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input))), "response"), "store_items");
            var assets = items.Length > 0 ? Json.Get(items[0], "assets") : null;
            if (assets != null)
            {
                string fmt = "https://shared.akamai.steamstatic.com/store_item_assets/" + Json.Str(assets, "asset_url_format");
                string cover = Json.Str(assets, "library_capsule_2x") ?? Json.Str(assets, "library_capsule");
                string hero = Json.Str(assets, "library_hero_2x") ?? Json.Str(assets, "library_hero");
                string icon = Json.Str(assets, "community_icon");
                if (cover != null) meta.CoverUrl = fmt.Replace("${FILENAME}", cover);
                if (hero != null) meta.BackgroundUrl = fmt.Replace("${FILENAME}", hero);
                if (icon != null) meta.IconUrl = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/" + appId + "/" + icon + ".jpg";
            }
            return meta;
        }
    }

    class Syncer
    {
        readonly Action<string> log;
        readonly Action<int, int, string> progress;
        readonly Func<string, bool> confirm;

        public Syncer(Action<string> log, Action<int, int, string> progress, Func<string, bool> confirm)
        {
            this.log = log; this.progress = progress; this.confirm = confirm;
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        static bool IsUnder(string path, string root)
        {
            return path != null && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public void Run(List<string> roots, bool autoRestart, bool dryRun = false)
        {
            var state = AppState.Load();
            state.Roots = roots;
            state.AutoRestart = autoRestart;
            var report = new List<string>();

            var found = new List<FoundGame>();
            var noExe = new List<string>();
            var dirs = new List<string>();
            foreach (var r in roots)
            {
                if (!Directory.Exists(r)) { log("Folder not found, skipped: " + r); continue; }
                try { dirs.AddRange(Directory.GetDirectories(r).Where(Scanner.IsGameFolderCandidate)); }
                catch (Exception ex) { log("Can't read " + r + ": " + ex.Message); }
            }
            log("Scanning " + dirs.Count + " folders...");
            for (int i = 0; i < dirs.Count; i++)
            {
                progress(i, dirs.Count, "Scanning " + Path.GetFileName(dirs[i]));
                string exe = Scanner.FindGameExe(dirs[i]);
                if (exe == null) { noExe.Add(dirs[i]); continue; }
                var g = new FoundGame { Folder = dirs[i], Exe = exe, Names = Scanner.NameCandidates(dirs[i], exe) };
                Scanner.ReadVersion(g);
                found.Add(g);
            }

            log("Identifying " + found.Count + " games on Steam...");
            for (int i = 0; i < found.Count; i++)
            {
                var g = found[i];
                progress(i, found.Count, "Identifying " + g.Names[0]);
                string cacheKey = (g.Folder + "|" + g.Exe).ToLowerInvariant();
                MatchEntry m;
                if (!state.Matches.TryGetValue(cacheKey, out m))
                {
                    m = Steam.Match(g.Names);
                    if (m != null) state.Matches[cacheKey] = m;
                }
                if (m != null) { g.AppId = m.AppId; g.Name = m.Name; g.Score = m.Score; g.Key = "steam:" + m.AppId; }
                else { g.Name = g.Names[0]; g.Key = "local:" + Scanner.Norm(g.Names[0]); }
            }

            var entries = new List<Dictionary<string, object>>();
            var toFetch = new List<KeyValuePair<FoundGame, Dictionary<string, object>>>();
            int unchanged = 0;
            var groups = found.GroupBy(g => g.Key).ToList();
            foreach (var grp in groups)
            {
                var copies = grp.ToList();
                copies.Sort((a, b) => -Scanner.CompareNewness(a, b));
                var newest = copies[0];
                foreach (var dup in copies.Skip(1))
                    report.Add("DUPLICATE   " + newest.Name + "  - extra copy at " + dup.Folder + " (" + dup.VersionText + "); using the newer " + newest.Folder + " (" + newest.VersionText + ")");

                GameState old;
                state.Games.TryGetValue(grp.Key, out old);
                Dictionary<string, object> e = null;
                if (old == null)
                {
                    e = Entry("add", newest, null);
                    e["note"] = "[" + newest.VersionText + "]  <- " + newest.Folder;
                    toFetch.Add(new KeyValuePair<FoundGame, Dictionary<string, object>>(newest, e));
                }
                else if (!Same(old.Exe, newest.Exe))
                {
                    e = Entry("update", newest, old);
                    e["note"] = (old.Version ?? "old") + " -> " + newest.VersionText + "  (game exe replaced: " + old.Exe + "  ->  " + newest.Exe + ")";
                }
                else if (old.Fingerprint != newest.Fingerprint)
                {
                    e = Entry("update", newest, old);
                    e["note"] = (old.Version ?? "old") + " -> " + newest.VersionText + "  (game files were updated in place)";
                }
                else unchanged++;

                if (e != null) entries.Add(e);
                state.Games[grp.Key] = new GameState { Name = newest.Name, Folder = newest.Folder, Exe = newest.Exe, Version = newest.VersionText, Fingerprint = newest.Fingerprint, AppId = newest.AppId };
            }

            var foundKeys = new HashSet<string>(groups.Select(g => g.Key));
            foreach (var kv in state.Games.ToList())
            {
                if (foundKeys.Contains(kv.Key)) continue;
                if (!roots.Any(r => IsUnder(kv.Value.Folder, r))) continue;
                if (File.Exists(kv.Value.Exe)) continue;
                var e = new Dictionary<string, object>();
                e["key"] = kv.Key; e["action"] = "missing"; e["name"] = kv.Value.Name;
                e["oldDir"] = kv.Value.Folder; e["oldExe"] = kv.Value.Exe; e["appId"] = kv.Value.AppId;
                entries.Add(e);
                state.Games.Remove(kv.Key);
            }

            foreach (var d in noExe) report.Add("NO GAME EXE " + d + "  - nothing playable inside (unfinished download/extract?)");

            for (int i = 0; i < toFetch.Count; i++)
            {
                var g = toFetch[i].Key;
                var e = toFetch[i].Value;
                progress(i, toFetch.Count, "Downloading art for " + g.Name);
                string dir = Path.Combine(Paths.Cache, Regex.Replace(g.Key, @"[^a-zA-Z0-9_-]", "_"));
                Directory.CreateDirectory(dir);
                if (g.AppId > 0)
                {
                    try
                    {
                        var meta = Steam.Details(g.AppId);
                        if (!string.IsNullOrEmpty(meta.Name)) e["name"] = meta.Name;
                        e["description"] = meta.Description;
                        e["genres"] = meta.Genres; e["developers"] = meta.Developers;
                        e["publishers"] = meta.Publishers; e["features"] = meta.Features;
                        e["releaseDate"] = meta.ReleaseDate;
                        e["cover"] = Fetch(meta.CoverUrl, Path.Combine(dir, "cover.jpg"));
                        e["background"] = Fetch(meta.BackgroundUrl, Path.Combine(dir, "background.jpg"));
                        e["icon"] = Fetch(meta.IconUrl, Path.Combine(dir, "icon.jpg"));
                    }
                    catch (Exception ex) { log("Couldn't get details for " + g.Name + ": " + ex.Message); }
                }
                if (e.ContainsKey("icon") == false || e["icon"] == null) e["icon"] = ExeIcon(g.Exe, Path.Combine(dir, "icon.png"));
                if (g.AppId == 0) report.Add("NO MATCH    " + g.Name + "  - not found on Steam, added with the folder name (tagged 'Needs metadata')");
            }

            progress(1, 1, "");
            log("");
            log("Found " + groups.Count + " games: " + toFetch.Count + " new, " +
                entries.Count(x => (string)x["action"] == "update") + " updated, " +
                entries.Count(x => (string)x["action"] == "missing") + " missing, " + unchanged + " unchanged.");

            Dictionary<string, object> result = null;
            if (dryRun)
            {
                log("Dry run - nothing was changed.");
            }
            else if (entries.Count == 0 && !ExtensionOutdated())
            {
                log("Everything is already up to date - nothing to change in Playnite.");
                state.Save();
            }
            else
            {
                if (entries.Count == 0) log("No game changes, but the Game Sorter helper inside Playnite needs updating - restarting Playnite once.");
                string runId = Guid.NewGuid().ToString("N");
                WritePending(runId, entries);
                state.Save();
                result = ApplyInPlaynite(runId, autoRestart);
            }

            log("");
            log("==================== REPORT ====================");
            if (result != null)
            {
                foreach (var l in Json.Arr(result, "lines")) log(Convert.ToString(l));
                var c = Json.Get(result, "counts");
                log("");
                log("Playnite: " + Json.Str(c, "added") + " added, " + Json.Str(c, "updated") + " updated, " +
                    Json.Str(c, "missing") + " marked missing, " + Json.Str(c, "skipped") + " skipped (already on Steam), " +
                    Json.Str(c, "same") + " already up to date, " + Json.Str(c, "errors") + " errors.");
            }
            else if (entries.Count > 0)
            {
                foreach (var e in entries)
                    log(((string)e["action"]).ToUpperInvariant().PadRight(12) + e["name"] + "  " + (e.ContainsKey("note") ? e["note"] : "") + "  (queued)");
            }
            if (report.Count > 0) { log(""); foreach (var l in report) log(l); }
        }

        static Dictionary<string, object> Entry(string action, FoundGame g, GameState old)
        {
            var e = new Dictionary<string, object>();
            e["key"] = g.Key;
            e["action"] = action;
            e["name"] = old != null ? old.Name : g.Name;
            e["dir"] = g.Folder;
            e["exe"] = g.Exe;
            e["workDir"] = Path.GetDirectoryName(g.Exe);
            e["appId"] = g.AppId;
            e["version"] = g.VersionText;
            if (old != null) { e["oldDir"] = old.Folder; e["oldExe"] = old.Exe; }
            return e;
        }

        static string Fetch(string url, string path)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            return Web.Download(url, path) ? path : null;
        }

        static string ExeIcon(string exe, string path)
        {
            try
            {
                using (var ico = Icon.ExtractAssociatedIcon(exe))
                using (var bmp = ico.ToBitmap())
                    bmp.Save(path, ImageFormat.Png);
                return path;
            }
            catch { return null; }
        }

        static void WritePending(string runId, List<Dictionary<string, object>> entries)
        {
            Directory.CreateDirectory(Paths.Data);
            var all = new List<object>();
            var keys = new HashSet<string>(entries.Select(x => (string)x["key"]));
            try
            {
                if (File.Exists(Paths.Pending))
                    foreach (var old in Json.Arr(Json.Parse(File.ReadAllText(Paths.Pending, Encoding.UTF8)), "games"))
                        if (!keys.Contains(Json.Str(old, "key"))) all.Add(old);
            }
            catch { }
            all.AddRange(entries.Cast<object>());
            var job = new Dictionary<string, object>();
            job["runId"] = runId;
            job["created"] = DateTime.Now.ToString("s");
            job["games"] = all;
            File.WriteAllText(Paths.Pending, Json.Write(job), new UTF8Encoding(false));
        }

        static bool ExtensionOutdated()
        {
            try
            {
                var installed = Path.Combine(Paths.Extension, "GameSorter.psm1");
                if (!File.Exists(installed)) return true;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameSorter.psm1"))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return !ms.ToArray().SequenceEqual(File.ReadAllBytes(installed));
                }
            }
            catch { return true; }
        }

        static void InstallExtension()
        {
            Directory.CreateDirectory(Paths.Extension);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "GameSorter.psm1", "extension.yaml" })
            {
                using (var s = asm.GetManifestResourceStream(name))
                using (var f = File.Create(Path.Combine(Paths.Extension, name)))
                    s.CopyTo(f);
            }
        }

        void BackupLibrary()
        {
            try
            {
                if (!Directory.Exists(Paths.PlayniteLibrary)) return;
                string dest = Path.Combine(Paths.Backups, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(dest);
                foreach (var f in Directory.GetFiles(Paths.PlayniteLibrary))
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
                foreach (var old in Directory.GetDirectories(Paths.Backups).OrderByDescending(d => d).Skip(5))
                    Directory.Delete(old, true);
                log("Backed up the Playnite library to " + dest);
            }
            catch (Exception ex) { log("Couldn't back up the Playnite library: " + ex.Message); }
        }

        static bool PlayniteRunning()
        {
            return Process.GetProcessesByName("Playnite.DesktopApp").Length > 0 || Process.GetProcessesByName("Playnite.FullscreenApp").Length > 0;
        }

        Dictionary<string, object> ApplyInPlaynite(string runId, bool autoRestart)
        {
            try { InstallExtension(); }
            catch (Exception ex) { log("Couldn't install the Playnite helper: " + ex.Message); return null; }

            string exe = Paths.PlayniteExe;
            if (exe == null)
            {
                log("Playnite wasn't found. The changes are saved and will be applied the next time Playnite starts.");
                return null;
            }

            if (PlayniteRunning())
            {
                if (!autoRestart && !confirm("Playnite needs to restart to apply the changes. Restart it now?"))
                {
                    log("Changes saved - they'll be applied the next time Playnite starts.");
                    return null;
                }
                log("Closing Playnite...");
                progress(0, 1, "Closing Playnite");
                try { Process.Start(exe, "--shutdown"); } catch { }
                var until = DateTime.Now.AddSeconds(60);
                while (PlayniteRunning() && DateTime.Now < until) Thread.Sleep(500);
                if (PlayniteRunning())
                {
                    log("Playnite didn't close by itself. Close it, and the changes will be applied when it next starts.");
                    return null;
                }
                Thread.Sleep(1500);
            }

            BackupLibrary();

            try { File.Delete(Paths.Result); } catch { }
            log("Starting Playnite to apply the changes...");
            progress(0, 1, "Waiting for Playnite to apply the changes");
            Process.Start(exe);
            var deadline = DateTime.Now.AddMinutes(5);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                if (!File.Exists(Paths.Result)) continue;
                try
                {
                    var r = Json.Parse(File.ReadAllText(Paths.Result, Encoding.UTF8));
                    if (Json.Str(r, "runId") == runId) return r as Dictionary<string, object>;
                }
                catch { }
            }
            log("Playnite didn't report back within 5 minutes. The changes stay queued and apply the next time Playnite starts.");
            return null;
        }
    }

    class MainForm : Form
    {
        readonly ListBox lstRoots = new ListBox();
        readonly Button btnAdd = new Button(), btnChange = new Button(), btnRemove = new Button(), btnRun = new Button(), btnLogs = new Button();
        readonly CheckBox chkRestart = new CheckBox();
        readonly ProgressBar bar = new ProgressBar();
        readonly Label lblStatus = new Label();
        readonly TextBox txtLog = new TextBox();
        readonly StringBuilder logText = new StringBuilder();
        AppState state;
        bool busy;

        public MainForm()
        {
            Text = "Game Sorter for Playnite";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 650);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(10, 8, 10, 8) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lbl = new Label { Text = "Game folders - every sub-folder inside is treated as one game (double-click a folder to change it):", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
            top.Controls.Add(lbl, 0, 0);
            top.SetColumnSpan(lbl, 2);

            lstRoots.Dock = DockStyle.Fill;
            lstRoots.Height = 120;
            lstRoots.IntegralHeight = false;
            lstRoots.HorizontalScrollbar = true;
            lstRoots.SelectedIndexChanged += (s, e) => UpdateButtons();
            lstRoots.DoubleClick += (s, e) => ChangeFolder();
            top.Controls.Add(lstRoots, 0, 1);

            var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
            foreach (var b in new[] { btnAdd, btnChange, btnRemove })
            {
                b.Size = new Size(170, 36);
                b.Margin = new Padding(0, 0, 0, 6);
                side.Controls.Add(b);
            }
            btnAdd.Text = "Add folder...";
            btnAdd.Click += (s, e) => AddFolder();
            btnChange.Text = "Change folder...";
            btnChange.Click += (s, e) => ChangeFolder();
            btnRemove.Text = "Remove folder";
            btnRemove.Click += (s, e) => RemoveFolder();
            top.Controls.Add(side, 1, 1);

            chkRestart.Text = "Restart Playnite automatically to apply changes";
            chkRestart.AutoSize = true;
            chkRestart.Margin = new Padding(3, 8, 3, 4);
            chkRestart.CheckedChanged += (s, e) => SaveSettings();
            top.Controls.Add(chkRestart, 0, 2);
            top.SetColumnSpan(chkRestart, 2);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            btnRun.Text = "Scan && Sync";
            btnRun.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
            btnRun.Size = new Size(200, 42);
            btnRun.Click += (s, e) => StartSync();
            actions.Controls.Add(btnRun, 0, 0);

            btnLogs.Text = "Open logs";
            btnLogs.Size = new Size(130, 42);
            btnLogs.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", "\"" + Paths.Logs + "\""); };
            actions.Controls.Add(btnLogs, 1, 0);

            var progressBox = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1, Margin = new Padding(8, 0, 3, 0) };
            progressBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.AutoEllipsis = true;
            lblStatus.Height = 20;
            bar.Dock = DockStyle.Fill;
            bar.Height = 20;
            progressBox.Controls.Add(lblStatus, 0, 0);
            progressBox.Controls.Add(bar, 0, 1);
            actions.Controls.Add(progressBox, 2, 0);

            top.Controls.Add(actions, 0, 3);
            top.SetColumnSpan(actions, 2);

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Both;
            txtLog.WordWrap = false;
            txtLog.Font = new Font("Consolas", 9.5f);
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = SystemColors.Window;

            Controls.Add(txtLog);
            Controls.Add(top);

            state = AppState.Load();
            foreach (var r in state.Roots) lstRoots.Items.Add(r);
            if (lstRoots.Items.Count > 0) lstRoots.SelectedIndex = 0;
            chkRestart.Checked = state.AutoRestart;
            UpdateButtons();

            Log("Game Sorter " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            Log("1. Add the folder(s) where your games are (e.g. F:\\games). Use Change folder or Remove folder to edit the list.");
            Log("2. Press Scan & Sync. New games are added to Playnite with names, covers and details;");
            Log("   run it again any time to pick up new games, newer versions and deleted games.");
            Log("");
            Shown += (s, e) => { if (lstRoots.Items.Count == 0) AddFolder(); };
        }

        string PickFolder(string startAt)
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose a folder that contains your games (each game in its own sub-folder)", ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(startAt) && Directory.Exists(startAt)) dlg.SelectedPath = startAt;
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                var path = dlg.SelectedPath.TrimEnd('\\');
                if (path.Length == 2) path += "\\";
                return path;
            }
        }

        int IndexOfFolder(string path)
        {
            for (int i = 0; i < lstRoots.Items.Count; i++)
                if (string.Equals((string)lstRoots.Items[i], path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void AddFolder()
        {
            var path = PickFolder(null);
            if (path == null) return;
            int existing = IndexOfFolder(path);
            if (existing >= 0) { lstRoots.SelectedIndex = existing; return; }
            lstRoots.Items.Add(path);
            lstRoots.SelectedIndex = lstRoots.Items.Count - 1;
            SaveSettings();
        }

        void ChangeFolder()
        {
            if (busy) return;
            int index = lstRoots.SelectedIndex;
            if (index < 0) { AddFolder(); return; }
            var path = PickFolder((string)lstRoots.Items[index]);
            if (path == null) return;
            int existing = IndexOfFolder(path);
            if (existing >= 0 && existing != index)
            {
                lstRoots.Items.RemoveAt(index);
                lstRoots.SelectedIndex = IndexOfFolder(path);
            }
            else
            {
                lstRoots.Items[index] = path;
                lstRoots.SelectedIndex = index;
            }
            SaveSettings();
        }

        void RemoveFolder()
        {
            int index = lstRoots.SelectedIndex;
            if (index < 0) return;
            lstRoots.Items.RemoveAt(index);
            if (lstRoots.Items.Count > 0) lstRoots.SelectedIndex = Math.Min(index, lstRoots.Items.Count - 1);
            SaveSettings();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool selected = lstRoots.SelectedIndex >= 0;
            btnAdd.Enabled = !busy;
            btnChange.Enabled = !busy && selected;
            btnRemove.Enabled = !busy && selected;
        }

        void SaveSettings()
        {
            if (state == null) return;
            state = AppState.Load();
            state.Roots = lstRoots.Items.Cast<string>().ToList();
            state.AutoRestart = chkRestart.Checked;
            try { state.Save(); } catch { }
        }

        void Log(string line)
        {
            logText.AppendLine(line);
            if (InvokeRequired) BeginInvoke(new Action(() => txtLog.AppendText(line + Environment.NewLine)));
            else txtLog.AppendText(line + Environment.NewLine);
        }

        void Progress(int value, int max, string text)
        {
            BeginInvoke(new Action(() =>
            {
                bar.Maximum = Math.Max(1, max);
                bar.Value = Math.Max(0, Math.Min(value, bar.Maximum));
                lblStatus.Text = text;
            }));
        }

        bool Confirm(string question)
        {
            return (bool)Invoke(new Func<bool>(() => MessageBox.Show(this, question, "Game Sorter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        }

        void StartSync()
        {
            if (lstRoots.Items.Count == 0) { AddFolder(); if (lstRoots.Items.Count == 0) return; }
            var roots = lstRoots.Items.Cast<string>().ToList();
            bool restart = chkRestart.Checked;
            SetBusy(true);
            txtLog.Clear();
            logText.Clear();
            Log("Sync started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            var t = new Thread(() =>
            {
                try { new Syncer(Log, Progress, Confirm).Run(roots, restart); }
                catch (Exception ex) { Log(""); Log("Something went wrong: " + ex); }
                string file = null;
                try
                {
                    Directory.CreateDirectory(Paths.Logs);
                    file = Path.Combine(Paths.Logs, "sync_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
                    File.WriteAllText(file, logText.ToString(), new UTF8Encoding(true));
                }
                catch { }
                if (file != null) { Log(""); Log("Log saved to " + file); }
                BeginInvoke(new Action(() => { SetBusy(false); lblStatus.Text = "Done"; bar.Value = bar.Maximum; }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetBusy(bool isBusy)
        {
            busy = isBusy;
            btnRun.Enabled = chkRestart.Enabled = !isBusy;
            UpdateButtons();
            UseWaitCursor = isBusy;
        }
    }
}
