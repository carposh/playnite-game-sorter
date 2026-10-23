using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
[assembly: AssemblyVersion("2.3.0.0")]
[assembly: AssemblyFileVersion("2.3.0.0")]

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
            string flag = args.Length > 1 ? args[1] : "";
            bool quiet = args.Contains("--quiet");
            if (flag == "--uninstall") { Installer.Uninstall(quiet); return; }
            if (flag == "--install") { Installer.InstallFrom(Installer.Self, Installer.InstalledVersion() == null); return; }
            if (flag == "--update")
            {
                var latest = Installer.CheckLatest();
                string error;
                if (latest != null && latest.IsNewer) Installer.DownloadAndInstall(latest, out error);
                return;
            }
            Installer.CleanupOld();
            if (!Installer.IsInstalledCopy && !Installer.OfferInstall()) return;
            Application.Run(new MainForm());
        }
    }

    class UpdateInfo
    {
        public Version Version;
        public string Url, Notes;
        public bool IsNewer { get { return Installer.Normalize(Version) > Installer.Normalize(Installer.Current); } }
    }

    static class Installer
    {
        public const string Repo = "carposh/playnite-game-sorter";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GameSorter";
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GameSorter");
        public static readonly string Exe = Path.Combine(Dir, "GameSorter.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Game Sorter.lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Game Sorter.lnk");

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string Self { get { return Assembly.GetExecutingAssembly().Location; } }

        public static Version Normalize(Version v)
        {
            return v == null ? new Version(0, 0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInstalledCopy { get { return SamePath(Self, Exe); } }

        public static Version InstalledVersion()
        {
            try { return File.Exists(Exe) ? AssemblyName.GetAssemblyName(Exe).Version : null; }
            catch { return null; }
        }

        public static void CleanupOld()
        {
            try { foreach (var f in Directory.GetFiles(Dir, "*.old")) try { File.Delete(f); } catch { } }
            catch { }
        }

        public static void Launch()
        {
            Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir });
        }

        public static bool OfferInstall()
        {
            var installed = InstalledVersion();
            var current = Normalize(Current);
            if (installed != null && Normalize(installed) >= current) { Launch(); return false; }
            string question = installed == null
                ? "Install Game Sorter on this PC?\n\nIt will be added to the Start menu and your desktop, and listed in Windows' Installed apps so you can uninstall it any time. Updates will install themselves.\n\nChoose No to just run it this once without installing."
                : "Game Sorter " + installed.ToString(3) + " is installed. Replace it with this version (" + current.ToString(3) + ")?\n\nChoose No to just run this copy without installing.";
            if (MessageBox.Show(question, "Game Sorter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
            try { InstallFrom(Self, installed == null); }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't install Game Sorter: " + ex.Message + "\n\nIt will run from where it is instead.", "Game Sorter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
            Launch();
            return false;
        }

        public static void InstallFrom(string source, bool createDesktopShortcut)
        {
            Directory.CreateDirectory(Dir);
            if (!SamePath(source, Exe))
            {
                if (File.Exists(Exe))
                {
                    try { File.Delete(Exe); }
                    catch { File.Move(Exe, Path.Combine(Dir, "GameSorter." + Guid.NewGuid().ToString("N") + ".old")); }
                }
                File.Copy(source, Exe, true);
            }
            Register();
            CreateShortcut(StartMenuLink);
            if (createDesktopShortcut || File.Exists(DesktopLink)) CreateShortcut(DesktopLink);
        }

        static void Register()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                var version = Normalize(AssemblyName.GetAssemblyName(Exe).Version).ToString(3);
                k.SetValue("DisplayName", "Game Sorter for Playnite");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "carposh");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + Exe + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", "https://github.com/" + Repo);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        static void CreateShortcut(string linkPath)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Exe });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Dir });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Exe + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Add and update your PC games in Playnite" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall Game Sorter?\n\nYour games stay in Playnite. Your settings, logs and Playnite library backups are kept in " + Paths.Data + ".",
                "Game Sorter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var link in new[] { StartMenuLink, DesktopLink }) try { File.Delete(link); } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { if (Directory.Exists(Paths.Extension)) Directory.Delete(Paths.Extension, true); } catch { }
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath()
                });
            }
            catch { }
            if (!quiet) MessageBox.Show("Game Sorter has been uninstalled.", "Game Sorter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static UpdateInfo CheckLatest()
        {
            var release = Json.Parse(Web.GetString("https://api.github.com/repos/" + Repo + "/releases/latest"));
            if (release == null) return null;
            Version version;
            var tag = (Json.Str(release, "tag_name") ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out version)) return null;
            foreach (var asset in Json.Arr(release, "assets"))
            {
                if (string.Equals(Json.Str(asset, "name"), "GameSorter.exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo { Version = version, Url = Json.Str(asset, "browser_download_url"), Notes = Json.Str(release, "body") };
            }
            return null;
        }

        public static bool DownloadAndInstall(UpdateInfo update, out string error)
        {
            error = null;
            var tmp = Path.Combine(Path.GetTempPath(), "GameSorter-update-" + Normalize(update.Version).ToString(3) + ".exe");
            try
            {
                if (!Web.Download(update.Url, tmp)) { error = "The download from GitHub failed."; return false; }
                Version got;
                try { got = AssemblyName.GetAssemblyName(tmp).Version; }
                catch { error = "The downloaded file isn't a valid Game Sorter exe."; return false; }
                if (Normalize(got) != Normalize(update.Version)) { error = "The downloaded file is version " + got + ", expected " + update.Version + "."; return false; }
                InstallFrom(tmp, InstalledVersion() == null);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { File.Delete(tmp); } catch { } }
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

    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(245, 244, 238);
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color Border = Color.FromArgb(222, 219, 208);
        public static readonly Color Text = Color.FromArgb(31, 30, 29);
        public static readonly Color Muted = Color.FromArgb(115, 112, 105);
        public static readonly Color LogText = Color.FromArgb(62, 60, 56);
        public static readonly Color Accent = Color.FromArgb(193, 95, 60);
        public static readonly Color AccentHover = Color.FromArgb(172, 82, 50);
        public static readonly Color AccentPressed = Color.FromArgb(150, 70, 42);
        public static readonly Color Subtle = Color.FromArgb(240, 238, 231);
        public static readonly Color SubtlePressed = Color.FromArgb(230, 227, 218);
        public static readonly Color Selection = Color.FromArgb(246, 233, 225);
        public static readonly Color Track = Color.FromArgb(232, 229, 220);
        public static readonly Color Disabled = Color.FromArgb(226, 223, 214);

        static readonly HashSet<string> Installed = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        static string Pick(params string[] names)
        {
            foreach (var n in names) if (Installed.Contains(n)) return n;
            return names[names.Length - 1];
        }

        public static readonly string SerifDisplay = Pick("Sitka Display", "Georgia");
        public static readonly string SerifHeading = Pick("Sitka Subheading", "Georgia");
        public static readonly string Sans = Pick("Segoe UI Variable Text", "Segoe UI");
        public static readonly string SansStrong = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
        public static readonly string Mono = Pick("Cascadia Mono", "Consolas");
        public static readonly string Icons = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Label SectionTitle(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(SerifHeading, 14f), ForeColor = Text, BackColor = Surface, Margin = new Padding(0) };
        }

        public static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(Sans, 9.5f), ForeColor = Muted, BackColor = Surface, Margin = new Padding(1, 3, 0, 0) };
        }
    }

    class Card : Panel
    {
        public float Radius = 12;
        public Color Fill = Theme.Surface;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            Padding = new Padding(22, 18, 22, 20);
            Margin = new Padding(0, 0, 0, 16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius))
            using (var fill = new SolidBrush(Fill))
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    class FlatButton : Control
    {
        readonly bool primary;
        bool hover, pressed;

        public FlatButton(string text, bool primary)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            this.primary = primary;
            Text = text;
            Font = new Font(primary ? Theme.SansStrong : Theme.Sans, 10f);
            Cursor = Cursors.Hand;
            Size = new Size(140, 38);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, border, text;
            if (!Enabled) { fill = primary ? Theme.Disabled : Theme.Surface; border = Theme.Disabled; text = Theme.Muted; }
            else if (primary) { fill = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent; border = fill; text = Color.White; }
            else { fill = pressed ? Theme.SubtlePressed : hover ? Theme.Subtle : Theme.Surface; border = Theme.Border; text = Theme.Text; }
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                using (var path = Theme.Rounded(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), 6))
                using (var pen = new Pen(Color.FromArgb(110, primary ? Color.White : Theme.Text)))
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    class ThinProgress : Control
    {
        int current, max = 100;

        public ThinProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 6;
        }

        public int Maximum { get { return max; } set { max = Math.Max(1, value); current = Math.Min(current, max); Invalidate(); } }
        public int Value { get { return current; } set { current = Math.Max(0, Math.Min(value, max)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float y = (Height - 4) / 2f;
            using (var track = Theme.Rounded(new RectangleF(0, y, Width - 1, 4), 2))
            using (var brush = new SolidBrush(Theme.Track))
                g.FillPath(brush, track);
            if (current <= 0) return;
            float w = Math.Max(4, (Width - 1) * (float)current / max);
            using (var fill = Theme.Rounded(new RectangleF(0, y, w, 4), 2))
            using (var brush = new SolidBrush(Theme.Accent))
                g.FillPath(brush, fill);
        }
    }

    class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        readonly ListBox lstRoots = new ListBox();
        readonly FlatButton btnAdd = new FlatButton("Add folder", false), btnChange = new FlatButton("Change", false), btnRemove = new FlatButton("Remove", false);
        readonly FlatButton btnRun = new FlatButton("Scan & Sync", true), btnLogs = new FlatButton("Open logs", false), btnUpdate = new FlatButton("Check for updates", false);
        readonly CheckBox chkRestart = new CheckBox();
        readonly ThinProgress bar = new ThinProgress();
        readonly Label lblStatus = new Label();
        readonly TextBox txtLog = new TextBox();
        readonly StringBuilder logText = new StringBuilder();
        readonly Font iconFont = new Font(Theme.Icons, 12f);
        AppState state;
        bool busy;

        public MainForm()
        {
            Text = "Game Sorter";
            Font = new Font(Theme.Sans, 10f);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(940, 740);
            MinimumSize = new Size(720, 660);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(30, 22, 30, 26), BackColor = Theme.Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 262));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 18) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "Game Sorter", AutoSize = true, Font = new Font(Theme.SerifDisplay, 26f), ForeColor = Theme.Text, Margin = new Padding(0) };
            var subtitle = new Label { Text = "Bring every game on your PC into Playnite, with names, artwork and details.", AutoSize = true, Font = new Font(Theme.Sans, 10.5f), ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 0) };
            var version = new Label { Text = "Version " + Installer.Normalize(Installer.Current).ToString(3), AutoSize = true, Font = new Font(Theme.Sans, 9f), ForeColor = Theme.Muted, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(0, 0, 0, 8) };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(version, 1, 0);
            header.Controls.Add(subtitle, 0, 1);
            header.SetColumnSpan(subtitle, 2);
            root.Controls.Add(header, 0, 0);

            var folders = new Card { Dock = DockStyle.Fill };
            var folderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0) };
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            folderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var folderTitles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 12) };
            folderTitles.Controls.Add(Theme.SectionTitle("Game folders"));
            folderTitles.Controls.Add(Theme.Hint("Every sub-folder inside is treated as one game. Double-click a folder to change it."));
            folderLayout.Controls.Add(folderTitles, 0, 0);
            folderLayout.SetColumnSpan(folderTitles, 2);

            var well = new Card { Dock = DockStyle.Fill, Radius = 8, Padding = new Padding(6), Margin = new Padding(0), BackColor = Theme.Surface };
            lstRoots.Dock = DockStyle.Fill;
            lstRoots.BorderStyle = BorderStyle.None;
            lstRoots.BackColor = Theme.Surface;
            lstRoots.ForeColor = Theme.Text;
            lstRoots.Font = new Font(Theme.Sans, 10f);
            lstRoots.DrawMode = DrawMode.OwnerDrawFixed;
            lstRoots.ItemHeight = 34;
            lstRoots.IntegralHeight = false;
            lstRoots.DrawItem += DrawFolderItem;
            lstRoots.SelectedIndexChanged += (s, e) => { UpdateButtons(); lstRoots.Invalidate(); };
            lstRoots.DoubleClick += (s, e) => ChangeFolder();
            well.Controls.Add(lstRoots);
            folderLayout.Controls.Add(well, 0, 1);

            var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(14, 0, 0, 0) };
            foreach (var b in new[] { btnAdd, btnChange, btnRemove })
            {
                b.Size = new Size(132, 38);
                b.Margin = new Padding(0, 0, 0, 8);
                side.Controls.Add(b);
            }
            btnAdd.Click += (s, e) => AddFolder();
            btnChange.Click += (s, e) => ChangeFolder();
            btnRemove.Click += (s, e) => RemoveFolder();
            folderLayout.Controls.Add(side, 1, 1);
            folders.Controls.Add(folderLayout);
            root.Controls.Add(folders, 0, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 16) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            btnRun.Size = new Size(170, 44);
            btnRun.Margin = new Padding(0, 0, 10, 0);
            btnRun.Click += (s, e) => StartSync();
            actions.Controls.Add(btnRun, 0, 0);

            btnLogs.Size = new Size(120, 44);
            btnLogs.Margin = new Padding(0, 0, 10, 0);
            btnLogs.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", "\"" + Paths.Logs + "\""); };
            actions.Controls.Add(btnLogs, 1, 0);

            btnUpdate.Size = new Size(160, 44);
            btnUpdate.Margin = new Padding(0, 0, 10, 0);
            btnUpdate.Click += (s, e) => CheckForUpdates(false);
            actions.Controls.Add(btnUpdate, 2, 0);

            var progressBox = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Margin = new Padding(14, 0, 0, 0) };
            progressBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            progressBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.AutoEllipsis = true;
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.Font = new Font(Theme.Sans, 9.5f);
            lblStatus.TextAlign = ContentAlignment.BottomRight;
            lblStatus.Margin = new Padding(0);
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0, 4, 0, 0);
            progressBox.Controls.Add(lblStatus, 0, 0);
            progressBox.Controls.Add(bar, 0, 1);
            actions.Controls.Add(progressBox, 3, 0);

            chkRestart.Text = "Restart Playnite automatically to apply changes";
            chkRestart.AutoSize = true;
            chkRestart.ForeColor = Theme.Muted;
            chkRestart.Font = new Font(Theme.Sans, 9.5f);
            chkRestart.Margin = new Padding(1, 10, 0, 0);
            chkRestart.CheckedChanged += (s, e) => SaveSettings();
            actions.Controls.Add(chkRestart, 0, 1);
            actions.SetColumnSpan(chkRestart, 4);
            root.Controls.Add(actions, 0, 2);

            var logCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0) };
            logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logLayout.Controls.Add(Theme.SectionTitle("Activity"), 0, 0);
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = true;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.BackColor = Theme.Surface;
            txtLog.ForeColor = Theme.LogText;
            txtLog.Font = new Font(Theme.Mono, 9.5f);
            txtLog.Dock = DockStyle.Fill;
            txtLog.Margin = new Padding(0, 12, 0, 0);
            logLayout.Controls.Add(txtLog, 0, 1);
            logCard.Controls.Add(logLayout);
            root.Controls.Add(logCard, 0, 3);

            Controls.Add(root);

            state = AppState.Load();
            foreach (var r in state.Roots) lstRoots.Items.Add(r);
            if (lstRoots.Items.Count > 0) lstRoots.SelectedIndex = 0;
            chkRestart.Checked = state.AutoRestart;
            UpdateButtons();

            Log("Ready. Press Scan & Sync to add new games and pick up updates.");
            Log("Add, change or remove game folders above at any time.");
            Log("");
            Shown += (s, e) => { CheckForUpdates(true); if (lstRoots.Items.Count == 0) AddFolder(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int caption = ColorTranslator.ToWin32(Theme.Background);
                int text = ColorTranslator.ToWin32(Theme.Text);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }
        }

        void DrawFolderItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
            if (selected)
            {
                using (var path = Theme.Rounded(new RectangleF(e.Bounds.X + 1, e.Bounds.Y + 2, e.Bounds.Width - 3, e.Bounds.Height - 4), 6))
                using (var brush = new SolidBrush(Theme.Selection))
                    g.FillPath(brush, path);
            }
            var fill = selected ? Theme.Selection : Theme.Surface;
            var iconRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, 26, e.Bounds.Height);
            TextRenderer.DrawText(g, "", iconFont, iconRect, selected ? Theme.Accent : Theme.Muted, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var textRect = new Rectangle(e.Bounds.X + 42, e.Bounds.Y, e.Bounds.Width - 50, e.Bounds.Height);
            TextRenderer.DrawText(g, (string)lstRoots.Items[e.Index], lstRoots.Font, textRect, Theme.Text, fill,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
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

        void CheckForUpdates(bool silent)
        {
            if (busy) return;
            if (!silent) { btnUpdate.Enabled = false; lblStatus.Text = "Checking for updates..."; }
            var t = new Thread(() =>
            {
                UpdateInfo update = null;
                try { update = Installer.CheckLatest(); } catch { }
                try { BeginInvoke(new Action(() => OnUpdateChecked(update, silent))); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnUpdateChecked(UpdateInfo update, bool silent)
        {
            if (!silent) { btnUpdate.Enabled = !busy; lblStatus.Text = ""; }
            if (update == null)
            {
                if (!silent) MessageBox.Show(this, "Couldn't check for updates. Check your internet connection and try again.", "Game Sorter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string current = Installer.Normalize(Installer.Current).ToString(3);
            string latest = Installer.Normalize(update.Version).ToString(3);
            if (!update.IsNewer)
            {
                if (!silent) MessageBox.Show(this, "You have the latest version (" + current + ").", "Game Sorter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (busy)
            {
                Log("Game Sorter " + latest + " is available. Press Check for updates when the sync is done to install it.");
                return;
            }
            string notes = (update.Notes ?? "").Trim();
            if (notes.Length > 700) notes = notes.Substring(0, 700) + "...";
            var answer = MessageBox.Show(this, "Game Sorter " + latest + " is available (you have " + current + ").\n\n" + notes + "\n\nUpdate now? Game Sorter will restart.",
                "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) InstallUpdate(update);
            else Log("Update " + latest + " skipped for now. Press Check for updates to install it later.");
        }

        void InstallUpdate(UpdateInfo update)
        {
            SetBusy(true);
            string latest = Installer.Normalize(update.Version).ToString(3);
            lblStatus.Text = "Downloading Game Sorter " + latest + "...";
            Log("Downloading Game Sorter " + latest + "...");
            var t = new Thread(() =>
            {
                string error;
                bool ok = Installer.DownloadAndInstall(update, out error);
                BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        Log("Updated to " + latest + ". Restarting...");
                        try { Installer.Launch(); } catch { }
                        Close();
                        return;
                    }
                    SetBusy(false);
                    lblStatus.Text = "";
                    Log("Update failed: " + error);
                    MessageBox.Show(this, "The update couldn't be installed:\n\n" + error, "Game Sorter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetBusy(bool isBusy)
        {
            busy = isBusy;
            btnRun.Enabled = chkRestart.Enabled = btnUpdate.Enabled = !isBusy;
            UpdateButtons();
            UseWaitCursor = isBusy;
        }
    }
}
