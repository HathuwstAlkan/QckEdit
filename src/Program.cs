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
            ("h264_23", "H.264 Standard (~Original Size)"),
            ("hevc18", "H.265 Extreme Quality (~30% Smaller)"),
            ("hevc20", "H.265 High Quality (~50% Smaller)"),
            ("hevc24", "H.265 Medium Quality (~70% Smaller)"),
            ("hevc28", "H.265 Small Size (~85% Smaller)"),
            ("ffv1",   "FFV1 Lossless (HUGE Backup Size)")
        };

        // -- Combo presets (Speed + Compression) -------------------------------
        static readonly (double Speed, string Codec, string Label)[] COMBOS = {
            (2.0, "hevc20", "2.0x Double + H.265 (~50% Smaller)"),
            (4.0, "hevc24", "4.0x Timelapse + H.265 (~70% Smaller)"),
            (8.0, "hevc28", "8.0x Extreme + H.265 (~85% Smaller)"),
            (0.5, "hevc20", "0.50x Slow-Mo + H.265 (~50% Smaller)")
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 0)
            {
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

                    try
                    {
                        string ext = Path.GetExtension(file).ToLower();
                        if (SUPPORTED.Contains(ext))
                        {
                            double speed = double.Parse(speedArg, System.Globalization.CultureInfo.InvariantCulture);
                            ProcessVideo(file, speed, codecArg);
                            ShowMessageBox("QckEdit Success", $"Successfully compressed:\n{Path.GetFileName(file)}", false);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowMessageBox("QckEdit Error", $"Failed to process {file}\n\nError: {ex.Message}", true);
                    }
                    Environment.Exit(0);
                    break;
                default:
                    RunInstaller();
                    break;
            }
        }

        // -- Core Processing Logic ---------------------------------------------
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
            
            string gpuInfo = "";
            try {
                var p = new Process { StartInfo = new ProcessStartInfo { FileName = "wmic", Arguments = "path win32_VideoController get name", RedirectStandardOutput = true, CreateNoWindow = true, UseShellExecute = false } };
                p.Start(); gpuInfo = p.StandardOutput.ReadToEnd().ToLower(); p.WaitForExit();
            } catch { }

            string aac = targetSpeed == 1.0 ? "-c:a copy" : "-c:a aac -b:a 128k";
            string aacHq = targetSpeed == 1.0 ? "-c:a copy" : "-c:a aac -b:a 256k";

            string primaryCodec = "";
            string fallbackCodec = "";

            bool hasNvidia = gpuInfo.Contains("nvidia");
            bool hasAmdDiscrete = gpuInfo.Contains("radeon rx") || (gpuInfo.Contains("amd") && !gpuInfo.Contains("graphics"));

            if (codecKey.StartsWith("hevc"))
            {
                string crf = codecKey.Substring(4);
                fallbackCodec = $"-c:v libx265 -crf {crf} -preset fast -pix_fmt yuv420p {aac}";
                if (hasNvidia) primaryCodec = $"-c:v hevc_nvenc -cq {crf} -preset p6 -tune hq -pix_fmt yuv420p {aac}";
                else if (hasAmdDiscrete) primaryCodec = $"-c:v hevc_amf -quality quality -pix_fmt yuv420p {aac}";
                else primaryCodec = fallbackCodec; // Rely on CPU for integrated graphics instead of hardware encoding
            }
            else if (codecKey == "ffv1")
            {
                primaryCodec = fallbackCodec = $"-c:v ffv1 -level 3 -g 1 -pix_fmt yuv420p {aacHq}";
            }
            else // h264 default
            {
                fallbackCodec = $"-c:v libx264 -crf 23 -preset fast -pix_fmt yuv420p {aac}";
                if (hasNvidia) primaryCodec = $"-c:v h264_nvenc -cq 23 -preset p6 -tune hq -pix_fmt yuv420p {aac}";
                else if (hasAmdDiscrete) primaryCodec = $"-c:v h264_amf -quality quality -pix_fmt yuv420p {aac}";
                else primaryCodec = fallbackCodec; // Rely on CPU for integrated graphics instead of hardware encoding
            }

            try
            {
                RunFFmpeg($"-i \"{srcPath}\" {videoFilter}{primaryCodec} \"{outFile}\"", Path.GetFileName(srcPath), outFile);
            }
            catch (Exception exPrimary)
            {
                if (primaryCodec != fallbackCodec)
                {
                    try 
                    {
                        RunFFmpeg($"-i \"{srcPath}\" {videoFilter}{fallbackCodec} \"{outFile}\"", Path.GetFileName(srcPath), outFile);
                    } 
                    catch (Exception exFallback) 
                    {
                        try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                        throw new Exception($"Primary and Fallback encoders both failed!\nFallback Error: {exFallback.Message}");
                    }
                }
                else 
                {
                    try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                    throw exPrimary;
                }
            }
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
        static void RunFFmpeg(string args, string fileTitle, string outFile)
        {
            var p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = FFmpeg,
                    Arguments = $"-y -hide_banner -loglevel warning {args}",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                }
            };

            string errLog = "";
            bool success = true;
            int exitCode = 0;
            bool isCancelled = false;

            using (var pForm = new Form())
            {
                pForm.Text = "QckEdit Processing...";
                pForm.Size = new System.Drawing.Size(420, 150);
                pForm.StartPosition = FormStartPosition.CenterScreen;
                pForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                pForm.MaximizeBox = false;
                pForm.MinimizeBox = true; // Allow user to minimize
                pForm.ControlBox = true; // Show X on the top right
                
                var lbl = new Label { 
                    Text = $"Processing Video:\n{fileTitle}", 
                    AutoSize = true, 
                    Location = new System.Drawing.Point(20, 15), 
                    MaximumSize = new System.Drawing.Size(380, 40) 
                };
                
                var pb = new ProgressBar { 
                    Style = ProgressBarStyle.Marquee, 
                    Location = new System.Drawing.Point(20, 50), 
                    Size = new System.Drawing.Size(360, 20) 
                };

                var btnCancel = new Button {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(280, 80),
                    Size = new System.Drawing.Size(100, 25)
                };

                Action performCancel = () => {
                    if (isCancelled) return;
                    isCancelled = true;
                    lbl.Text = "Terminating process...";
                    btnCancel.Enabled = false;
                    try { if (!p.HasExited) p.Kill(); } catch { }
                };

                btnCancel.Click += (s, e) => performCancel();
                pForm.FormClosing += (s, e) => {
                    if (!p.HasExited && !isCancelled) {
                        e.Cancel = true; // Prevent closing instantly without cleanup
                        performCancel();
                    }
                };

                pForm.Controls.Add(lbl);
                pForm.Controls.Add(pb);
                pForm.Controls.Add(btnCancel);

                pForm.Shown += (s, e) => {
                    Application.DoEvents();
                    try
                    {
                        p.Start();
                        while (!p.HasExited)
                        {
                            Application.DoEvents();
                            Thread.Sleep(50);
                        }
                        if (isCancelled) return; // Skip success checks if cancelled

                        errLog = p.StandardError.ReadToEnd();
                        exitCode = p.ExitCode;
                        if (exitCode != 0) success = false;
                    }
                    catch (Exception ex)
                    {
                        if (!isCancelled) {
                            success = false;
                            errLog = ex.Message;
                        }
                    }
                    finally
                    {
                        if (!pForm.IsDisposed) pForm.Close();
                    }
                };

                Application.Run(pForm);
            }

            if (isCancelled)
            {
                Thread.Sleep(500); // Wait for file locks to drop
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                ShowMessageBox("QckEdit", "Successfully cancelled operation", false);
                Environment.Exit(0); // Fully kill the instance to prevent fallback logic from triggering
            }

            if (!success) throw new Exception($"FFmpeg crashed!\nExit Code: {exitCode}\n\nLast Output:\n{errLog}");
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
                Text = "QckEdit is a lightweight video processing tool.\n\nClicking 'Install' adds fast right-click options for H.264 (Universal), H.265, and Lossless compression, as well as video speed modifications directly into Windows Explorer.\n\nClicking 'Uninstall' completely erases all QckEdit menus.",
                Location = new System.Drawing.Point(20, 60),
                Size = new System.Drawing.Size(380, 100),
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
