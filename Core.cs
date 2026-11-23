using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TimeshiftViewer
{
    // ------------------------------------------------------------
    //  パス
    // ------------------------------------------------------------
    static class Paths
    {
        public static string Base;
        public static string Tools => Path.Combine(Base, "tools");
        public static string Ffmpeg => Path.Combine(Tools, "ffmpeg.exe");
        public static string Mpv => Path.Combine(Tools, "mpv.exe");
        public static string MpvConf => Path.Combine(Base, "mpv-config");
        public static string Settings => Path.Combine(Base, "settings.ini");
        public static string Log => Path.Combine(Base, "ffmpeg-log.txt");

        public static void Init()
        {
            // exe と同じ場所に書き込めなければ %LOCALAPPDATA% を使う
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            Base = IsWritable(exeDir) ? exeDir
                 : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeshiftViewer");
            Directory.CreateDirectory(Base);
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string p = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(p, "");
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }

        // mpv 用の設定・スクリプトを exe 内から書き出す
        public static void ExtractMpvConfig()
        {
            Directory.CreateDirectory(Path.Combine(MpvConf, "scripts"));
            var asm = Assembly.GetExecutingAssembly();
            void Write(string res, string dest)
            {
                using (var s = asm.GetManifestResourceStream(res))
                using (var f = File.Create(dest)) s.CopyTo(f);
            }
            Write("mpv.conf", Path.Combine(MpvConf, "mpv.conf"));
            Write("input.conf", Path.Combine(MpvConf, "input.conf"));
            Write("timeshift.lua", Path.Combine(MpvConf, "scripts", "timeshift.lua"));
        }
    }

    // ------------------------------------------------------------
    //  設定（key=value のテキストで保存）
    // ------------------------------------------------------------
    class AppSettings
    {
        public string Video = "";
        public string AudioMode = "video";   // video / none / device
        public string AudioDevice = "";
        public string Resolution = "1920x1080";
        public string Fps = "60";
        public int Kbps = 8000;
        public int Minutes = 10;
        public string Encoder = "auto";
        public string DetectedEncoder = "";
        public int Volume = 100;
        public int PenAutoClear = 1;          // 1: ライブに戻ったらペンの書き込みを消す

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(Paths.Settings)) return s;
                foreach (var line in File.ReadAllLines(Paths.Settings, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1);
                    var f = typeof(AppSettings).GetField(k);
                    if (f == null) continue;
                    if (f.FieldType == typeof(int)) { if (int.TryParse(v, out int n)) f.SetValue(s, n); }
                    else f.SetValue(s, v);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var f in typeof(AppSettings).GetFields()) sb.AppendLine(f.Name + "=" + f.GetValue(this));
                File.WriteAllText(Paths.Settings, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------
    //  ffmpeg / mpv のダウンロード
    // ------------------------------------------------------------
    static class ToolInstaller
    {
        public static bool Installed => File.Exists(Paths.Ffmpeg) && File.Exists(Paths.Mpv);

        const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        const string MpvApi = "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest";
        const string SevenZrUrl = "https://www.7-zip.org/a/7zr.exe";

        public static async Task Install(Action<string, int> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Paths.Tools);
            string tmp = Path.Combine(Path.GetTempPath(), "tsv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                if (!File.Exists(Paths.Ffmpeg))
                {
                    string zip = Path.Combine(tmp, "ffmpeg.zip");
                    await Download(FfmpegUrl, zip, p => progress("ffmpeg をダウンロード中...", p));
                    progress("ffmpeg を展開中...", -1);
                    string dir = Path.Combine(tmp, "ff");
                    await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir));
                    var exe = Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                              ?? throw new Exception("ffmpeg.exe が見つかりませんでした");
                    File.Copy(exe, Paths.Ffmpeg, true);
                }
                if (!File.Exists(Paths.Mpv))
                {
                    progress("mpv の最新版を確認中...", -1);
                    string json;
                    using (var wc = NewClient()) json = await wc.DownloadStringTaskAsync(MpvApi);
                    var urls = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>()
                                    .Select(m => m.Groups[1].Value).ToList();
                    string url = urls.FirstOrDefault(u => Regex.IsMatch(Path.GetFileName(u), @"^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$"))
                              ?? urls.FirstOrDefault(u => Regex.IsMatch(Path.GetFileName(u), @"^mpv-x86_64-(?!v3).*\.7z$"))
                              ?? throw new Exception("mpv のダウンロードファイルが見つかりませんでした");
                    string arc = Path.Combine(tmp, "mpv.7z");
                    await Download(url, arc, p => progress("mpv をダウンロード中...", p));
                    string seven = Path.Combine(tmp, "7zr.exe");
                    await Download(SevenZrUrl, seven, p => progress("展開ツールをダウンロード中...", p));
                    progress("mpv を展開中...", -1);
                    string dir = Path.Combine(tmp, "mpv");
                    await Task.Run(() =>
                    {
                        var psi = new ProcessStartInfo(seven, $"x \"{arc}\" -o\"{dir}\" -y")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
                    });
                    var exe = Directory.GetFiles(dir, "mpv.exe", SearchOption.AllDirectories).FirstOrDefault()
                              ?? throw new Exception("mpv.exe が見つかりませんでした");
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(exe)))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".exe" || ext == ".dll" || ext == ".com")
                            File.Copy(f, Path.Combine(Paths.Tools, Path.GetFileName(f)), true);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "TimeshiftViewer";
            return wc;
        }

        static async Task Download(string url, string dest, Action<int> progress)
        {
            using (var wc = NewClient())
            {
                wc.DownloadProgressChanged += (s, e) => progress(e.TotalBytesToReceive > 0 ? e.ProgressPercentage : -1);
                await wc.DownloadFileTaskAsync(new Uri(url), dest);
            }
        }
    }

    // ------------------------------------------------------------
    //  ffmpeg 関連
    // ------------------------------------------------------------
    class RunResult { public bool Ok; public string Log = ""; }

    static class FF
    {
        public static Task<RunResult> Run(string args, int timeoutMs = 15000)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(Paths.Ffmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return new RunResult { Ok = false, Log = "タイムアウトしました" };
                    }
                    p.WaitForExit();
                    return new RunResult { Ok = p.ExitCode == 0, Log = err.Result };
                }
            });
        }

        public static async Task<(List<string> video, List<string> audio)> ListDevices()
        {
            var r = await Run("-hide_banner -list_devices true -f dshow -i dummy", 20000);
            var video = new List<string>(); var audio = new List<string>();
            string section = "";
            foreach (var line in Regex.Split(r.Log, "\r?\n"))
            {
                if (line.Contains("DirectShow video devices")) { section = "video"; continue; }
                if (line.Contains("DirectShow audio devices")) { section = "audio"; continue; }
                if (line.Contains("Alternative name")) continue;
                string name = null, kind = "";
                var m = Regex.Match(line, @"\]\s+""(.+)""\s+\(([a-z, ]+)\)\s*$");
                if (m.Success) { name = m.Groups[1].Value; kind = m.Groups[2].Value; }
                else
                {
                    m = Regex.Match(line, @"\]\s+""(.+)""\s*$");
                    if (m.Success) { name = m.Groups[1].Value; kind = section; }
                }
                if (name == null) continue;
                if (kind.Contains("video")) { if (!video.Contains(name)) video.Add(name); }
                else if (kind.Contains("audio")) { if (!audio.Contains(name)) audio.Add(name); }
            }
            return (video, audio);
        }

        public static async Task<string> DetectEncoder(Action<string> status)
        {
            foreach (var enc in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                status("エンコーダを確認中: " + enc);
                var r = await Run($"-hide_banner -loglevel error -f lavfi -i color=black:s=1280x720:r=30 -frames:v 5 -c:v {enc} -f null -", 15000);
                if (r.Ok) return enc;
            }
            return "libx264";
        }

        public static string EncoderArgs(string enc, int kbps, int gop)
        {
            switch (enc)
            {
                case "h264_nvenc": return $"-c:v h264_nvenc -preset p3 -tune ll -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k -g {gop} -bf 0 -pix_fmt yuv420p";
                case "h264_qsv":   return $"-c:v h264_qsv -preset veryfast -b:v {kbps}k -maxrate {kbps}k -g {gop} -bf 0 -pix_fmt nv12";
                // AMF は -g だけではキーフレームを入れない（最初の1枚だけになり、途中から再生・シークできない）。1秒ごとに IDR を強制する
                case "h264_amf":   return $"-force_key_frames expr:gte(t,n_forced*1) -c:v h264_amf -forced_idr 1 -header_spacing {gop} -usage lowlatency -quality speed -rc cbr -b:v {kbps}k -maxrate {kbps}k -g {gop} -bf 0 -pix_fmt nv12";
                default:           return $"-c:v libx264 -preset veryfast -tune zerolatency -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k -g {gop} -pix_fmt yuv420p";
            }
        }

        /// <summary>開発用: キャプチャの代わりに流す動画ファイル（--test-input=）</summary>
        public static string TestInput;

        public static string InputArgs(AppSettings c, bool withFormat, string audioMode)
        {
            if (TestInput != null) return $"-hide_banner -nostdin -re -stream_loop -1 -i \"{TestInput}\"";
            string spec = "video=" + c.Video;
            if (audioMode == "video") spec += ":audio=" + c.Video;
            else if (audioMode == "device") spec += ":audio=" + c.AudioDevice;
            var a = new StringBuilder("-hide_banner -nostdin -f dshow -rtbufsize 1024M");
            if (withFormat)
            {
                if (c.Resolution != "source") a.Append(" -video_size ").Append(c.Resolution);
                if (c.Fps != "source") a.Append(" -framerate ").Append(c.Fps);
            }
            a.Append(" -i \"").Append(spec.Replace("\"", "")).Append('"');
            return a.ToString();
        }

        public static string Tail(string log, int n = 10)
        {
            var lines = Regex.Split(log ?? "", "\r?\n").Where(l => l.Trim() != "").ToList();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - n)));
        }
    }
}
