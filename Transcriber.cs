using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>本机转写能力探测结果（缺什么一目了然，控制台据此显示红/绿点）。</summary>
public sealed class TranscribeCapability
{
    public bool FfmpegFound { get; init; }
    public string FfmpegPath { get; init; } = "";
    public bool WhisperFound { get; init; }
    public string WhisperPath { get; init; } = "";
    /// <summary>whisper.cpp / faster-whisper / ""（未探测到）</summary>
    public string Backend { get; init; } = "";
    public string PythonPath { get; init; } = "";
    public string Note { get; init; } = "";

    /// <summary>能转写（至少要探测到一种 whisper 后端）。</summary>
    public bool Ready => WhisperFound;
}

/// <summary>
/// 本地音频转写（可选能力，移植自 BiliLearn 的 video_asr）：探测 ffmpeg / whisper，抽音频 → 转写。
/// 任一环节缺失都优雅降级（返回空串），调用方按「无语音」跳过 —— 不引入任何强制依赖。
/// </summary>
public sealed class Transcriber
{
    readonly ILogger logger;
    readonly Func<BiliScrollConfig> getConfig;
    readonly string pluginDir;

    TranscribeCapability? cached;
    DateTime cachedAt = DateTime.MinValue;

    public Transcriber(ILogger logger, Func<BiliScrollConfig> getConfig, string pluginDir)
    {
        this.logger = logger;
        this.getConfig = getConfig;
        this.pluginDir = pluginDir;
    }

    // ---------------- 能力探测（带缓存，避免每次调用都起进程）----------------

    public TranscribeCapability Detect(bool force = false)
    {
        if (!force && cached != null && (DateTime.Now - cachedAt).TotalSeconds < 120)
            return cached;

        BiliScrollConfig cfg = getConfig();
        string ffmpeg = ResolveFfmpeg(cfg);
        (string whisperPath, string backend, string python, string note) = ResolveWhisper(cfg);

        var cap = new TranscribeCapability
        {
            FfmpegFound = !string.IsNullOrEmpty(ffmpeg),
            FfmpegPath = ffmpeg,
            WhisperFound = !string.IsNullOrEmpty(backend),
            WhisperPath = whisperPath,
            Backend = backend,
            PythonPath = python,
            Note = note
        };
        cached = cap;
        cachedAt = DateTime.Now;
        return cap;
    }

    string ResolveFfmpeg(BiliScrollConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath) && File.Exists(cfg.FfmpegPath))
            return cfg.FfmpegPath;

        string bundle = Path.Combine(pluginDir, "tools", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(bundle))
            return bundle;

        string found = WhichExe("ffmpeg.exe");
        if (!string.IsNullOrEmpty(found))
            return found;

        foreach (string cand in new[]
                 {
                     @"C:\ffmpeg\bin\ffmpeg.exe",
                     @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                     @"C:\ProgramData\chocolatey\bin\ffmpeg.exe"
                 })
        {
            if (File.Exists(cand))
                return cand;
        }
        return "";
    }

    (string Path, string Backend, string Python, string Note) ResolveWhisper(BiliScrollConfig cfg)
    {
        // 1) 用户显式指定
        if (!string.IsNullOrWhiteSpace(cfg.WhisperPath) && File.Exists(cfg.WhisperPath))
        {
            string name = Path.GetFileName(cfg.WhisperPath);
            bool isPython = name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("py", StringComparison.OrdinalIgnoreCase);
            if (!isPython && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (cfg.WhisperPath, "whisper.cpp", "", "");
            return ("", "faster-whisper", cfg.WhisperPath, "");
        }

        // 2) 插件自带 tools/whisper
        foreach (string exe in new[] { "whisper-cli.exe", "whisper.exe", "main.exe" })
        {
            string p = Path.Combine(pluginDir, "tools", "whisper", exe);
            if (File.Exists(p))
                return (p, "whisper.cpp", "", "");
        }

        // 3) PATH 里的 whisper.cpp 命令行（不猜 main，避免误命中同名程序）
        foreach (string name in new[] { "whisper-cli.exe", "whisper.exe" })
        {
            string p = WhichExe(name);
            if (!string.IsNullOrEmpty(p))
                return (p, "whisper.cpp", "", "");
        }

        // 4) faster-whisper（走 python 模块，模型会自动下载，最省事）
        string python = WhichExe("python.exe");
        if (string.IsNullOrEmpty(python))
            python = WhichExe("py.exe");
        if (!string.IsNullOrEmpty(python) && HasPythonModule(python, "faster_whisper"))
            return ("", "faster-whisper", python, "");
        if (!string.IsNullOrEmpty(python))
            return ("", "", python, "检测到 python，但未安装 faster-whisper（pip install faster-whisper）");

        return ("", "", "", "未检测到 whisper.cpp 或 faster-whisper");
    }

    bool HasPythonModule(string python, string module)
    {
        try
        {
            var psi = new ProcessStartInfo(python, $"-c \"import {module}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 where 在 PATH 里找一个可执行文件（找不到返回空串）。</summary>
    static string WhichExe(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("where", name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
                return "";
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(4000))
                return "";
            string[] lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    // ---------------- 转写 ----------------

    public bool IsAvailable() => Detect().Ready;

    /// <summary>
    /// 把一段音频/视频文件转成文字。返回空串表示「转不了或没有语音」，调用方据此跳过。
    /// </summary>
    public async Task<string> TranscribeAsync(string mediaPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return "";

        BiliScrollConfig cfg = getConfig();
        TranscribeCapability cap = Detect();
        if (!cap.Ready)
        {
            logger.LogWarning($"本地转写不可用：{cap.Note}");
            return "";
        }

        string model = string.IsNullOrWhiteSpace(cfg.TranscribeModel) ? "tiny" : cfg.TranscribeModel.Trim();
        int maxSec = Math.Max(60, cfg.MaxAudioMinutes * 60);

        string audio = mediaPath;
        string tmpWav = "";
        // 有 ffmpeg 就先转成 16k 单声道、并截断到 MaxAudioMinutes，又快又省
        if (cap.FfmpegFound)
        {
            tmpWav = Path.Combine(Path.GetTempPath(), "biliscroll_" + Guid.NewGuid().ToString("N") + ".16k.wav");
            var (ffOk, _) = await RunProcessAsync(cap.FfmpegPath,
                $"-y -i \"{mediaPath}\" -t {maxSec} -vn -ac 1 -ar 16000 \"{tmpWav}\"", ct, TimeSpan.FromMinutes(10));
            if (ffOk && File.Exists(tmpWav) && new FileInfo(tmpWav).Length > 0)
            {
                audio = tmpWav;
            }
            else
            {
                TryDelete(tmpWav);
                tmpWav = "";
                logger.LogWarning("ffmpeg 抽音频失败，退回用原始音频转写。");
            }
        }

        try
        {
            if (cap.Backend == "whisper.cpp")
                return await RunWhisperCppAsync(cap.WhisperPath, model, audio, ct);
            if (cap.Backend == "faster-whisper")
                return await RunFasterWhisperAsync(cap.PythonPath, model, audio, ct);
            return "";
        }
        finally
        {
            if (!string.IsNullOrEmpty(tmpWav))
                TryDelete(tmpWav);
        }
    }

    async Task<string> RunWhisperCppAsync(string exe, string model, string wav, CancellationToken ct)
    {
        if (!wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("whisper.cpp 需要 16kHz wav，但 ffmpeg 不可用，跳过本地转写。");
            return "";
        }
        string modelPath = ResolveWhisperCppModel(exe, model);
        if (string.IsNullOrEmpty(modelPath))
        {
            logger.LogWarning($"whisper.cpp 找不到模型文件 ggml-{model}.bin（请放到 whisper.exe 同目录或插件 tools\\whisper）。");
            return "";
        }

        string outBase = Path.Combine(Path.GetTempPath(), "biliscroll_asr_" + Guid.NewGuid().ToString("N"));
        var (ok, _) = await RunProcessAsync(exe,
            $"-m \"{modelPath}\" -f \"{wav}\" -l zh -otxt -of \"{outBase}\" -t 4", ct, TimeSpan.FromMinutes(30));

        string txt = outBase + ".txt";
        string result = "";
        try
        {
            if (File.Exists(txt))
                result = File.ReadAllText(txt, Encoding.UTF8);
        }
        catch (Exception e)
        {
            logger.LogDebug($"读取 whisper.cpp 结果失败：{e.Message}");
        }
        TryDelete(txt);
        if (!ok && string.IsNullOrWhiteSpace(result))
            logger.LogWarning("whisper.cpp 转写未产出结果。");
        return CleanTranscript(result);
    }

    string ResolveWhisperCppModel(string exe, string model)
    {
        var dirs = new List<string>();
        try
        {
            string? d = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(d))
            {
                dirs.Add(d);
                dirs.Add(Path.Combine(d, "models"));
            }
        }
        catch
        {
        }
        dirs.Add(Path.Combine(pluginDir, "tools", "whisper"));
        dirs.Add(Path.Combine(pluginDir, "tools", "whisper", "models"));

        string[] names = { $"ggml-{model}.bin", $"ggml-{model}-q5_1.bin", $"{model}.bin" };
        foreach (string d in dirs)
        {
            foreach (string n in names)
            {
                string p = Path.Combine(d, n);
                if (File.Exists(p))
                    return p;
            }
        }
        return "";
    }

    async Task<string> RunFasterWhisperAsync(string python, string model, string audio, CancellationToken ct)
    {
        string script = EnsureAsrScript();
        if (string.IsNullOrEmpty(script))
            return "";
        var (_, stdout) = await RunProcessAsync(python,
            $"\"{script}\" \"{model}\" \"{audio}\"", ct, TimeSpan.FromMinutes(30));
        return CleanTranscript(stdout);
    }

    /// <summary>把内联转写脚本落到插件 tools 目录（避免命令行里那些难缠的引号转义）。</summary>
    string EnsureAsrScript()
    {
        try
        {
            string dir = Path.Combine(pluginDir, "tools");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "biliscroll_asr.py");
            if (!File.Exists(path))
                File.WriteAllText(path, AsrScript, new UTF8Encoding(false));
            return path;
        }
        catch (Exception e)
        {
            logger.LogWarning($"写入转写脚本失败：{e.Message}");
            return "";
        }
    }

    const string AsrScript =
        "import sys\n" +
        "def main():\n" +
        "    if len(sys.argv) < 3:\n" +
        "        print('')\n" +
        "        return\n" +
        "    model = sys.argv[1]\n" +
        "    audio = sys.argv[2]\n" +
        "    try:\n" +
        "        from faster_whisper import WhisperModel\n" +
        "        m = WhisperModel(model, device='cpu', compute_type='int8')\n" +
        "        segments, info = m.transcribe(audio, language='zh', beam_size=3, vad_filter=True)\n" +
        "        lines = [s.text.strip() for s in segments]\n" +
        "        print('\\n'.join([l for l in lines if l]))\n" +
        "    except Exception as e:\n" +
        "        sys.stderr.write('ERROR: ' + str(e) + '\\n')\n" +
        "        print('')\n" +
        "if __name__ == '__main__':\n" +
        "    main()\n";

    // ---------------- 能力自检（控制台「一键测试」）----------------

    public Task<string> TestAsync(CancellationToken ct = default)
    {
        TranscribeCapability cap = Detect(force: true);
        var sb = new StringBuilder();
        sb.AppendLine("本地转写能力自检：");
        sb.AppendLine($"- ffmpeg：{(cap.FfmpegFound ? "已找到 " + cap.FfmpegPath : "未找到（转写前无法把音频转成 16kHz，whisper.cpp 会不可用）")}");
        sb.AppendLine($"- whisper：{(cap.WhisperFound ? cap.Backend + (cap.WhisperPath.Length > 0 ? " @ " + cap.WhisperPath : "") : "未找到")}");
        if (!string.IsNullOrEmpty(cap.PythonPath))
            sb.AppendLine($"- python：{cap.PythonPath}");
        if (!string.IsNullOrEmpty(cap.Note))
            sb.AppendLine($"- 说明：{cap.Note}");
        sb.AppendLine(cap.Ready
            ? "结论：可用。无字幕的视频会自动尝试本地转写。"
            : "结论：不可用。装了 ffmpeg + whisper（或 faster-whisper）后重开本卡即可。其它功能不受影响。");
        return Task.FromResult(sb.ToString());
    }

    // ---------------- 过程工具 ----------------

    async Task<(bool Ok, string StdOut)> RunProcessAsync(string file, string args, CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = new Process { StartInfo = psi };
            if (!p.Start())
                return (false, "");

            Task<string> so = p.StandardOutput.ReadToEndAsync(ct);
            Task<string> se = p.StandardError.ReadToEndAsync(ct);
            bool timedOut = false;
            try
            {
                await p.WaitForExitAsync(ct).WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                try { p.Kill(true); } catch { }
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }

            string stdout = await so;
            string stderr = await se;
            if (timedOut)
            {
                logger.LogWarning($"{Path.GetFileName(file)} 超时（{timeout.TotalMinutes:0} 分钟），已终止。");
                return (false, stdout);
            }
            if (p.ExitCode != 0)
                logger.LogDebug($"{Path.GetFileName(file)} 退出码 {p.ExitCode}：{Trim(stderr, 300)}");
            return (p.ExitCode == 0, stdout);
        }
        catch (Exception e)
        {
            logger.LogWarning($"运行 {Path.GetFileName(file)} 失败：{e.Message}");
            return (false, "");
        }
    }

    /// <summary>清掉转写结果里的空白行与 BOM，保留正文。</summary>
    static string CleanTranscript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var lines = raw
            .Replace("\uFEFF", "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 转写结果是不是「没有语音」：空、太短，或只剩水印/署名（如「字幕byXX」「请不吝点赞」）。
    /// 这是 duty 纪律里「空是一种结果」的落点 —— 不重试、不写调试脚本，直接跳过。
    /// </summary>
    public static bool LooksLikeNoSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        string t = text;
        foreach (string noise in new[] { "字幕by", "字幕BY", "字幕：", "字幕:", "字幕组", "请不吝点赞", "订阅", "转发", "打赏" })
            t = t.Replace(noise, "");
        string letters = new(t.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length < 6;
    }

    static string Trim(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text.Substring(0, max);

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
