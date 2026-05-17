using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CpaTray
{
    internal static class Program
    {
        private const string AppName = "CPA";
        private const string ExeRelativePath = @".\cli-proxy-api.exe";
        private const string BackendLatestUrl = "https://github.com/router-for-me/CLIProxyAPI/releases/latest";
        private const string BackendDownloadBase = "https://github.com/router-for-me/CLIProxyAPI/releases/download/";
        private const string FrontendLatestUrl = "https://github.com/router-for-me/Cli-Proxy-API-Management-Center/releases/latest";
        private const string FrontendDownloadBase = "https://github.com/router-for-me/Cli-Proxy-API-Management-Center/releases/download/";
        private const string MutexName = @"Local\CPA-CLIProxyAPI-SingleFile";

        private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string ExeFullPath = Path.GetFullPath(Path.Combine(BaseDirectory, "cli-proxy-api.exe"));
        private static readonly string ExeProcessName = Path.GetFileNameWithoutExtension(ExeFullPath);
        private static readonly string StaticDirectory = Path.Combine(BaseDirectory, "static");
        private static readonly string ManagementHtmlPath = Path.Combine(StaticDirectory, "management.html");
        private static readonly string UpdateStatePath = Path.Combine(BaseDirectory, ".cpa-update-state.json");

        private static Mutex mutex;
        private static NotifyIcon notifyIcon;
        private static Icon runningIcon;
        private static Icon stoppedIcon;
        private static ToolStripMenuItem statusItem;
        private static ToolStripMenuItem startItem;
        private static ToolStripMenuItem stopItem;
        private static ToolStripMenuItem restartItem;
        private static ToolStripMenuItem checkUpdateItem;
        private static ToolStripMenuItem applyUpdateItem;
        private static System.Windows.Forms.Timer timer;
        private static ApplicationContext appContext;
        private static Form hiddenForm;
        private static volatile bool isCheckingUpdates;
        private static volatile bool isUpdating;
        private static UpdateInfo latestUpdateInfo;
        private static UpdateState updateState;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                return;
            }

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Directory.SetCurrentDirectory(BaseDirectory);
            updateState = UpdateState.Load(UpdateStatePath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            runningIcon = LoadEmbeddedIcon("CpaColorIcon") ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            stoppedIcon = LoadEmbeddedIcon("CpaGrayIcon") ?? MakeGrayIcon(runningIcon);

            hiddenForm = new Form();
            hiddenForm.ShowInTaskbar = false;
            hiddenForm.WindowState = FormWindowState.Minimized;
            hiddenForm.Load += delegate { hiddenForm.Hide(); };

            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "CPA - 检查中";
            notifyIcon.Icon = stoppedIcon;
            notifyIcon.Visible = true;
            notifyIcon.ContextMenuStrip = BuildMenu();
            notifyIcon.DoubleClick += delegate { ShowStatus(); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { UpdateTray(); };
            timer.Start();

            appContext = new ApplicationContext(hiddenForm);

            try
            {
                IntPtr unusedHandle = hiddenForm.Handle;
                UpdateTray();
                StartCpa();
                QueueUpdateCheck(false);
                Application.Run(appContext);
            }
            finally
            {
                if (timer != null) timer.Dispose();
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                }
                if (runningIcon != null) runningIcon.Dispose();
                if (stoppedIcon != null) stoppedIcon.Dispose();
                if (hiddenForm != null) hiddenForm.Dispose();
                if (mutex != null)
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }
        }

        private static ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("状态：检查中");
            statusItem.Enabled = false;
            startItem = new ToolStripMenuItem("启动 CPA");
            startItem.Click += delegate { StartCpa(); };
            stopItem = new ToolStripMenuItem("停止 CPA");
            stopItem.Click += delegate { StopCpa(false); };
            restartItem = new ToolStripMenuItem("重启 CPA");
            restartItem.Click += delegate { RestartCpa(); };
            checkUpdateItem = new ToolStripMenuItem("检查更新");
            checkUpdateItem.Click += delegate { QueueUpdateCheck(true); };
            applyUpdateItem = new ToolStripMenuItem("更新到最新版本");
            applyUpdateItem.Enabled = false;
            applyUpdateItem.Click += delegate { QueueApplyUpdate(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出 CPA");
            exitItem.Click += delegate
            {
                StopCpa(true);
                notifyIcon.Visible = false;
                appContext.ExitThread();
            };

            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startItem);
            menu.Items.Add(stopItem);
            menu.Items.Add(restartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(checkUpdateItem);
            menu.Items.Add(applyUpdateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            return menu;
        }

        private static Icon LoadEmbeddedIcon(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null;
            using (stream) return new Icon(stream);
        }

        private static Icon MakeGrayIcon(Icon sourceIcon)
        {
            using (Bitmap source = sourceIcon.ToBitmap())
            using (Bitmap gray = new Bitmap(source.Width, source.Height))
            {
                for (int y = 0; y < source.Height; y++)
                {
                    for (int x = 0; x < source.Width; x++)
                    {
                        Color c = source.GetPixel(x, y);
                        int v = (int)((c.R * 0.299) + (c.G * 0.587) + (c.B * 0.114));
                        gray.SetPixel(x, y, Color.FromArgb(c.A, v, v, v));
                    }
                }
                return Icon.FromHandle(gray.GetHicon());
            }
        }

        private static Process[] GetCpaProcesses()
        {
            Process[] processes = Process.GetProcessesByName(ExeProcessName);
            List<Process> exactMatches = new List<Process>();
            bool sawUnknownProcess = false;
            foreach (Process process in processes)
            {
                try
                {
                    string processPath = Path.GetFullPath(process.MainModule.FileName);
                    if (string.Equals(processPath, ExeFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        exactMatches.Add(process);
                    }
                }
                catch
                {
                    sawUnknownProcess = true;
                }
            }
            if (exactMatches.Count > 0) return exactMatches.ToArray();
            return sawUnknownProcess ? processes : new Process[0];
        }

        private static bool IsCpaRunning()
        {
            return GetCpaProcesses().Length > 0;
        }

        private static void UpdateTray()
        {
            bool running = IsCpaRunning();
            notifyIcon.Text = running ? "CPA - 运行中" : "CPA - 已停止";
            notifyIcon.Icon = running ? runningIcon : stoppedIcon;
            statusItem.Text = running ? "状态：运行中" : "状态：已停止";
            startItem.Enabled = !running && !isUpdating;
            stopItem.Enabled = running && !isUpdating;
            restartItem.Enabled = !isUpdating;
            checkUpdateItem.Enabled = !isCheckingUpdates && !isUpdating;
            applyUpdateItem.Enabled = latestUpdateInfo != null && latestUpdateInfo.HasUpdates && !isCheckingUpdates && !isUpdating;
        }

        private static bool StartCpaProcess()
        {
            if (IsCpaRunning()) return false;
            if (!File.Exists(ExeFullPath))
            {
                throw new FileNotFoundException("找不到 .\\cli-proxy-api.exe，请把 CPA.exe 放在 cli-proxy-api.exe 同一目录。", ExeFullPath);
            }
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = ExeRelativePath;
            startInfo.WorkingDirectory = BaseDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(startInfo);
            Thread.Sleep(600);
            return true;
        }

        private static bool StopCpaProcessesCore()
        {
            Process[] processes = GetCpaProcesses();
            if (processes.Length == 0) return false;
            List<string> errors = new List<string>();
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        process.WaitForExit(1500);
                    }
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("进程 ID：" + process.Id + "，" + ex.Message);
                }
            }
            Thread.Sleep(300);
            if (errors.Count > 0) throw new InvalidOperationException("停止 CPA 失败。\r\n" + string.Join("\r\n", errors.ToArray()));
            return true;
        }

        private static void StartCpa()
        {
            try
            {
                bool started = StartCpaProcess();
                UpdateTray();
                ShowBalloon(started ? "CPA 已启动" : "CPA", started ? "CPA 正在后台运行。" : "CPA 已在运行。", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                UpdateTray();
                ShowError("启动 CPA 失败。\r\n" + ex.Message);
            }
        }

        private static void StopCpa(bool silent)
        {
            try
            {
                bool stopped = StopCpaProcessesCore();
                UpdateTray();
                if (!silent) ShowBalloon(stopped ? "CPA 已停止" : "CPA", stopped ? "CPA 已停止运行。" : "CPA 已经停止。", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                UpdateTray();
                if (!silent) ShowError(ex.Message);
            }
        }

        private static void RestartCpa()
        {
            StopCpa(true);
            StartCpa();
        }

        private static void ShowStatus()
        {
            ShowBalloon("CPA 状态", IsCpaRunning() ? "CPA 正在运行。" : "CPA 已停止。", ToolTipIcon.Info);
            UpdateTray();
        }

        private static void QueueUpdateCheck(bool userInitiated)
        {
            if (isCheckingUpdates || isUpdating) return;
            isCheckingUpdates = true;
            checkUpdateItem.Text = "正在检查更新...";
            UpdateTray();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UpdateInfo info = CheckForUpdates();
                    RunOnUi(delegate
                    {
                        latestUpdateInfo = info;
                        isCheckingUpdates = false;
                        checkUpdateItem.Text = "检查更新";
                        UpdateTray();
                        if (info.HasUpdates) ShowBalloon("发现新版本", info.ToShortMessage(), ToolTipIcon.Info);
                        else if (userInitiated) ShowBalloon("CPA 已是最新", "后端和管理面板均未发现新版本。", ToolTipIcon.Info);
                    });
                }
                catch (Exception ex)
                {
                    RunOnUi(delegate
                    {
                        isCheckingUpdates = false;
                        checkUpdateItem.Text = "检查更新";
                        UpdateTray();
                        if (userInitiated) ShowError("检查更新失败。\r\n" + ex.Message);
                    });
                }
            });
        }

        private static void QueueApplyUpdate()
        {
            if (isUpdating || latestUpdateInfo == null || !latestUpdateInfo.HasUpdates) return;
            string confirm = latestUpdateInfo.ToLongMessage() + "\r\n\r\n是否现在更新？更新后端时会临时停止 CPA 服务。";
            if (MessageBox.Show(confirm, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            isUpdating = true;
            applyUpdateItem.Text = "正在更新...";
            UpdateTray();
            UpdateInfo info = latestUpdateInfo;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ApplyUpdate(info);
                    RunOnUi(delegate
                    {
                        latestUpdateInfo = null;
                        isUpdating = false;
                        applyUpdateItem.Text = "更新到最新版本";
                        UpdateTray();
                        ShowBalloon("更新完成", "CPA 后端或管理面板已更新到最新版本。", ToolTipIcon.Info);
                    });
                }
                catch (Exception ex)
                {
                    RunOnUi(delegate
                    {
                        isUpdating = false;
                        applyUpdateItem.Text = "更新到最新版本";
                        UpdateTray();
                        ShowError("更新失败。\r\n" + ex.Message);
                    });
                }
            });
        }

        private static UpdateInfo CheckForUpdates()
        {
            UpdateInfo info = new UpdateInfo();
            info.CurrentBackendVersion = GetLocalBackendVersion();
            string backendTag = GetLatestTagByRedirect(BackendLatestUrl);
            info.LatestBackendVersion = NormalizeTag(backendTag);
            info.BackendAsset = BuildBackendAsset(backendTag);
            info.BackendUpdateAvailable = IsNewerVersion(info.LatestBackendVersion, info.CurrentBackendVersion);

            string frontendTag = GetLatestTagByRedirect(FrontendLatestUrl);
            info.CurrentFrontendVersion = GetLocalFrontendVersion();
            info.LatestFrontendVersion = NormalizeTag(frontendTag);
            info.FrontendAsset = BuildFrontendAsset(frontendTag);
            info.FrontendVersionUnknown = string.IsNullOrWhiteSpace(info.CurrentFrontendVersion);
            info.FrontendUpdateAvailable = info.FrontendVersionUnknown
                ? !string.Equals(updateState.FrontendTag, frontendTag, StringComparison.OrdinalIgnoreCase)
                : IsNewerVersion(info.LatestFrontendVersion, info.CurrentFrontendVersion);
            return info;
        }

        private static string GetLatestTagByRedirect(string latestUrl)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(latestUrl);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.UserAgent = "CPA-Windows-Tray";
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    string location = response.Headers["Location"];
                    string candidate = !string.IsNullOrWhiteSpace(location)
                        ? new Uri(new Uri(latestUrl), location).AbsoluteUri
                        : response.ResponseUri.AbsoluteUri;
                    return ParseTagFromReleaseUrl(candidate);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    string location = response.Headers["Location"];
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        return ParseTagFromReleaseUrl(new Uri(new Uri(latestUrl), location).AbsoluteUri);
                    }
                    throw new InvalidOperationException("GitHub 返回错误：" + (int)response.StatusCode + " " + response.StatusDescription);
                }
                throw;
            }
        }

        private static string ParseTagFromReleaseUrl(string url)
        {
            Match match = Regex.Match(url, @"/releases/tag/([^/?#]+)", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException("无法从 GitHub release 跳转地址解析版本号：" + url);
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        private static ReleaseAsset BuildBackendAsset(string tag)
        {
            string version = NormalizeTag(tag);
            string name = "CLIProxyAPI_" + version + "_windows_amd64.zip";
            return new ReleaseAsset { Name = name, BrowserDownloadUrl = BackendDownloadBase + tag + "/" + name, TagName = tag };
        }

        private static ReleaseAsset BuildFrontendAsset(string tag)
        {
            return new ReleaseAsset { Name = "management.html", BrowserDownloadUrl = FrontendDownloadBase + tag + "/management.html", TagName = tag };
        }

        private static void ApplyUpdate(UpdateInfo info)
        {
            string stagingDir = Path.Combine(BaseDirectory, ".cpa-update-staging");
            PrepareEmptyDirectory(stagingDir);
            try
            {
                bool wasRunning = IsCpaRunning();
                if (info.BackendUpdateAvailable)
                {
                    StopCpaProcessesCore();
                    UpdateBackend(info, stagingDir);
                    updateState.BackendTag = info.BackendAsset.TagName;
                }
                if (info.FrontendUpdateAvailable)
                {
                    UpdateFrontend(info, stagingDir);
                    updateState.FrontendTag = info.FrontendAsset.TagName;
                }
                updateState.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                updateState.Save(UpdateStatePath);
                if (info.BackendUpdateAvailable && wasRunning) StartCpaProcess();
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
        }

        private static void UpdateBackend(UpdateInfo info, string stagingDir)
        {
            string downloadPath = Path.Combine(stagingDir, info.BackendAsset.Name);
            DownloadFile(info.BackendAsset.BrowserDownloadUrl, downloadPath);
            string extractDir = Path.Combine(stagingDir, "backend");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(downloadPath, extractDir);
            string newExePath = FindBackendExe(extractDir);
            string candidatePath = Path.Combine(stagingDir, "cli-proxy-api.exe.new");
            File.Copy(newExePath, candidatePath, true);
            string detectedVersion = GetBackendVersion(candidatePath);
            if (!VersionsEqualOrNewer(detectedVersion, info.LatestBackendVersion))
            {
                throw new InvalidOperationException("下载的后端版本不匹配。检测到：" + detectedVersion + "，期望：" + info.LatestBackendVersion);
            }
            string backupPath = Path.Combine(BaseDirectory, "cli-proxy-api.exe.bak");
            TryDeleteFile(backupPath);
            bool movedOriginal = false;
            try
            {
                File.Move(ExeFullPath, backupPath);
                movedOriginal = true;
                File.Move(candidatePath, ExeFullPath);
                TryDeleteFile(backupPath);
            }
            catch
            {
                if (movedOriginal && File.Exists(backupPath) && !File.Exists(ExeFullPath)) File.Move(backupPath, ExeFullPath);
                throw;
            }
        }

        private static void UpdateFrontend(UpdateInfo info, string stagingDir)
        {
            string downloadPath = Path.Combine(stagingDir, "management.html");
            DownloadFile(info.FrontendAsset.BrowserDownloadUrl, downloadPath);
            Directory.CreateDirectory(StaticDirectory);
            string backupPath = Path.Combine(StaticDirectory, "management.html.bak");
            TryDeleteFile(backupPath);
            bool movedOriginal = false;
            try
            {
                if (File.Exists(ManagementHtmlPath))
                {
                    File.Move(ManagementHtmlPath, backupPath);
                    movedOriginal = true;
                }
                File.Move(downloadPath, ManagementHtmlPath);
                TryDeleteFile(backupPath);
            }
            catch
            {
                if (movedOriginal && File.Exists(backupPath) && !File.Exists(ManagementHtmlPath)) File.Move(backupPath, ManagementHtmlPath);
                throw;
            }
        }

        private static void DownloadFile(string url, string path)
        {
            using (WebClient client = NewWebClient())
            {
                client.DownloadFile(url, path);
            }
        }

        private static WebClient NewWebClient()
        {
            WebClient client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers.Add("User-Agent", "CPA-Windows-Tray");
            return client;
        }

        private static string GetLocalBackendVersion()
        {
            return GetBackendVersion(ExeFullPath);
        }

        private static string GetBackendVersion(string exePath)
        {
            if (!File.Exists(exePath)) return string.Empty;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = exePath;
            startInfo.Arguments = "--help";
            startInfo.WorkingDirectory = Path.GetDirectoryName(exePath);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using (Process process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                Match match = Regex.Match(output, @"CLIProxyAPI Version:\s*v?([0-9]+(?:\.[0-9]+){1,3}(?:[-+][A-Za-z0-9\.-]+)?)", RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
        }

        private static string GetLocalFrontendVersion()
        {
            if (!File.Exists(ManagementHtmlPath)) return string.Empty;
            string firstChunk;
            using (FileStream stream = File.OpenRead(ManagementHtmlPath))
            {
                int length = (int)Math.Min(stream.Length, 262144);
                byte[] buffer = new byte[length];
                stream.Read(buffer, 0, length);
                firstChunk = Encoding.UTF8.GetString(buffer);
            }
            Match match = Regex.Match(firstChunk, @"(?:management|app|version)[^0-9]{0,40}v?([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string FindBackendExe(string directory)
        {
            foreach (string file in Directory.GetFiles(directory, "cli-proxy-api.exe", SearchOption.AllDirectories)) return file;
            foreach (string file in Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (name.Contains("cli") || name.Contains("proxy")) return file;
            }
            throw new InvalidOperationException("下载包中没有找到 cli-proxy-api.exe。");
        }

        private static bool IsNewerVersion(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            if (string.IsNullOrWhiteSpace(current)) return true;
            Version latestVersion;
            Version currentVersion;
            if (Version.TryParse(CleanVersion(latest), out latestVersion) && Version.TryParse(CleanVersion(current), out currentVersion))
            {
                return latestVersion.CompareTo(currentVersion) > 0;
            }
            return !string.Equals(NormalizeTag(latest), NormalizeTag(current), StringComparison.OrdinalIgnoreCase);
        }

        private static bool VersionsEqualOrNewer(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return true;
            return string.Equals(NormalizeTag(actual), NormalizeTag(expected), StringComparison.OrdinalIgnoreCase) || !IsNewerVersion(expected, actual);
        }

        private static string CleanVersion(string value)
        {
            value = NormalizeTag(value);
            int dash = value.IndexOf('-');
            if (dash >= 0) value = value.Substring(0, dash);
            int plus = value.IndexOf('+');
            if (plus >= 0) value = value.Substring(0, plus);
            return value;
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            tag = tag.Trim();
            return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
        }

        private static void PrepareEmptyDirectory(string path)
        {
            TryDeleteDirectory(path);
            Directory.CreateDirectory(path);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void RunOnUi(Action action)
        {
            if (hiddenForm == null || hiddenForm.IsDisposed) return;
            try { hiddenForm.BeginInvoke(action); }
            catch { }
        }

        private static void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = text;
            notifyIcon.BalloonTipIcon = icon;
            notifyIcon.ShowBalloonTip(3000);
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private sealed class UpdateInfo
        {
            public string CurrentBackendVersion;
            public string LatestBackendVersion;
            public bool BackendUpdateAvailable;
            public ReleaseAsset BackendAsset;
            public string CurrentFrontendVersion;
            public string LatestFrontendVersion;
            public bool FrontendVersionUnknown;
            public bool FrontendUpdateAvailable;
            public ReleaseAsset FrontendAsset;

            public bool HasUpdates { get { return BackendUpdateAvailable || FrontendUpdateAvailable; } }

            public string ToShortMessage()
            {
                List<string> parts = new List<string>();
                if (BackendUpdateAvailable) parts.Add("后端 " + DisplayVersion(CurrentBackendVersion) + " → " + DisplayVersion(LatestBackendVersion));
                if (FrontendUpdateAvailable) parts.Add("管理面板 " + DisplayVersion(CurrentFrontendVersion) + " → " + DisplayVersion(LatestFrontendVersion));
                return string.Join("；", parts.ToArray());
            }

            public string ToLongMessage()
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("发现可更新版本：");
                if (BackendUpdateAvailable) builder.AppendLine("后端：" + DisplayVersion(CurrentBackendVersion) + " → " + DisplayVersion(LatestBackendVersion));
                if (FrontendUpdateAvailable) builder.AppendLine("管理面板：" + DisplayVersion(CurrentFrontendVersion) + " → " + DisplayVersion(LatestFrontendVersion));
                return builder.ToString().Trim();
            }

            private static string DisplayVersion(string value)
            {
                return string.IsNullOrWhiteSpace(value) ? "未知" : "v" + NormalizeTag(value);
            }
        }

        private sealed class ReleaseAsset
        {
            public string Name;
            public string BrowserDownloadUrl;
            public string TagName;
        }

        private sealed class UpdateState
        {
            public string BackendTag { get; set; }
            public string FrontendTag { get; set; }
            public string UpdatedAtUtc { get; set; }

            public static UpdateState Load(string path)
            {
                try
                {
                    if (!File.Exists(path)) return new UpdateState();
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    return serializer.Deserialize<UpdateState>(json) ?? new UpdateState();
                }
                catch
                {
                    return new UpdateState();
                }
            }

            public void Save(string path)
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(this), Encoding.UTF8);
            }
        }
    }
}
