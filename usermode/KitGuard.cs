using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace KitGuard
{
    sealed class Entry
    {
        public string Rel;
        public string Hash;
        public long Size;
        public long Ticks;
        public string Signer;
    }

    static class Program
    {
        const string AppName = "KitGuard";
        const string Version = "1.0.0";
        const string MutexName = "Local\\KitGuard_Main_9F3C7A";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "KitGuard";
        const string TaskName = "KitGuard_Resurrect";

        static string ExePath;
        static string DataDir;
        static string RootDir = "";
        static string VaultDir;
        static string ManifestPath;
        static string LogPath;
        static string ConfigPath;

        static bool Silent;
        static bool Hard;
        static bool NoWatchdog;
        static int CheckMs = 1000;
        static int TestSeconds;
        static int VerifySeconds = 600;
        static string Exts = "exe,dll,sys,com,bat";

        static volatile bool Stop;
        static readonly object Sync = new object();
        static readonly List<Entry> Entries = new List<Entry>();
        static readonly Dictionary<string, Entry> EntryMap = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Suppress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly object LogLock = new object();
        static Process WatchdogProcess;
        static bool IfeoWarned;
        static int RestoreCount;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            try { ExePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch { ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location; }

            string mode = "main";
            int wdPid = -1;
            string dataOverride = null;
            string dirOverride = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--install": mode = "install"; break;
                    case "--uninstall": mode = "uninstall"; break;
                    case "--status": mode = "status"; break;
                    case "--restore": mode = "restore"; break;
                    case "--refresh": mode = "refresh"; break;
                    case "--unprotect": mode = "unprotect"; break;
                    case "--resurrect": mode = "resurrect"; break;
                    case "--watchdog": mode = "watchdog"; if (i + 1 < args.Length) int.TryParse(args[++i], out wdPid); break;
                    case "--silent": Silent = true; break;
                    case "--hard": Hard = true; break;
                    case "--no-watchdog": NoWatchdog = true; break;
                    case "--dir": if (i + 1 < args.Length) dirOverride = args[++i]; break;
                    case "--vault": if (i + 1 < args.Length) dataOverride = args[++i]; break;
                    case "--check-ms": if (i + 1 < args.Length) int.TryParse(args[++i], out CheckMs); break;
                    case "--test-seconds": if (i + 1 < args.Length) int.TryParse(args[++i], out TestSeconds); break;
                    case "--verify-seconds": if (i + 1 < args.Length) int.TryParse(args[++i], out VerifySeconds); break;
                    case "--version": Console.WriteLine(AppName + " " + Version); return 0;
                    case "--help": PrintHelp(); return 0;
                }
            }

            if (dataOverride != null) DataDir = dataOverride;
            else DataDir = GetDefaultDataDir();

            VaultDir = Path.Combine(DataDir, "vault");
            ManifestPath = Path.Combine(DataDir, "manifest.tsv");
            ConfigPath = Path.Combine(DataDir, "KitGuard.ini");
            LogPath = Path.Combine(DataDir, "KitGuard.log");

            LoadConfig();

            if (dirOverride != null) RootDir = dirOverride;
            if (RootDir == null || RootDir.Length == 0)
            {
                if (Directory.Exists(@"D:\360compkill64")) RootDir = @"D:\360compkill64";
                else RootDir = Path.GetDirectoryName(ExePath);
            }
            RootDir = Path.GetFullPath(RootDir);

            if (Silent)
            {
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, 0);
            }

            try
            {
                switch (mode)
                {
                    case "install": return DoInstall();
                    case "uninstall": return DoUninstall();
                    case "status": return DoStatus();
                    case "restore": return DoRestoreNow();
                    case "refresh": return DoRefresh();
                    case "unprotect": return DoUnprotect();
                    case "resurrect": return DoResurrect();
                    case "watchdog": return DoWatchdog(wdPid);
                    default: return DoMain();
                }
            }
            catch (Exception ex)
            {
                string msg = "未知异常";
                try { msg = ex.GetType().FullName + ": " + ex.Message; } catch { }
                try { Console.WriteLine("错误: " + msg); } catch { }
                try { Log("致命错误: " + msg); } catch { }
                return 1;
            }
        }

        static string GetDefaultDataDir()
        {
            string pd = null;
            try { pd = Environment.GetEnvironmentVariable("ProgramData"); } catch { }
            if (string.IsNullOrEmpty(pd) || !Directory.Exists(pd))
            {
                try { pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } catch { }
            }
            try
            {
                if (string.IsNullOrEmpty(pd) || !Directory.Exists(pd))
                {
                    if (Directory.Exists(@"C:\ProgramData")) pd = @"C:\ProgramData";
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(pd) && Directory.Exists(pd)) return Path.Combine(pd, AppName);
            }
            catch { }
            try
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local) && Directory.Exists(local)) return Path.Combine(local, AppName);
            }
            catch { }
            return Path.Combine(Path.GetDirectoryName(ExePath), "data");
        }

        static void PrintHelp()
        {
            Console.WriteLine(AppName + " " + Version + " - 360急救箱守护程序");
            Console.WriteLine("");
            Console.WriteLine("用法: KitGuard.exe [选项]");
            Console.WriteLine("  --install      安装并启动守护(备份目录+开机自启+计划任务)");
            Console.WriteLine("  --uninstall    卸载守护(停止进程/删除自启/移除 ACL)");
            Console.WriteLine("  --status       查看守护状态");
            Console.WriteLine("  --restore      立即全量校验并恢复");
            Console.WriteLine("  --refresh      把当前目录重新作为基线(急救箱升级后使用)");
            Console.WriteLine("  --unprotect    移除硬保护 ACL");
            Console.WriteLine("  --dir <路径>   指定受保护目录(默认 D:\\360compkill64)");
            Console.WriteLine("  --vault <路径> 指定数据目录(默认 C:\\ProgramData\\KitGuard)");
            Console.WriteLine("  --hard         额外给关键文件加拒绝删除 ACL");
            Console.WriteLine("  --silent       静默运行(隐藏控制台)");
            Console.WriteLine("  --no-watchdog  不启动互护看门狗");
            Console.WriteLine("  --check-ms N   巡检间隔毫秒(默认1000)");
            Console.WriteLine("  --help         显示帮助");
        }

        // ---------------- 安装 / 卸载 ----------------

        static int DoInstall()
        {
            Log("安装开始: " + DataDir);
            Directory.CreateDirectory(DataDir);
            Log("数据目录已创建");
            Directory.CreateDirectory(VaultDir);

            string target = Path.Combine(DataDir, AppName + ".exe");
            StopAllInstances();
            System.Threading.Thread.Sleep(500);
            string self = Path.GetFullPath(ExePath);
            if (!string.Equals(self, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(ExePath, target, true);
                Log("已复制程序到 " + target);
            }

            try
            {
                string backup = Path.Combine(DataDir, "KitGuard.backup.exe");
                if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(backup), StringComparison.OrdinalIgnoreCase))
                    File.Copy(target, backup, true);
            }
            catch (Exception ex) { Log("备份自身失败: " + ex.Message); }

            SaveConfig();

            int n = Snapshot(false);
            Log("基线文件数: " + n);

            if (Hard) HardenAll();

            InstallRunKey(target);
            InstallTask(target);

            StartProcess(target, ChildArgs("--main"));

            Console.WriteLine("安装完成。");
            Console.WriteLine("  受保护目录: " + RootDir);
            Console.WriteLine("  数据/备份  : " + DataDir);
            Console.WriteLine("  文件数     : " + n);
            Console.WriteLine("  自启方式   : HKCU Run + 计划任务 " + TaskName);
            Console.WriteLine("  状态检查   : KitGuard.exe --status");
            return 0;
        }

        static int DoUninstall()
        {
            StopAllInstances();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    if (k != null) k.DeleteValue(RunValueName, false);
            }
            catch (Exception ex) { Console.WriteLine("删除自启失败: " + ex.Message); }

            RunTool("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
            UnprotectAll();
            Console.WriteLine("守护已卸载(备份保留在 " + DataDir + "，如需彻底删除请手动删除该目录)。");
            return 0;
        }

        static void StopAllInstances()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(AppName))
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }
            catch { }
        }

        static void InstallRunKey(string target)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                    k.SetValue(RunValueName, "\"" + target + "\" " + ChildArgs("--main"));
                Log("已写入开机自启: HKCU Run");
            }
            catch (Exception ex) { Log("写自启失败: " + ex.Message); }
        }

        static void InstallTask(string target)
        {
            try
            {
                string vbs = Path.Combine(DataDir, "resurrect.vbs");
                string q = "\"";
                string backup = Path.Combine(DataDir, "KitGuard.backup.exe");
                string cmd1 = q + target + q + " " + ChildArgs("--resurrect");
                string cmd2 = q + backup + q + " " + ChildArgs("--resurrect");
                string vbsText =
                    "Set fso = CreateObject(" + q + "Scripting.FileSystemObject" + q + ")" + Environment.NewLine +
                    "Set sh = CreateObject(" + q + "WScript.Shell" + q + ")" + Environment.NewLine +
                    "If fso.FileExists(" + q + target + q + ") Then" + Environment.NewLine +
                    "  sh.Run " + q + cmd1.Replace(q, q + q) + q + ", 0, False" + Environment.NewLine +
                    "ElseIf fso.FileExists(" + q + backup + q + ") Then" + Environment.NewLine +
                    "  sh.Run " + q + cmd2.Replace(q, q + q) + q + ", 0, False" + Environment.NewLine +
                    "End If" + Environment.NewLine;
                File.WriteAllText(vbs, vbsText, Encoding.Default);

                string tr = "wscript.exe //B //Nologo \"" + vbs + "\"";
                string outp = RunTool("schtasks.exe", "/Create /TN \"" + TaskName + "\" /TR \"" + tr + "\" /SC MINUTE /MO 10 /F");
                Log("计划任务输出: " + outp.Trim());
            }
            catch (Exception ex) { Log("创建计划任务失败: " + ex.Message); }
        }

        // ---------------- 配置 ----------------

        static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (string line in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "dir") RootDir = v;
                    else if (k == "hard") Hard = (v == "1" || v.ToLowerInvariant() == "true");
                    else if (k == "checkms") int.TryParse(v, out CheckMs);
                    else if (k == "exts" && v.Length > 0) Exts = v;
                }
            }
            catch { }
        }

        static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Dir=" + RootDir);
                sb.AppendLine("Hard=" + (Hard ? "1" : "0"));
                sb.AppendLine("CheckMs=" + CheckMs);
                sb.AppendLine("Exts=" + Exts);
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log("保存配置失败: " + ex.Message); }
        }

        // ---------------- 快照 / 基线 ----------------

        static int Snapshot(bool rebuild)
        {
            Directory.CreateDirectory(VaultDir);
            List<Entry> list;
            if (!rebuild && File.Exists(ManifestPath))
            {
                list = LoadManifest();
                Log("已载入基线: " + list.Count + " 个文件");
                foreach (Entry e in list)
                {
                    string vf = Path.Combine(VaultDir, e.Hash);
                    if (!File.Exists(vf))
                    {
                        string f = Path.Combine(RootDir, e.Rel);
                        if (File.Exists(f)) { try { File.Copy(f, vf, true); } catch { } }
                    }
                }
            }
            else
            {
                list = new List<Entry>();
                List<string> files = new List<string>();
                WalkFiles(RootDir, files);
                HashSet<string> exts = new HashSet<string>(Exts.Split(','), StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    string rel = GetRel(f);
                    if (IsExcluded(rel)) continue;
                    string ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
                    if (!exts.Contains(ext)) continue;
                    try
                    {
                        Entry e = BuildEntry(f);
                        EnsureVault(e);
                        list.Add(e);
                    }
                    catch (Exception ex) { Log("跳过 " + rel + ": " + ex.Message); }
                }
                SaveManifest(list);
                Log("已建立基线: " + list.Count + " 个文件");
            }

            lock (Sync)
            {
                Entries.Clear();
                Entries.AddRange(list);
                EntryMap.Clear();
                foreach (Entry e in list) EntryMap[e.Rel] = e;
            }
            return list.Count;
        }

        static List<Entry> LoadManifest()
        {
            List<Entry> list = new List<Entry>();
            if (!File.Exists(ManifestPath)) return list;
            foreach (string raw in File.ReadAllLines(ManifestPath, Encoding.UTF8))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('\t');
                if (p.Length < 4) continue;
                Entry e = new Entry();
                e.Rel = p[0];
                e.Hash = p[1];
                long tmp;
                if (long.TryParse(p[2], out tmp)) e.Size = tmp;
                if (long.TryParse(p[3], out tmp)) e.Ticks = tmp;
                e.Signer = p.Length > 4 ? p[4] : "";
                list.Add(e);
            }
            return list;
        }

        static void SaveManifest(List<Entry> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("#rel\thash\tsize\tticks\tsigner");
                foreach (Entry e in list)
                    sb.AppendLine(e.Rel + "\t" + e.Hash + "\t" + e.Size + "\t" + e.Ticks + "\t" + e.Signer);
                File.WriteAllText(ManifestPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log("保存基线失败: " + ex.Message); }
        }

        static Entry BuildEntry(string full)
        {
            FileInfo fi = new FileInfo(full);
            Entry e = new Entry();
            e.Rel = GetRel(full);
            e.Size = fi.Length;
            e.Ticks = fi.LastWriteTimeUtc.Ticks;
            e.Hash = ComputeHash(full);
            e.Signer = GetSigner(full);
            return e;
        }

        static void EnsureVault(Entry e)
        {
            string vf = Path.Combine(VaultDir, e.Hash);
            if (File.Exists(vf)) return;
            string f = Path.Combine(RootDir, e.Rel);
            if (File.Exists(f)) File.Copy(f, vf, true);
        }

        static void WalkFiles(string dir, List<string> outList)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir)) outList.Add(f);
            }
            catch { }
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(d);
                    if (name.Equals("_guard", StringComparison.OrdinalIgnoreCase)) continue;
                    WalkFiles(d, outList);
                }
            }
            catch { }
        }

        static bool IsExcluded(string rel)
        {
            if (rel.Equals("_guard", StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.StartsWith("_guard\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string GetRel(string full)
        {
            string r = Path.GetFullPath(full);
            string root = Path.GetFullPath(RootDir);
            if (r.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return r.Substring(root.Length).TrimStart('\\');
            return Path.GetFileName(full);
        }

        static string ComputeHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static string GetSigner(string path)
        {
            try
            {
                X509Certificate c = X509Certificate.CreateFromSignedFile(path);
                return c.GetCertHashString();
            }
            catch { return ""; }
        }

        // ---------------- 守护主循环 ----------------

        static int DoMain()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    if (!Silent) Console.WriteLine("守护已在运行。");
                    return 0;
                }

                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs ce) { ce.Cancel = true; Stop = true; };

                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(VaultDir);

                Snapshot(false);
                Log("守护启动: 保护 " + RootDir + "，文件数 " + Entries.Count + "，间隔 " + CheckMs + "ms，硬保护=" + Hard);

                if (Hard) HardenAll();

                StartWatcher();
                if (!NoWatchdog) StartWatchdog();
                StartIfeoThread();
                StartVerifyThread();

                DateTime t0 = DateTime.UtcNow;
                while (!Stop)
                {
                    try
                    {
                        if (!Directory.Exists(RootDir))
                        {
                            Log("受保护目录丢失，正在重建: " + RootDir);
                            Directory.CreateDirectory(RootDir);
                        }
                        EnsureSelf();
                        EnsureAll();
                        RepairIfeoOnce();
                    }
                    catch (Exception ex) { Log("巡检异常: " + ex.Message); }

                    if (TestSeconds > 0 && (DateTime.UtcNow - t0).TotalSeconds >= TestSeconds) break;
                    Thread.Sleep(CheckMs);
                }

                StopWatchdog();
                Log("守护退出。");
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static void EnsureSelf()
        {
            try
            {
                if (File.Exists(ExePath)) return;
                string backup = Path.Combine(DataDir, "KitGuard.backup.exe");
                if (!File.Exists(backup)) return;
                string dir = Path.GetDirectoryName(ExePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backup, ExePath, true);
                Log("已恢复自身程序: " + ExePath);
            }
            catch (Exception ex) { Log("恢复自身失败: " + ex.Message); }
        }

        static void EnsureAll()
        {
            List<Entry> copy;
            lock (Sync) copy = new List<Entry>(Entries);
            foreach (Entry e in copy)
            {
                if (Stop) return;
                EnsureOne(e);
            }
        }

        static bool EnsureOne(Entry e)
        {
            string path = Path.Combine(RootDir, e.Rel);
            string vaultFile = Path.Combine(VaultDir, e.Hash);
            try
            {
                if (!File.Exists(vaultFile))
                {
                    Log("保险库缺少 " + e.Rel + "，跳过");
                    return false;
                }

                FileInfo fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    RestoreFromVault(e, path, vaultFile, "删除");
                    return true;
                }

                if (fi.Length == e.Size && fi.LastWriteTimeUtc.Ticks == e.Ticks) return false;

                if (ComputeHash(path) == e.Hash)
                {
                    SetTimes(path, e.Ticks);
                    return false;
                }

                string signer = GetSigner(path);
                if (e.Signer.Length > 0 && signer == e.Signer && fi.LastWriteTimeUtc.Ticks > e.Ticks)
                {
                    AcceptUpdate(e, path, vaultFile);
                    return false;
                }

                RestoreFromVault(e, path, vaultFile, "篡改");
                return true;
            }
            catch (Exception ex)
            {
                Log("恢复失败 " + e.Rel + ": " + ex.Message);
                return false;
            }
        }

        static void RestoreFromVault(Entry e, string path, string vaultFile, string reason)
        {
            lock (Sync) Suppress.Add(path);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists) { try { fi.Attributes = FileAttributes.Normal; } catch { } }

                        using (FileStream src = new FileStream(vaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (FileStream dst = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                        {
                            dst.SetLength(0);
                            src.CopyTo(dst, 65536);
                        }
                        SetTimes(path, e.Ticks);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt == 2) throw;
                        Thread.Sleep(150);
                    }
                }

                if (Hard) ApplyDenyDelete(path);
                Interlocked.Increment(ref RestoreCount);
                Log("已恢复(" + reason + "): " + e.Rel);
            }
            finally
            {
                lock (Sync) Suppress.Remove(path);
            }
        }

        static void AcceptUpdate(Entry e, string path, string vaultFile)
        {
            try
            {
                Entry ne = BuildEntry(path);
                EnsureVault(ne);
                lock (Sync)
                {
                    Suppress.Add(path);
                    EntryMap[ne.Rel] = ne;
                    for (int i = 0; i < Entries.Count; i++)
                        if (string.Equals(Entries[i].Rel, ne.Rel, StringComparison.OrdinalIgnoreCase)) { Entries[i] = ne; break; }
                    SaveManifest(Entries);
                    Suppress.Remove(path);
                }
                Log("检测到官方更新，已接受新版本: " + e.Rel);
            }
            catch (Exception ex) { Log("接受更新失败 " + e.Rel + ": " + ex.Message); }
        }

        static void SetTimes(string path, long ticks)
        {
            try { File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc)); } catch { }
        }

        // ---------------- 文件监控 ----------------

        static void StartWatcher()
        {
            try
            {
                FileSystemWatcher w = new FileSystemWatcher(RootDir);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
                w.Changed += delegate(object s, FileSystemEventArgs e) { OnFsEvent(e.FullPath); };
                w.Created += delegate(object s, FileSystemEventArgs e) { OnFsEvent(e.FullPath); };
                w.Deleted += delegate(object s, FileSystemEventArgs e) { OnFsEvent(e.FullPath); };
                w.Renamed += delegate(object s, RenamedEventArgs e) { OnFsEvent(e.FullPath); };
                w.EnableRaisingEvents = true;
                Log("文件监控已启用");
            }
            catch (Exception ex) { Log("文件监控不可用(退化为轮询): " + ex.Message); }
        }

        static void OnFsEvent(string full)
        {
            if (Stop) return;
            try
            {
                string rel = GetRel(full);
                Entry e;
                lock (Sync)
                {
                    if (Suppress.Contains(full)) return;
                    if (!EntryMap.TryGetValue(rel, out e)) return;
                }
                EnsureOne(e);
            }
            catch { }
        }

        // ---------------- 看门狗 / 自启动 ----------------

        static string ChildArgs(string mode)
        {
            string a = mode;
            if (Hard) a += " --hard";
            a += " --silent --dir \"" + RootDir + "\" --vault \"" + DataDir + "\"";
            return a;
        }

        static void StartWatchdog()
        {
            try
            {
                if (WatchdogProcess != null && !WatchdogProcess.HasExited) return;
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, ChildArgs("--watchdog " + Process.GetCurrentProcess().Id));
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                WatchdogProcess = Process.Start(psi);

                Thread t = new Thread(delegate()
                {
                    while (!Stop)
                    {
                        Thread.Sleep(2000);
                        try
                        {
                            if (WatchdogProcess == null || WatchdogProcess.HasExited)
                            {
                                WatchdogProcess = Process.Start(psi);
                            }
                        }
                        catch { }
                    }
                });
                t.IsBackground = true;
                t.Start();
                Log("互护看门狗已启动 (PID " + WatchdogProcess.Id + ")");
            }
            catch (Exception ex) { Log("看门狗启动失败: " + ex.Message); }
        }

        static void StopWatchdog()
        {
            try
            {
                if (WatchdogProcess != null && !WatchdogProcess.HasExited)
                {
                    WatchdogProcess.Kill();
                    WatchdogProcess.WaitForExit(2000);
                }
            }
            catch { }
        }

        static bool IsMainRunning()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(MutexName)) { return true; }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch { return false; }
        }

        static int DoWatchdog(int parentPid)
        {
            Process parent = null;
            try { parent = Process.GetProcessById(parentPid); } catch { }
            if (parent != null)
            {
                try { parent.WaitForExit(); } catch { }
            }
            for (int i = 0; i < 25; i++)
            {
                if (!IsMainRunning()) break;
                Thread.Sleep(200);
            }
            if (!IsMainRunning())
            {
                Log("主进程已退出，看门狗拉起新进程");
                StartProcess(ExePath, ChildArgs("--main"));
            }
            return 0;
        }

        static int DoResurrect()
        {
            if (!IsMainRunning())
            {
                Log("计划任务: 守护未运行，拉起");
                StartProcess(ExePath, ChildArgs("--main"));
            }
            return 0;
        }

        static void StartProcess(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex) { Log("启动进程失败: " + ex.Message); }
        }

        // ---------------- IFEO 劫持修复 ----------------

        static void StartIfeoThread()
        {
            Thread t = new Thread(delegate()
            {
                while (!Stop)
                {
                    RepairIfeoOnce();
                    Thread.Sleep(5000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void RepairIfeoOnce()
        {
            List<string> names = new List<string>();
            lock (Sync)
            {
                foreach (Entry e in Entries)
                {
                    if (e.Rel.IndexOf('\\') < 0 && e.Rel.ToLowerInvariant().EndsWith(".exe")) names.Add(e.Rel);
                }
            }
            names.Add("KitGuard.exe");

            string[] subs = new string[] {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
            };
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };

            foreach (RegistryKey hive in hives)
            {
                foreach (string sub in subs)
                {
                    foreach (string n in names)
                    {
                        try
                        {
                            using (RegistryKey k = hive.OpenSubKey(sub + "\\" + n, true))
                            {
                                if (k == null) continue;
                                object dbg = k.GetValue("Debugger");
                                if (dbg != null)
                                {
                                    k.DeleteValue("Debugger", false);
                                    Log("已清除 IFEO 劫持: " + n + " -> " + dbg);
                                }
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            if (!IfeoWarned)
                            {
                                IfeoWarned = true;
                                Log("警告: 无管理员权限，HKLM 下的 IFEO 无法修复");
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        // ---------------- 完整性复检 ----------------

        static void StartVerifyThread()
        {
            Thread t = new Thread(delegate()
            {
                while (!Stop)
                {
                    for (int i = 0; i < VerifySeconds * 10 && !Stop; i++) Thread.Sleep(100);
                    if (Stop) return;
                    VerifyAllHashes();
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void VerifyAllHashes()
        {
            List<Entry> copy;
            lock (Sync) copy = new List<Entry>(Entries);
            foreach (Entry e in copy)
            {
                if (Stop) return;
                try
                {
                    string path = Path.Combine(RootDir, e.Rel);
                    if (!File.Exists(path)) { EnsureOne(e); continue; }
                    if (ComputeHash(path) != e.Hash) EnsureOne(e);
                }
                catch { }
            }
        }

        // ---------------- ACL 硬保护 ----------------

        static void HardenAll()
        {
            List<Entry> copy;
            lock (Sync) copy = new List<Entry>(Entries);
            int ok = 0;
            foreach (Entry e in copy)
            {
                string p = Path.Combine(RootDir, e.Rel);
                if (File.Exists(p)) { if (ApplyDenyDelete(p)) ok++; }
            }
            Log("硬保护已应用到 " + ok + "/" + copy.Count + " 个文件");
        }

        static bool ApplyDenyDelete(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                FileSecurity sec = fi.GetAccessControl();
                SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                foreach (AuthorizationRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    FileSystemAccessRule fr = r as FileSystemAccessRule;
                    if (fr == null) continue;
                    if (fr.AccessControlType == AccessControlType.Deny &&
                        fr.IdentityReference.Value == everyone.Value &&
                        (fr.FileSystemRights & FileSystemRights.Delete) == FileSystemRights.Delete)
                        return true;
                }
                sec.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Delete, AccessControlType.Deny));
                fi.SetAccessControl(sec);
                return true;
            }
            catch (Exception ex)
            {
                Log("加 ACL 失败 " + Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
        }

        static bool RemoveDenyDelete(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                FileSecurity sec = fi.GetAccessControl();
                SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                List<FileSystemAccessRule> del = new List<FileSystemAccessRule>();
                foreach (AuthorizationRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    FileSystemAccessRule fr = r as FileSystemAccessRule;
                    if (fr == null) continue;
                    if (fr.AccessControlType == AccessControlType.Deny &&
                        fr.IdentityReference.Value == everyone.Value &&
                        (fr.FileSystemRights & FileSystemRights.Delete) == FileSystemRights.Delete)
                        del.Add(fr);
                }
                foreach (FileSystemAccessRule fr in del) sec.RemoveAccessRule(fr);
                fi.SetAccessControl(sec);
                return true;
            }
            catch { return false; }
        }

        static void UnprotectAll()
        {
            List<Entry> copy;
            lock (Sync) copy = new List<Entry>(Entries);
            if (copy.Count == 0) { Snapshot(false); lock (Sync) copy = new List<Entry>(Entries); }
            foreach (Entry e in copy)
            {
                string p = Path.Combine(RootDir, e.Rel);
                if (File.Exists(p)) RemoveDenyDelete(p);
            }
            Console.WriteLine("已移除硬保护 ACL。");
        }

        // ---------------- 其它命令 ----------------

        static int DoStatus()
        {
            Console.WriteLine(AppName + " " + Version);
            Console.WriteLine("受保护目录: " + RootDir);
            Console.WriteLine("数据目录  : " + DataDir);
            try
            {
                List<Entry> copy;
                lock (Sync) copy = new List<Entry>(Entries);
                if (copy.Count == 0) { Snapshot(false); lock (Sync) copy = new List<Entry>(Entries); }
                int missing = 0, changed = 0;
                foreach (Entry e in copy)
                {
                    string p = Path.Combine(RootDir, e.Rel);
                    FileInfo fi = new FileInfo(p);
                    if (!fi.Exists) missing++;
                    else if (fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.Ticks) changed++;
                }
                Console.WriteLine("基线文件  : " + copy.Count + " 个");
                Console.WriteLine("缺失      : " + missing + " 个");
                Console.WriteLine("疑似变化  : " + changed + " 个");
            }
            catch (Exception ex) { Console.WriteLine("读取基线失败: " + ex.Message); }
            Console.WriteLine("主进程    : " + (IsMainRunning() ? "运行中" : "未运行"));
            return 0;
        }

        static int DoRestoreNow()
        {
            Snapshot(false);
            int before = RestoreCount;
            EnsureAll();
            Console.WriteLine("校验完成，本次恢复 " + (RestoreCount - before) + " 个文件。");
            return 0;
        }

        static int DoRefresh()
        {
            Snapshot(true);
            Console.WriteLine("基线已刷新，共 " + Entries.Count + " 个文件。");
            return 0;
        }

        static int DoUnprotect()
        {
            UnprotectAll();
            return 0;
        }

        // ---------------- 工具 ----------------

        static string RunTool(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try
                {
                    Encoding gbk = Encoding.GetEncoding(936);
                    psi.StandardOutputEncoding = gbk;
                    psi.StandardErrorEncoding = gbk;
                }
                catch { }
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return o;
                }
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                    {
                        string bak = LogPath + ".1";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(LogPath, bak);
                    }
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            if (!Silent) Console.WriteLine(line);
        }
    }
}
