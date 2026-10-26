using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>
/// 「缺件一键下载」：把可选外部工具（ffmpeg / whisper.cpp / 语音模型）从多个镜像地址下载并安装进插件 tools\ 目录。
/// 每个工具维护一份有序镜像列表，逐个尝试（下载 → 校验 → 安装）；任一环节失败就打日志、换下一个镜像；
/// 全部失败时如实返回失败原因 —— 只返回文本、绝不抛异常，也绝不阻塞其它功能。零额外依赖（只用 BCL）。
/// </summary>
public sealed class ToolsFetcher
{
    // ---------------- 镜像地址（均已实际核实可用，见文件末尾注释） ----------------

    /// <summary>ffmpeg（Windows 64 位静态构建）镜像，按顺序尝试。</summary>
    static readonly string[] FfmpegUrls =
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        "https://gh-proxy.com/https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    };

    /// <summary>whisper.cpp 兜底直链（钉住确实存在的 tag）；运行时优先用 GitHub API 解析最新版资产。</summary>
    static readonly string[] PinnedWhisperUrls =
    {
        "https://github.com/ggerganov/whisper.cpp/releases/download/v1.8.3/whisper-bin-x64.zip",
        "https://github.com/ggerganov/whisper.cpp/releases/download/v1.7.6/whisper-bin-x64.zip",
        "https://gh-proxy.com/https://github.com/ggerganov/whisper.cpp/releases/download/v1.8.3/whisper-bin-x64.zip",
    };

    const string WhisperApiUrl = "https://api.github.com/repos/ggerganov/whisper.cpp/releases/latest";

    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    readonly ILogger logger;
    readonly Func<BiliScrollConfig> getConfig;
    readonly string pluginDir;
    readonly HttpClient http;

    public ToolsFetcher(ILogger logger, Func<BiliScrollConfig> getConfig, string pluginDir)
    {
        this.logger = logger;
        this.getConfig = getConfig;
        this.pluginDir = pluginDir;

        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            // 大文件（ffmpeg 上百 MB）下载慢，超时交给传入的 CancellationToken 控制
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>给 UI / 自检展示：每个工具的名称 + 所有镜像地址。</summary>
    public IReadOnlyList<ToolPlan> Plans
    {
        get
        {
            string model = ModelName;
            var whisperPlan = new List<string> { WhisperApiUrl + "（动态解析最新 whisper-bin-x64.zip）" };
            whisperPlan.AddRange(PinnedWhisperUrls);
            return new List<ToolPlan>
            {
                new("ffmpeg", "ffmpeg（Windows 64 位静态构建）",
                    Path.Combine(pluginDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"), FfmpegUrls),
                new("whisper", "whisper.cpp（Windows x64 预编译包）",
                    Path.Combine(pluginDir, "tools", "whisper", "whisper-cli.exe"), whisperPlan),
                new("model", $"语音模型 ggml-{model}.bin",
                    Path.Combine(pluginDir, "tools", "whisper", $"ggml-{model}.bin"), ModelUrls(model)),
            };
        }
    }

    public sealed record ToolPlan(string Key, string Name, string Target, IReadOnlyList<string> Urls);

    /// <summary>配置里的转写模型名（默认 tiny），做了兜底，避免为空时拼出怪路径。</summary>
    string ModelName
    {
        get
        {
            string m = "";
            try { m = getConfig()?.TranscribeModel ?? ""; } catch { }
            m = m.Trim().ToLowerInvariant();
            return m is "tiny" or "base" or "small" ? m : "tiny";
        }
    }

    static IReadOnlyList<string> ModelUrls(string model) => new[]
    {
        $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin",
        $"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin",
    };

    /// <summary>
    /// 一键下载缺失工具。which ∈ "all" | "ffmpeg" | "whisper" | "model"（默认 all）。
    /// log 用于实时日志；返回给人看的结果文本（中文，逐条说明成功/失败与用了哪个镜像）。绝不抛异常。
    /// </summary>
    public async Task<string> FetchAsync(string which, Action<string>? log, CancellationToken ct)
    {
        string w = (which ?? "").Trim().ToLowerInvariant();
        bool all = w.Length == 0 || w == "all" || w == "*";
        bool doFfmpeg = all || w == "ffmpeg";
        bool doWhisper = all || w == "whisper" || w == "whisper.cpp";
        bool doModel = all || w == "model" || w == "ggml";

        if (!doFfmpeg && !doWhisper && !doModel)
            return $"不认识的类型「{which}」——可选：all（全部）/ ffmpeg / whisper / model。";

        var sb = new StringBuilder();
        sb.AppendLine("缺失工具一键下载（每个工具逐个尝试镜像，一个失败自动换下一个）：");
        sb.AppendLine();

        try
        {
            if (doFfmpeg)
                sb.AppendLine(await FetchFfmpegAsync(log, ct));
            if (doWhisper)
                sb.AppendLine(await FetchWhisperAsync(log, ct));
            if (doModel)
                sb.AppendLine(await FetchModelAsync(log, ct));
        }
        catch (OperationCanceledException)
        {
            sb.AppendLine("下载被取消（插件卸载或会话结束）。");
        }
        catch (Exception e)
        {
            logger.LogWarning($"工具下载异常：{e.Message}");
            sb.AppendLine($"下载过程出错：{e.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("装好的工具即刻可用（可点「一键测试能力」复核）。下载失败多半是网络/镜像临时不可用，稍后重试即可；其它功能不受影响。");
        return sb.ToString();
    }

    // ---------------- 三个工具各自的多镜像流程 ----------------

    async Task<string> FetchFfmpegAsync(Action<string>? log, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string target = Path.Combine(pluginDir, "tools", "ffmpeg", "bin", "ffmpeg.exe");

        foreach (string url in FfmpegUrls)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, reason) = await DownloadAndInstallAsync(url, InstallFfmpeg, log, ct);
            if (ok)
            {
                log?.Invoke($"✔ ffmpeg 安装成功（用了镜像：{url}）");
                sb.AppendLine($"✔ ffmpeg：安装成功 → {target}");
                sb.AppendLine($"  使用镜像：{url}");
                return sb.ToString();
            }
            log?.Invoke($"✘ ffmpeg 镜像失败（{reason}），换下一个：{url}");
            sb.AppendLine($"✘ 镜像 {Short(url)}：{reason}");
        }

        sb.AppendLine("✘ ffmpeg：所有镜像都失败，未安装（不影响其它功能）。");
        return sb.ToString();
    }

    async Task<string> FetchWhisperAsync(Action<string>? log, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string target = Path.Combine(pluginDir, "tools", "whisper");

        var urls = new List<string>();
        string? latest = await ResolveLatestWhisperZipAsync(log, ct);
        if (!string.IsNullOrEmpty(latest))
            urls.Add(latest);
        urls.AddRange(PinnedWhisperUrls);

        foreach (string url in urls)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, reason) = await DownloadAndInstallAsync(url, InstallWhisper, log, ct);
            if (ok)
            {
                log?.Invoke($"✔ whisper.cpp 安装成功（用了镜像：{url}）");
                sb.AppendLine($"✔ whisper.cpp：安装成功 → {target}");
                sb.AppendLine($"  使用镜像：{url}");
                return sb.ToString();
            }
            log?.Invoke($"✘ whisper.cpp 镜像失败（{reason}），换下一个：{url}");
            sb.AppendLine($"✘ 镜像 {Short(url)}：{reason}");
        }

        sb.AppendLine("✘ whisper.cpp：所有镜像都失败，未安装（不影响其它功能）。");
        return sb.ToString();
    }

    async Task<string> FetchModelAsync(Action<string>? log, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string model = ModelName;
        string target = Path.Combine(pluginDir, "tools", "whisper", $"ggml-{model}.bin");

        foreach (string url in ModelUrls(model))
        {
            ct.ThrowIfCancellationRequested();
            var (ok, reason) = await DownloadAndInstallAsync(url, p => InstallModel(p, model), log, ct);
            if (ok)
            {
                log?.Invoke($"✔ 模型 ggml-{model}.bin 安装成功（用了镜像：{url}）");
                sb.AppendLine($"✔ 模型 ggml-{model}.bin：安装成功 → {target}");
                sb.AppendLine($"  使用镜像：{url}");
                return sb.ToString();
            }
            log?.Invoke($"✘ 模型镜像失败（{reason}），换下一个：{url}");
            sb.AppendLine($"✘ 镜像 {Short(url)}：{reason}");
        }

        sb.AppendLine($"✘ 模型 ggml-{model}.bin：所有镜像都失败，未安装（不影响其它功能）。");
        return sb.ToString();
    }

    /// <summary>用 GitHub API 解析 whisper.cpp 最新 release 里的 whisper-bin-x64.zip 直链；失败返回 null。</summary>
    async Task<string?> ResolveLatestWhisperZipAsync(Action<string>? log, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, WhisperApiUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                log?.Invoke($"whisper 最新版解析失败（HTTP {(int)resp.StatusCode}），改用兜底直链。");
                return null;
            }
            string json = await resp.Content.ReadAsStringAsync(timeout.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            string? exact = null, loose = null;
            foreach (JsonElement a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out JsonElement ne) || !a.TryGetProperty("browser_download_url", out JsonElement ue))
                    continue;
                string name = ne.GetString() ?? "";
                string url = ue.GetString() ?? "";
                if (url.Length == 0)
                    continue;
                if (string.Equals(name, "whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase))
                    exact ??= url;
                else if (name.EndsWith("-bin-x64.zip", StringComparison.OrdinalIgnoreCase)
                         && !name.Contains("cublas", StringComparison.OrdinalIgnoreCase)
                         && !name.Contains("cuda", StringComparison.OrdinalIgnoreCase))
                    loose ??= url;
            }

            string? picked = exact ?? loose;
            if (picked != null)
                log?.Invoke($"whisper.cpp 最新版资产：{picked}");
            else
                log?.Invoke("whisper.cpp 最新版里没有 x64 预编译包，改用兜底直链。");
            return picked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log?.Invoke($"whisper 最新版解析出错（{e.Message}），改用兜底直链。");
            return null;
        }
    }

    // ---------------- 下载 → 安装 通用流程 ----------------

    /// <summary>下载一个地址到临时文件并交给 installer 安装；返回是否成功与失败原因。下载的半成品一定会被删除。</summary>
    async Task<(bool Ok, string Reason)> DownloadAndInstallAsync(
        string url, Func<string, (bool Ok, string Reason)> installer, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string tmp = Path.Combine(Path.GetTempPath(), "biliscroll_dl_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            log?.Invoke($"开始下载：{url}");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var info = new FileInfo(tmp);
            if (!info.Exists || info.Length <= 0)
                return (false, "下载到的文件为空");

            log?.Invoke($"下载完成（{info.Length / 1024 / 1024} MB），正在校验与安装…");
            return installer(tmp);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            TryDeleteFile(tmp);
        }
    }

    /// <summary>解压 ffmpeg 压缩包，递归找到 ffmpeg.exe（连同 ffprobe.exe）装到 tools\ffmpeg\bin。</summary>
    (bool Ok, string Reason) InstallFfmpeg(string zipPath)
    {
        string staging = NewStaging();
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            string? ffmpeg = FindFile(staging, n => string.Equals(n, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(ffmpeg))
                return (false, "压缩包里没有 ffmpeg.exe（可能不是有效的 ffmpeg 包）");

            string? ffprobe = FindFile(staging, n => string.Equals(n, "ffprobe.exe", StringComparison.OrdinalIgnoreCase));

            string bin = Path.Combine(pluginDir, "tools", "ffmpeg", "bin");
            Directory.CreateDirectory(bin);
            File.Copy(ffmpeg, Path.Combine(bin, "ffmpeg.exe"), true);
            if (!string.IsNullOrEmpty(ffprobe))
                File.Copy(ffprobe, Path.Combine(bin, "ffprobe.exe"), true);

            string installed = Path.Combine(bin, "ffmpeg.exe");
            if (!File.Exists(installed) || new FileInfo(installed).Length == 0)
                return (false, "安装后校验失败（ffmpeg.exe 不存在或为空）");
            return (true, "已安装到 " + installed);
        }
        catch (Exception e)
        {
            return (false, "解压/安装失败：" + e.Message);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>解压 whisper.cpp 压缩包，把可执行文件所在目录整体装到 tools\whisper（拍平外层的 Release/ 之类）。</summary>
    (bool Ok, string Reason) InstallWhisper(string zipPath)
    {
        string staging = NewStaging();
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            string? exe = FindFile(staging, n =>
                string.Equals(n, "whisper-cli.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "whisper.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, "main.exe", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(exe))
                return (false, "压缩包里没有 whisper 可执行文件（whisper-cli.exe / whisper.exe / main.exe）");

            string srcRoot = Path.GetDirectoryName(exe) ?? staging;
            string target = Path.Combine(pluginDir, "tools", "whisper");
            Directory.CreateDirectory(target);
            CopyDir(srcRoot, target);

            if (string.IsNullOrEmpty(FindWhisperExe(target)))
                return (false, "安装后校验失败（目标目录没找到可执行文件）");
            return (true, "已安装到 " + target);
        }
        catch (Exception e)
        {
            return (false, "解压/安装失败：" + e.Message);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>把下载到的 .bin 模型装成 tools\whisper\ggml-{model}.bin（太小的文件判为无效）。</summary>
    (bool Ok, string Reason) InstallModel(string binPath, string model)
    {
        try
        {
            var info = new FileInfo(binPath);
            if (!info.Exists || info.Length < 1_000_000)
                return (false, $"文件太小（{info.Length} 字节），不像有效模型");

            string target = Path.Combine(pluginDir, "tools", "whisper");
            Directory.CreateDirectory(target);
            string dst = Path.Combine(target, $"ggml-{model}.bin");
            File.Copy(binPath, dst, true);

            if (!File.Exists(dst) || new FileInfo(dst).Length == 0)
                return (false, "安装后校验失败");
            return (true, "已安装到 " + dst);
        }
        catch (Exception e)
        {
            return (false, "安装失败：" + e.Message);
        }
    }

    // ---------------- 小工具 ----------------

    static string NewStaging()
    {
        string dir = Path.Combine(Path.GetTempPath(), "biliscroll_tools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>在 root 下递归找第一个满足条件的文件名（找不到返回 null）。</summary>
    static string? FindFile(string root, Func<string, bool> match)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (match(Path.GetFileName(f)))
                    return f;
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>在 tools\whisper 里按 Transcriber 的既有顺序找 whisper 可执行文件。</summary>
    static string? FindWhisperExe(string dir)
    {
        foreach (string name in new[] { "whisper-cli.exe", "whisper.exe", "main.exe" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p))
                return p;
        }
        return FindFile(dir, n =>
            string.Equals(n, "whisper-cli.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "whisper.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "main.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>递归复制目录（保持相对结构，覆盖同名文件）。</summary>
    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string to = Path.Combine(dst, rel);
            string? toDir = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(toDir))
                Directory.CreateDirectory(toDir);
            File.Copy(file, to, true);
        }
    }

    /// <summary>把长 URL 缩成「主机名 + 文件名」便于日志与结果文本。</summary>
    static string Short(string url)
    {
        try
        {
            var uri = new Uri(url);
            string host = uri.Host;
            string file = Path.GetFileName(uri.AbsolutePath);
            return file.Length > 0 ? $"{host}/{file}" : host;
        }
        catch
        {
            return url;
        }
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}

// 镜像可用性核实（2026-10-19，用 HEAD 请求实测，均返回 200）：
//   ffmpeg  : www.gyan.dev/...release-essentials.zip（114 MB）
//             github.com/BtbN/FFmpeg-Builds/.../latest/ffmpeg-master-latest-win64-gpl.zip（200 MB）
//             gh-proxy.com 加速同上（200）
//   whisper : api.github.com/repos/ggerganov/whisper.cpp/releases/latest（动态解析，最新 v1.9.5 无 x64 资产，故兜底）
//             github.com/ggerganov/whisper.cpp/releases/download/v1.8.3/whisper-bin-x64.zip（3.9 MB，含 Release/main.exe 等）
//             v1.7.6/whisper-bin-x64.zip（200）；gh-proxy.com 加速 v1.8.3 同上（200）
//   模型    : huggingface.co/... 与 hf-mirror.com/... 的 ggml-tiny/base/small.bin（tiny 77 MB / base 148 MB / small 488 MB）
