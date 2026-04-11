using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace QckEdit
{
    class Program
    {
        // -- Speed presets -----------------------------------------------------
        static readonly (double Speed, string Label)[] SPEEDS = {
            (0.25, "0.25x"),
            (0.5,  "0.50x"),
            (1.5,  "1.50x"),
            (2.0,  "2.00x"),
            (4.0,  "4.00x"),
            (8.0,  "8.00x")
        };

        // -- Compression presets (HEVC & FFV1) ---------------------------------
        static readonly (string Codec, string Label)[] COMPRESSIONS = {
            ("hevc18", "H.265 Extreme Quality (CRF 18)"),
            ("hevc20", "H.265 High Quality (CRF 20)"),
            ("hevc24", "H.265 Medium Quality (CRF 24)"),
            ("hevc28", "H.265 Small Size (CRF 28)"),
            ("ffv1",   "FFV1 (Lossless)")
        };

        // -- Combo presets (Speed + Compression) -------------------------------
        static readonly (double Speed, string Codec, string Label)[] COMBOS = {
            (2.0, "hevc20", "2.0x Double + H.265 (CRF20)"),
            (4.0, "hevc24", "4.0x Timelapse + H.265 (CRF24)"),
            (8.0, "hevc28", "8.0x Extreme + H.265 (CRF28)"),
            (0.5, "hevc20", "0.50x Slow-Mo + H.265 (CRF20)")
        };

        // -- Supported video formats -------------------------------------------
        static readonly string[] SUPPORTED = {
            ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm",
            ".flv", ".ts",  ".mts", ".m2ts",".3gp", ".obs", ".rec",
            ".hevc",".h265",".h264",".f4v", ".ogv", ".vob", ".asf",
            ".divx",".rmvb",".capture"
        };

        // -- Paths -------------------------------------------------------------
        static readonly string ExeDir   = AppContext.BaseDirectory;
        static readonly string FFmpeg   = Path.Combine(ExeDir, "ffmpeg.exe");
        static readonly string FFprobe  = Path.Combine(ExeDir, "ffprobe.exe");
        static readonly string QueueDir = Path.GetTempPath();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm());
                return;
            }

            switch (args[0].ToLower())
            {
                case "--install":
                    RunInstaller();
                    break;
                case "--uninstall":
                    RunUninstaller(false);
                    break;
                case "--uninstall-silent":
                    RunUninstaller(true);
                    break;
                case "--process":
                    int pIdx = Array.IndexOf(args, "--process");
                    if (pIdx == -1 || pIdx == args.Length - 1) return;
                    string file = args[pIdx + 1];

                    string speedArg = "1.0";
                    string codecArg = "none";
                    
                    int sIdx = Array.IndexOf(args, "--speed");
                    if (sIdx != -1) speedArg = args[sIdx + 1];
                    
                    int cIdx = Array.IndexOf(args, "--codec");
                    if (cIdx != -1) codecArg = args[cIdx + 1];

                    Enqueue(file, speedArg, codecArg);
                    RunBatchIfFirst();
                    break;
                default:
                    RunInstaller();
                    break;
            }
        }

        // -- Core Processing Logic ---------------------------------------------
        static void ProcessBatch(List<QueueEntry> items)
        {
            int total = items.Count;
            int done = 0;
            var errors = new List<string>();

            ShowToast("QckEdit", $"Processing {total} file{(total != 1 ? "s" : "")}…");

            foreach (var item in items)
            {
                try
                {
                    string ext = Path.GetExtension(item.Path).ToLower();
                    if (!SUPPORTED.Contains(ext)) continue;

                    ProcessVideo(item.Path, double.Parse(item.Speed, System.Globalization.CultureInfo.InvariantCulture), item.Codec);
                    done++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
                    ShowMessageBox("QckEdit Error", $"Failed to process {item.Path}\n\nError: {ex.Message}", true);
                }
            }

            if (errors.Count == 0 && done > 0)
                ShowToast("QckEdit [OK]", $"{done} file{(done != 1 ? "s" : "")} processed successfully.");
            else if (errors.Count > 0)
                ShowToast("QckEdit [ERROR]", $"{errors.Count} error(s): {errors[0]}");
        }

        static void ProcessVideo(string srcPath, double targetSpeed, string codecKey)
        {
            string dir = Path.GetDirectoryName(srcPath)!;
            string stem = Path.GetFileNameWithoutExtension(srcPath);
            
            string labelSpeed = targetSpeed == 1.0 ? "" : $"_{targetSpeed}x";
            string labelCodec = codecKey == "none" ? "" : $"_{codecKey}";
            
            if (targetSpeed == 1.0 && codecKey == "none") return;

            string outFile = Path.Combine(dir, $"{stem}{labelSpeed}{labelCodec}.mkv"); // MKV wrap

            string videoFilter = "";
            string audioFilter = "";
            
            if (targetSpeed != 1.0)
            {
                double pts = 1.0 / targetSpeed;
                string ptsStr = pts.ToString(System.Globalization.CultureInfo.InvariantCulture);
                videoFilter = $"-filter_complex \"[0:v]setpts={ptsStr}*PTS[v];[0:a]{BuildAtempoChain(targetSpeed)}[a]\" -map \"[v]\" -map \"[a]\" ";
            }
            
            string codecArgs;
            if (codecKey.StartsWith("hevc"))
            {
                string crf = codecKey.Substring(4);
                // Video to libx265, slow preset. Audio copied if 1x speed, otherwise re-encoded to aac.
                codecArgs = targetSpeed == 1.0 
                  ? $"-c:v libx265 -crf {crf} -preset slow -pix_fmt yuv420p -c:a copy"
                  : $"-c:v libx265 -crf {crf} -preset slow -pix_fmt yuv420p -c:a aac -b:a 128k";
            }
            else if (codecKey == "ffv1")
            {
                // Lossless video codec
                codecArgs = targetSpeed == 1.0 
                  ? $"-c:v ffv1 -level 3 -g 1 -pix_fmt yuv420p -c:a copy"
                  : $"-c:v ffv1 -level 3 -g 1 -pix_fmt yuv420p -c:a aac -b:a 256k";
            }
            else
            {
                 // Speed change only. Default back to heavily compatible h264
                 codecArgs = $"-c:v libx264 -crf 23 -preset fast -c:a aac -b:a 128k";
            }

            RunFFmpeg($"-i \"{srcPath}\" {videoFilter}{codecArgs} \"{outFile}\"");
        }

        static string BuildAtempoChain(double speed)
        {
            if (speed == 1.0) return "atempo=1.0";
            var chain = new List<string>();
            while (speed > 2.0) { chain.Add("atempo=2.0"); speed /= 2.0; }
            while (speed < 0.5) { chain.Add("atempo=0.5"); speed /= 0.5; }
            if (speed != 1.0) chain.Add($"atempo={speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return string.Join(",", chain);
        }

        // -- FFmpeg execution --------------------------------------------------
        static void RunFFmpeg(string args, bool ignoreError = false)
        {
            var p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = FFmpeg,
                    Arguments = $"-y -hide_banner -loglevel error {args}",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            p.Start();
            p.WaitForExit();
            if (p.ExitCode != 0 && !ignoreError) throw new Exception($"FFmpeg exited with code {p.ExitCode}");
        }

        public static void RunInstaller()
        {
            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Process.GetCurrentProcess().MainModule!.FileName,
                        Arguments = "--install",
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                }
                catch { }
                return;
            }
            DownloadFFmpegIfNeeded();
            RegisterContextMenu(Process.GetCurrentProcess().MainModule!.FileName!);
            MessageBox.Show("QckEdit has been successfully installed!\n\nYou can now right-click video files to use it.", "QckEdit Installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void RunUninstaller(bool silent = false)
        {
            if (!silent)
            {
                var confirm = MessageBox.Show(
                    "Are you sure you want to completely remove the QckEdit right-click menus from your system?", 
                    "Uninstall QckEdit", 
                    MessageBoxButtons.YesNo, 
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes) return;
            }

            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Process.GetCurrentProcess().MainModule!.FileName,
                        Arguments = "--uninstall-silent",
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                }
                catch { }
                return;
            }
            UnregisterContextMenu();
            MessageBox.Show("QckEdit has been successfully uninstalled.", "QckEdit Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void RegisterContextMenu(string exePath)
        {
            UnregisterContextMenu();
            foreach (var ext in SUPPORTED)
            {
                try
                {
                    string shellKey = $@"SystemFileAssociations\{ext}\shell\QckEdit";
                    using var parent = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(shellKey)!;
                    parent.SetValue("MUIVerb", "QckEdit");
                    parent.SetValue("SubCommands", ""); 

                    string cmpRoot = $@"{shellKey}\shell\01_Compress";
                    using var cRoot = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(cmpRoot)!;
                    cRoot.SetValue("MUIVerb", "Compress / Transcode");
                    cRoot.SetValue("SubCommands", "");

                    string spdRoot = $@"{shellKey}\shell\02_Speed";
                    using var sRoot = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(spdRoot)!;
                    sRoot.SetValue("MUIVerb", "Change Speed");
                    sRoot.SetValue("SubCommands", "");

                    string comboRoot = $@"{shellKey}\shell\03_Combo";
                    using var cbRoot = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(comboRoot)!;
                    cbRoot.SetValue("MUIVerb", "Speed + Compress");
                    cbRoot.SetValue("SubCommands", "");

                    int idx = 1;
                    foreach (var (codec, lbl) in COMPRESSIONS) {
                        using var opt = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{cmpRoot}\shell\{idx:D2}_{codec}");
                        opt!.SetValue("MUIVerb", lbl);
                        Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{cmpRoot}\shell\{idx:D2}_{codec}\command")!
                            .SetValue("", $"\"{exePath}\" --process \"%1\" --codec {codec}");
                        idx++;
                    }

                    idx = 1;
                    foreach (var (sp, lbl) in SPEEDS) {
                        using var opt = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{spdRoot}\shell\{idx:D2}_Spd");
                        opt!.SetValue("MUIVerb", lbl);
                        Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{spdRoot}\shell\{idx:D2}_Spd\command")!
                            .SetValue("", $"\"{exePath}\" --process \"%1\" --speed {sp.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                        idx++;
                    }

                    idx = 1;
                    foreach (var (sp, codec, lbl) in COMBOS) {
                        using var opt = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{comboRoot}\shell\{idx:D2}_Cb");
                        opt!.SetValue("MUIVerb", lbl);
                        Microsoft.Win32.Registry.ClassesRoot.CreateSubKey($@"{comboRoot}\shell\{idx:D2}_Cb\command")!
                            .SetValue("", $"\"{exePath}\" --process \"%1\" --speed {sp.ToString(System.Globalization.CultureInfo.InvariantCulture)} --codec {codec}");
                        idx++;
                    }
                } catch { }
            }
        }

        static void UnregisterContextMenu()
        {
            foreach (var ext in SUPPORTED) {
                try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree($@"SystemFileAssociations\{ext}\shell\QckEdit", false); } catch { }
            }
        }

        // -- System / Admin Utilities -------------------------------------------
        static bool IsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static void ShowMessageBox(string title, string message, bool isError = false)
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, isError ? MessageBoxIcon.Error : MessageBoxIcon.Information);
        }

        static void ShowToast(string title, string message)
        {
            try
            {
                string ps = $"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; $xml = New-Object Windows.Data.Xml.Dom.XmlDocument; $template = \"<toast><visual><binding template='ToastGeneric'><text>{title.Replace("'", "\"")}</text><text>{message.Replace("'", "\"")}</text></binding></visual></toast>\"; $xml.LoadXml($template); $toast = [Windows.UI.Notifications.ToastNotification]::new($xml); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(\"QckEdit\").Show($toast);";
                Process.Start(new ProcessStartInfo { FileName = "powershell", Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"" , WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true}).WaitForExit();
            } catch { }
        }

        // -- FFmpeg Download ---------------------------------------------------
        static void DownloadFFmpegIfNeeded()
        {
            if (File.Exists(FFmpeg)) return;
            Console.WriteLine("Downloading FFmpeg... (~30MB)");
            string zipPath = Path.Combine(ExeDir, "ffmpeg.zip");
            using (var client = new WebClient()) {
                client.DownloadFile("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zipPath);
            }
            ZipFile.ExtractToDirectory(zipPath, ExeDir, true);
            string extractedFFmpeg = Directory.GetFiles(ExeDir, "ffmpeg.exe", SearchOption.AllDirectories).First();
            string extractedFFprobe = Directory.GetFiles(ExeDir, "ffprobe.exe", SearchOption.AllDirectories).First();
            File.Move(extractedFFmpeg, FFmpeg, true);
            File.Move(extractedFFprobe, FFprobe, true);
            var parentDir = Directory.GetParent(extractedFFmpeg)!.Parent!.FullName;
            Directory.Delete(parentDir, true);
            File.Delete(zipPath);
        }

        // -- Queue System ------------------------------------------------------
        class QueueEntry { public string Path { get; set; } = ""; public string Speed { get; set; } = ""; public string Codec { get; set; } = ""; }
        static void Enqueue(string filePath, string speed, string codec)
        {
            string qf = Path.Combine(QueueDir, $"qt_queue_{speed}_{codec}.lock");
            AcquireLock(qf);
            try {
                var q = File.Exists(qf) ? System.Text.Json.JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(qf)) : new List<QueueEntry>();
                q!.Add(new QueueEntry { Path = filePath, Speed = speed, Codec = codec });
                File.WriteAllText(qf, System.Text.Json.JsonSerializer.Serialize(q));
            } finally { ReleaseLock(qf); }
        }

        static void RunBatchIfFirst()
        {
            var files = Directory.GetFiles(QueueDir, "qt_queue_*.lock").ToList();
            if (files.Count > 1 || Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1) return;
            Thread.Sleep(1500); 
            while (true)
            {
                var f = Directory.GetFiles(QueueDir, "qt_queue_*.lock").FirstOrDefault();
                if (f == null) break;
                List<QueueEntry>? q = null;
                AcquireLock(f);
                try {
                    q = System.Text.Json.JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(f));
                    File.Delete(f);
                } catch { 
                    break;
                } finally { ReleaseLock(f); }
                if (q != null) ProcessBatch(q);
            }
            Environment.Exit(0);
        }

        static void AcquireLock(string file)
        {
            while (true) {
                try {
                    using (File.Open(file + ".mut", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) return;
                } catch { Thread.Sleep(100); }
            }
        }
        
        static void ReleaseLock(string file)
        {
            try { File.Delete(file + ".mut"); } catch { }
        }
    }

    public class InstallerForm : Form
    {
        public InstallerForm()
        {
            this.Text = "QckEdit Installer";
            this.Width = 450;
            this.Height = 250;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var titleLabel = new Label
            {
                Text = "Install QckEdit Context Menus",
                Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(20, 20),
                AutoSize = true
            };

            var descLabel = new Label
            {
                Text = "QckEdit is a lightweight video processing tool.\n\nClicking 'Install' will automatically add right-click options for H.265 and FFV1 compression, as well as video speed modifications directly into Windows Explorer.\n\nClicking 'Uninstall' will completely erase all QckEdit context menus from the registry.",
                Location = new System.Drawing.Point(20, 60),
                Size = new System.Drawing.Size(380, 80),
                Font = new System.Drawing.Font("Segoe UI", 9F)
            };

            var installBtn = new Button
            {
                Text = "Install",
                Location = new System.Drawing.Point(90, 160),
                Size = new System.Drawing.Size(100, 30),
                Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold)
            };
            installBtn.Click += (s, e) => {
                Program.RunInstaller();
                this.Close();
            };

            var uninstallBtn = new Button
            {
                Text = "Uninstall",
                Location = new System.Drawing.Point(240, 160),
                Size = new System.Drawing.Size(100, 30),
                Font = new System.Drawing.Font("Segoe UI", 9F)
            };
            uninstallBtn.Click += (s, e) => {
                Program.RunUninstaller(false);
                this.Close();
            };

            this.Controls.Add(titleLabel);
            this.Controls.Add(descLabel);
            this.Controls.Add(installBtn);
            this.Controls.Add(uninstallBtn);
        }
    }
}
