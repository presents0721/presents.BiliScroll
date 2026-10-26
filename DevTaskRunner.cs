using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>
/// 「主人指令 · 本机项目下载测试」的执行器：把主人指定的项目（仅白名单来源，默认 GitHub）下载到插件目录之外的沙箱目录，
/// 识别项目类型，跑它的「标准测试」，并把结果整理成一段中文报告。
/// 安全底线：只允许 http(s) + 白名单 host；只用结构化的 ProcessStartInfo.ArgumentList 起进程（绝不拼 shell 字符串）；
/// 有超时与输出行数上限；演练模式只下载/分析、不真正执行测试；任何异常都兜底返回可读结果，绝不抛。
/// </summary>
public sealed class DevTaskRunner
{
    /// <summary>备选分支（zip 下载时依次试探）。</summary>
    static readonly string[] Branches = { "main", "master" };

    /// <summary>跑测试时最多回显/记录多少行输出（防止一次刷几百行）。</summary>
    const int OutputLineCap = 200;

    /// <summary>下载单步的兜底超时（分钟）。</summary>
    const int DownloadTimeoutMinutes = 20;

    /// <summary>ghproxy.com / gitclone.com 作为 GitHub 的加速镜像，始终视为允许来源。</summary>
    static readonly string[] MirrorHosts = { "ghproxy.com", "gitclone.com" };

    /// <summary>判定「这条文本是下载测试指令」的触发词（必须同时含一个链接才成立）。</summary>
    static readonly string[] TriggerWords =
    {
        "下载", "下载下来", "clone", "克隆", "拉下来", "拉下来测", "测一下", "测一测", "测试", "跑一下", "跑一跑",
        "跑测试", "跑个测试", "跑起来", "test", "build", "本地测试", "本地跑", "测测"
    };

    static readonly HttpClient http = CreateHttp();

    readonly ILogger logger;
    readonly Func<BiliScrollConfig> getConfig;
    /// <summary>沙箱根目录（在插件目录之外）。</summary>
    readonly string outsideDir;

    public DevTaskRunner(ILogger logger, Func<BiliScrollConfig> getConfig, string outsideDir)
    {
        this.logger = logger;
        this.getConfig = getConfig;
        this.outsideDir = outsideDir;
    }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BiliScrollDevTask/1.0 (+local)");
        return h;
    }

    // ---------------- 指令解析（只从主人本人正文里取） ----------------

    /// <summary>
    /// 判断一段文本是不是「下载并测试项目」的指令：必须【同时】含一个 http(s) 链接、【且】含触发词。
    /// 调用方只应把主人本人的消息正文传进来，不要把「被引用的他人内容」当指令来源。
    /// mode：默认 test；文本里说「只下载 / 只克隆 / 只看 / 不用跑测试」则给 clone。
    /// </summary>
    public static bool TryParseDevTask(string? text, out string url, out string mode)
    {
        url = "";
        mode = "test";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var m = Regex.Match(text, @"https?://[^\s""'<>）】\)\]]+", RegexOptions.IgnoreCase);
        if (!m.Success)
            return false;
        url = m.Value.Trim().TrimEnd('.', ',', '。', '，', ';', '；');

        bool hasTrigger = TriggerWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (!hasTrigger)
            return false;

        if (text.Contains("只下载") || text.Contains("只克隆") || text.Contains("只拉") || text.Contains("只分析")
            || text.Contains("不用跑") || text.Contains("别跑测试") || text.Contains("别测试"))
            mode = "clone";
        return true;
    }

    // ---------------- 主入口 ----------------

    /// <summary>
    /// 下载并（可选）测试一个项目，返回给人看的中文报告。任何异常都兜底，不抛。
    /// </summary>
    public async Task<string> RunAsync(string url, string mode, Action<string>? log, CancellationToken ct)
    {
        void Say(string s)
        {
            try { log?.Invoke(s); } catch { }
        }

        BiliScrollConfig config = getConfig();
        if (!config.DevTaskEnabled)
            return "「本机项目下载测试」总开关是关的：要先用控制台「本机项目下载测试」卡打开，或在对话里用 <BiliScrollSetConfig devTaskEnabled=\"true\"/>。";

        string wantMode = (mode ?? "").Trim().ToLowerInvariant() == "clone" ? "clone" : "test";

        // 1) 解析 URL 与来源
        if (string.IsNullOrWhiteSpace(url) || !Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase))
            return "这个地址不是有效的 http(s) 链接，无法下载。请给一个完整的项目地址（例如 https://github.com/owner/repo）。";

        string host;
        try
        {
            host = new Uri(url).Host.ToLowerInvariant();
        }
        catch
        {
            return "这个地址解析不了，无法下载。请给一个完整的项目地址。";
        }

        var allowedHosts = AllowedHosts(config);
        if (!allowedHosts.Contains(host))
            return $"拒绝下载：来源 {host} 不在白名单内。当前白名单：{string.Join("、", allowedHosts)}（可在控制台「本机项目下载测试」卡修改，仅支持白名单来源）。";

        var (owner, repo) = ParseOwnerRepo(url);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return "没能从这个地址里解析出 owner/repo（目前只支持 GitHub 风格的项目地址，例如 https://github.com/owner/repo）。";

        // 2) 准备沙箱目录
        string safeRepo = SanitizeName(repo);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string workDir = Path.Combine(outsideDir, safeRepo + "-" + stamp);
        try
        {
            Directory.CreateDirectory(workDir);
        }
        catch (Exception e)
        {
            return $"创建沙箱目录失败：{e.Message}";
        }
        Say($"沙箱目录：{workDir}");

        string projectDir = workDir;
        bool downloaded = false;
        try
        {
            // 3) 下载：优先 git clone，无 git（或克隆失败）则下 zip
            string git = ResolveExe("git");
            if (!string.IsNullOrEmpty(git))
            {
                Say("检测到本机 git，优先用 git clone --depth 1 下载…");
                var clone = await RunProcessAsync(git, new[] { "clone", "--depth", "1", url, workDir }, outsideDir,
                    TimeSpan.FromMinutes(Math.Min(DownloadTimeoutMinutes, ClampedMinutes(config))), Say, ct);
                if (clone.Ok)
                {
                    downloaded = true;
                }
                else
                {
                    Say($"git clone 未成功（{clone.Note}），改用 zip 下载兜底。");
                    TryCleanup(workDir);
                    Directory.CreateDirectory(workDir);
                }
            }
            else
            {
                Say("未检测到本机 git，改用 zip 下载。");
            }

            if (!downloaded)
            {
                string zipLog = "";
                string okBranch = "";
                string zipPath = Path.Combine(Path.GetTempPath(), "biliscroll_dev_" + Guid.NewGuid().ToString("N") + ".zip");
                foreach (string branch in Branches)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    string zipUrl = $"https://codeload.github.com/{owner}/{repo}/zip/refs/heads/{branch}";
                    Say($"尝试下载分支 {branch}：{zipUrl}");
                    TryDelete(zipPath);
                    if (await DownloadFileAsync(zipUrl, zipPath, Say, ct))
                    {
                        okBranch = branch;
                        break;
                    }
                    zipLog = $"分支 {branch} 下载失败";
                }

                if (okBranch.Length == 0)
                {
                    TryCleanup(workDir);
                    return $"下载失败：git 与 zip（{string.Join("、", Branches)}）都没能拿到。{zipLog}。可能网络不通、仓库不存在，或仓库不叫 {owner}/{repo}。";
                }

                try
                {
                    projectDir = ExtractZip(zipPath, workDir);
                    downloaded = true;
                }
                catch (Exception e)
                {
                    TryDelete(zipPath);
                    TryCleanup(workDir);
                    return $"解压失败（{okBranch} 分支）：{e.Message}，已清理半成品。";
                }
                finally
                {
                    TryDelete(zipPath);
                }
                Say($"已下载并解压（分支 {okBranch}）。");
            }

            // 4) 识别类型 + 选择命令
            string type = DetectProjectType(projectDir);
            string typeText = type.Length == 0 ? "未识别（没有 package.json / *.sln / *.csproj / pyproject.toml / Cargo.toml 等标志）" : TypeText(type);
            var step = BuildTestStep(type, projectDir);
            var tools = DetectTools();

            var sb = new StringBuilder();
            sb.AppendLine("【本机项目下载测试 · 结果】");
            sb.AppendLine($"项目：{owner}/{repo}（来源 {host}）");
            sb.AppendLine($"本地目录：{projectDir}");
            sb.AppendLine($"识别类型：{typeText}");

            // 5) 演练模式：只报告将执行的命令，不真正执行
            if (config.DevTaskDryRun)
            {
                sb.AppendLine($"即将执行的命令：{step.Display}");
                if (step.Need.Length > 0 && !tools.ContainsKey(step.Need))
                    sb.AppendLine($"注意：本机未检测到 {step.Need}，真跑时可能需要先安装；当前是演练模式，不执行。");
                sb.AppendLine("说明：当前为演练模式（只下载 + 识别类型，不真正跑测试）。想真跑就在控制台关掉「演练」再下指令。");
                sb.AppendLine(ToolsLine(tools));
                Say("演练模式：已完成下载与类型识别，未执行测试。");
                return sb.ToString();
            }

            // 6) 真跑测试
            if (type.Length == 0 || step.Program.Length == 0)
            {
                sb.AppendLine("没有可执行的「标准测试」命令（未识别到项目类型，或所需工具不在白名单命令里）。");
                sb.AppendLine(ToolsLine(tools));
                return sb.ToString();
            }
            if (step.Need.Length > 0 && !tools.ContainsKey(step.Need))
            {
                sb.AppendLine($"未执行测试：本机缺少 {step.Need}（{step.Display} 跑不起来）。装好它再下指令即可。");
                sb.AppendLine(ToolsLine(tools));
                return sb.ToString();
            }

            Say($"开始执行：{step.Display}（工作目录 {projectDir}）");
            var run = await RunProcessAsync(step.Program, step.Args, projectDir,
                TimeSpan.FromMinutes(ClampedMinutes(config)), Say, ct);

            sb.AppendLine($"执行命令：{step.Display}");
            if (run.TimedOut)
                sb.AppendLine($"结果：超时（超过 {ClampedMinutes(config)} 分钟，已终止进程树）。");
            else
                sb.AppendLine($"结果：{(run.Ok ? "通过（退出码 0）" : "未通过（退出码 " + run.Code + "）")}");
            sb.AppendLine(run.Output.Length == 0 ? "（命令没有输出）" : "输出（末尾最多 200 行）：\n" + run.Output.TrimEnd());
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            TryCleanup(workDir);
            return "任务已取消（插件已被卸载或请求取消），半成品已清理。";
        }
        catch (Exception e)
        {
            logger.LogWarning($"本机项目下载测试失败：{e}");
            TryCleanup(workDir);
            return $"执行失败：{e.Message}（半成品已清理）。";
        }
    }

    int ClampedMinutes(BiliScrollConfig config) => Math.Clamp(config.DevTaskMaxMinutes, 1, 60);

    // ---------------- 来源白名单 ----------------

    static HashSet<string> AllowedHosts(BiliScrollConfig config)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (config.DevTaskAllowedHosts ?? "").Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string h = raw.Trim().ToLowerInvariant();
            if (h.Length > 0)
                set.Add(h);
        }
        if (set.Count == 0)
            set.Add("github.com");
        foreach (string mirror in MirrorHosts)
            set.Add(mirror);   // 加速镜像始终允许
        return set;
    }

    /// <summary>从 URL 里取出 GitHub 风格的 owner/repo（也兼容 ghproxy/gitclone 这类把 github.com 拼在路径里的镜像）。</summary>
    static (string Owner, string Repo) ParseOwnerRepo(string url)
    {
        try
        {
            string body = url;
            int idx = body.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                body = body[(idx + "github.com".Length)..];
            else
                body = new Uri(url).AbsolutePath;

            var parts = body.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(s => s != ":").ToArray();
            if (parts.Length >= 2)
            {
                string repo = parts[1];
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    repo = repo[..^4];
                return (parts[0], repo);
            }
        }
        catch { }
        return ("", "");
    }

    static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        string s = sb.ToString().Trim('.', '_');
        return s.Length == 0 ? "repo" : (s.Length > 60 ? s[..60] : s);
    }

    // ---------------- 下载 / 解压 ----------------

    async Task<bool> DownloadFileAsync(string url, string dest, Action<string>? log, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log?.Invoke($"下载返回 {(int)resp.StatusCode}（{url}）");
                return false;
            }
            await using var fs = File.Create(dest);
            await resp.Content.CopyToAsync(fs, ct);
            return new FileInfo(dest).Length > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log?.Invoke($"下载失败：{e.Message}");
            return false;
        }
    }

    static string ExtractZip(string zip, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(zip, targetDir, overwriteFiles: true);
        // GitHub 的 zip 内通常只有一个顶层目录（repo-branch），取它作为项目根
        string[] dirs = Directory.GetDirectories(targetDir);
        string[] files = Directory.GetFiles(targetDir);
        return (dirs.Length == 1 && files.Length == 0) ? dirs[0] : targetDir;
    }

    /// <summary>清理半成品目录（失败即删，避免留下垃圾）。</summary>
    void TryCleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            logger.LogDebug($"清理沙箱目录失败（{dir}）：{e.Message}");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- 类型识别 / 命令选择 ----------------

    static string TypeText(string type) => type switch
    {
        "node" => "node（Node.js / npm 项目）",
        "dotnet" => ".NET（sln / csproj）",
        "python" => "python（pyproject / requirements / setup.py）",
        "rust" => "rust（Cargo.toml）",
        _ => "未识别"
    };

    static string DetectProjectType(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "package.json")))
                return "node";
            if (HasFileDeep(dir, "*.sln") || HasFileDeep(dir, "*.csproj"))
                return "dotnet";
            if (File.Exists(Path.Combine(dir, "pyproject.toml"))
                || File.Exists(Path.Combine(dir, "requirements.txt"))
                || File.Exists(Path.Combine(dir, "setup.py")))
                return "python";
            if (File.Exists(Path.Combine(dir, "Cargo.toml")))
                return "rust";
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
        return "";
    }

    static bool HasFileDeep(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 4
            }).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一条准备执行的命令：可执行文件、参数、给人看的显示串、所需工具名（空=无需特定工具）。</summary>
    readonly record struct TestStep(string Program, IReadOnlyList<string> Args, string Display, string Need);

    static TestStep BuildTestStep(string type, string dir)
    {
        switch (type)
        {
            case "node":
            {
                bool hasTest = ReadPackageHasScript(dir, "test");
                string npm = ResolveExe("npm");
                if (hasTest)
                    return new TestStep(npm, new[] { "test" }, "npm test", "npm");
                return new TestStep(npm, new[] { "run", "build" }, "npm run build", "npm");
            }
            case "dotnet":
            {
                string dotnet = ResolveExe("dotnet");
                if (HasTestProject(dir))
                    return new TestStep(dotnet, new[] { "test" }, "dotnet test", "dotnet");
                return new TestStep(dotnet, new[] { "build" }, "dotnet build", "dotnet");
            }
            case "python":
            {
                if (!string.IsNullOrEmpty(ResolveExe("pytest")))
                {
                    string pytest = ResolveExe("pytest");
                    return new TestStep(pytest, Array.Empty<string>(), "pytest", "pytest");
                }
                string python = ResolveExe("python");
                return new TestStep(python, new[] { "-m", "build" }, "python -m build", "python");
            }
            case "rust":
            {
                string cargo = ResolveExe("cargo");
                return new TestStep(cargo, new[] { "test" }, "cargo test", "cargo");
            }
            default:
                return new TestStep("", Array.Empty<string>(), "（无可执行的标准测试）", "");
        }
    }

    /// <summary>读 package.json 看 scripts 里有没有某个脚本（简易文本匹配，够用）。</summary>
    static bool ReadPackageHasScript(string dir, string script)
    {
        try
        {
            string p = Path.Combine(dir, "package.json");
            if (!File.Exists(p))
                return false;
            string text = File.ReadAllText(p);
            int scriptsIdx = text.IndexOf("\"scripts\"", StringComparison.Ordinal);
            if (scriptsIdx < 0)
                return false;
            return text.IndexOf("\"" + script + "\"", scriptsIdx, StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否存在「测试工程」迹象（*Test*.csproj / *.sln，或名为 test/tests 的目录）。</summary>
    static bool HasTestProject(string dir)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.csproj", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }))
            {
                if (Path.GetFileNameWithoutExtension(f).Contains("test", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (string d in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
            {
                string name = Path.GetFileName(d).ToLowerInvariant();
                if (name is "test" or "tests")
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ---------------- 环境探测 ----------------

    static Dictionary<string, bool> DetectTools()
    {
        var map = new Dictionary<string, bool>();
        map["git"] = !string.IsNullOrEmpty(ResolveExe("git"));
        map["node"] = !string.IsNullOrEmpty(ResolveExe("node"));
        map["npm"] = !string.IsNullOrEmpty(ResolveExe("npm"));
        map["dotnet"] = !string.IsNullOrEmpty(ResolveExe("dotnet"));
        map["python"] = !string.IsNullOrEmpty(ResolveExe("python"));
        map["pytest"] = !string.IsNullOrEmpty(ResolveExe("pytest"));
        map["cargo"] = !string.IsNullOrEmpty(ResolveExe("cargo"));
        return map;
    }

    static string ToolsLine(Dictionary<string, bool> tools)
    {
        var parts = tools.Select(kv => $"{kv.Key} {(kv.Value ? "有" : "无")}");
        return "本机工具： " + string.Join(" ｜ ", parts);
    }

    /// <summary>解析命令名为本机可执行文件全路径：优先原名，再依次试 .exe / .cmd / .bat（Windows 上 npm 等是 .cmd）。</summary>
    static string ResolveExe(string baseName)
    {
        foreach (string cand in new[] { baseName, baseName + ".exe", baseName + ".cmd", baseName + ".bat" })
        {
            string p = WhichExe(cand);
            if (!string.IsNullOrEmpty(p))
                return p;
        }
        return "";
    }

    /// <summary>用 where 在 PATH 里找一个可执行文件（找不到返回空串）。</summary>
    static string WhichExe(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(name);
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

    // ---------------- 进程执行（结构化 ArgumentList，超时 + 输出上限） ----------------

    readonly record struct RunOutcome(bool Ok, int Code, bool TimedOut, string Output, string Note);

    async Task<RunOutcome> RunProcessAsync(string exePath, IReadOnlyList<string> args, string workDir, TimeSpan timeout, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(exePath))
            return new RunOutcome(false, -1, false, "", "找不到可执行文件");

        ProcessStartInfo psi;
        try
        {
            psi = BuildStartInfo(exePath, args, workDir);
        }
        catch (Exception e)
        {
            return new RunOutcome(false, -1, false, "", e.Message);
        }

        var sb = new StringBuilder();
        int lines = 0;
        object gate = new object();
        void Append(string? line)
        {
            if (line == null)
                return;
            lock (gate)
            {
                if (lines >= OutputLineCap)
                    return;
                sb.AppendLine(line);
                lines++;
            }
            try { log?.Invoke("  " + line); } catch { }
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Append(e.Data);
        p.ErrorDataReceived += (_, e) => Append(e.Data);

        if (!p.Start())
            return new RunOutcome(false, -1, false, "", "无法启动进程：" + Path.GetFileName(exePath));
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            bool timedOut = !ct.IsCancellationRequested;
            return new RunOutcome(false, -1, timedOut, sb.ToString(), timedOut ? "超时" : "已取消");
        }
        return new RunOutcome(p.ExitCode == 0, p.ExitCode, false, sb.ToString(), "");
    }

    /// <summary>
    /// 组装 ProcessStartInfo：Windows 上 .cmd/.bat（npm 等）不是 PE，CreateProcess 无法直接执行，
    /// 需经 cmd.exe 调用；这里用【参数数组】逐个追加（绝不拼 shell 字符串），其余情况直接跑可执行文件。
    /// </summary>
    static ProcessStartInfo BuildStartInfo(string exePath, IReadOnlyList<string> args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        bool isBatch = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                       || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isBatch)
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exePath);
        }
        else
        {
            psi.FileName = exePath;
        }

        foreach (string a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }
}
