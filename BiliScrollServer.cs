using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>控制台服务对宿主的依赖（由 BiliScrollModule 实现），避免服务反向耦合具体模块类型。</summary>
public interface IBiliScrollHost
{
    BiliScrollConfig Config { get; }
    string PluginDirectory { get; }
    string Version { get; }
    string CharacterName { get; }
    bool IsLoggedIn { get; }
    string UserName { get; }
    long UserMid { get; }
    bool IsRunning { get; }
    string LastError { get; }
    RoundResult? LastResult { get; }
    int RoundsToday { get; }

    void UpdateConfig(BiliScrollConfig config);
    Task<RoundResult> RunRoundAsync(string trigger);
    Task<(string Key, string Url)> QrGenerateAsync();
    Task<QrPollResult> QrPollAsync(string key);
    Task LogoutAsync();
    /// <summary>复核最近自己发过的评论是否公开可见，并把结论追加到今天的日报末尾。</summary>
    Task<string> VerifyCommentsAsync();
    /// <summary>请求停止正在跑的一轮。</summary>
    void StopRound();

    /// <summary>当前「AI 写操作权限」状态清单。</summary>
    List<ActionStatus> ActionsStatus();
    /// <summary>本地转写能力探测结果（控制台显示红/绿点）。</summary>
    TranscribeCapability TranscribeCapability { get; }
    /// <summary>本地转写一键测试。</summary>
    Task<string> TestTranscribeAsync();
    /// <summary>抽帧识图是否可用（本机有 ffmpeg 且能解析到视觉模型）。</summary>
    bool VisionAvailable { get; }
    /// <summary>当前会用到的视觉模型名（未探测到为空串）。</summary>
    string VisionModelName { get; }
    /// <summary>控制台直接触发一个写操作（受权限 + 演练约束）。</summary>
    Task<string> InteractAsync(string bvid, string action, string? text);
    /// <summary>控制台直接发一条动态。</summary>
    Task<string> PublishDynamicAsync(string text);
    /// <summary>生成投稿草稿并（授权时）打开投稿页。</summary>
    Task<string> DraftUploadAsync(string? bvid, string? title, string? tags, string? desc);
    /// <summary>控制台直接发一条私信。</summary>
    Task<string> PrivateReplyAsync(long mid, string text);
    /// <summary>一键下载缺失的可选工具（ffmpeg / whisper.cpp / 语音模型），多镜像逐个尝试。</summary>
    Task<string> FetchToolsAsync(string which);
    /// <summary>「实时值守」当前状态（总开关 + 间隔）的中文描述，供控制台展示。</summary>
    string RealtimeStatus { get; }
    /// <summary>立刻跑一次「实时值守」（私信 + 评论/@ 两个通道），返回中文汇总。</summary>
    Task<string> RealtimeNowAsync();
    /// <summary>「主人指令 · 本机项目下载测试」当前状态（总开关 + 演练 + 白名单 + 超时）的中文描述。</summary>
    string DevTaskStatus { get; }
    /// <summary>控制台手动触发一次「本机项目下载测试」，返回中文报告。</summary>
    Task<string> DevTaskRunAsync(string url, string mode);
    /// <summary>各可选工具的镜像清单（供控制台展示）。</summary>
    IReadOnlyList<ToolsFetcher.ToolPlan> ToolsPlan { get; }
}

/// <summary>
/// 插件自带控制台：本地 Kestrel 服务，既托管 Resources/BiliScroll 下的设置/日报页面，
/// 也提供 REST + WebSocket 通道（配置读写、跑一轮、扫码登录、日报列表与内容、实时日志）。
///
/// 【为什么不用 ASP.NET Core】Alife 5.0.0 起，插件编译环境不再提供 StaticFiles / WebSockets 等
/// ASP.NET Core 程序集（官方同时去掉了对管理员权限与 dotnet SDK 的依赖、并增加了跨平台支持），
/// 用 Kestrel 的插件会直接编译失败。因此这里改成只用 BCL 手写极小的 HTTP/1.1 服务：
/// TcpListener 只绑 127.0.0.1，静态文件、REST 接口、WebSocket 握手与收发全部手写，零框架依赖、跨平台。
/// </summary>
public sealed class BiliScrollServer : IAsyncDisposable
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>WebSocket 握手用固定魔术串（RFC 6455）。</summary>
    const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".txt"] = "text/plain; charset=utf-8",
    };

    readonly int port;
    readonly string webRootPath;
    readonly IBiliScrollHost host;
    readonly ILogger logger;
    readonly ConcurrentDictionary<string, WsClient> clients = new();
    readonly CancellationTokenSource cts = new();

    TcpListener? listener;

    public int ActualPort { get; private set; }
    public int ClientCount => clients.Count;

    public BiliScrollServer(int port, string webRootPath, IBiliScrollHost host, ILogger logger)
    {
        this.port = port;
        this.webRootPath = Path.GetFullPath(webRootPath);
        this.host = host;
        this.logger = logger;
    }

    public Task<int> StartAsync(CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            int candidate = port + attempt;
            if (!IsPortAvailable(candidate))
                continue;
            try
            {
                var l = new TcpListener(IPAddress.Loopback, candidate);
                l.Start();
                listener = l;
                ActualPort = candidate;
                _ = Task.Run(AcceptLoop, CancellationToken.None);
                logger.LogInformation($"刷B站控制台已启动：http://127.0.0.1:{candidate}");
                return Task.FromResult(candidate);
            }
            catch (Exception e)
            {
                logger.LogWarning($"控制台端口 {candidate} 启动失败：{e.GetType().Name}: {e.Message}");
            }
        }

        throw new Exception("刷B站控制台找不到可用端口，启动失败。");
    }

    static bool IsPortAvailable(int p)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, p);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    // ---------------- 单连接处理 ----------------

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();

                using var headCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                headCts.CancelAfter(TimeSpan.FromSeconds(30));

                var parsed = await ReadRequestHeadAsync(stream, headCts.Token);
                if (parsed == null)
                    return;

                var (method, target, headers) = parsed.Value;

                string query = "";
                int qi = target.IndexOf('?');
                if (qi >= 0)
                {
                    query = target.Substring(qi + 1);
                    target = target.Substring(0, qi);
                }
                string path;
                try { path = Uri.UnescapeDataString(target); }
                catch { path = target; }

                var ex = new HttpExchange(stream, method, path, ParseQuery(query));

                // WebSocket 升级（实时日志通道）
                if (path == "/ws" && headers.TryGetValue("upgrade", out string? up) &&
                    up.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(stream, headers);
                    return;
                }

                if (headers.TryGetValue("content-length", out string? clRaw) && int.TryParse(clRaw, out int cl) && cl > 0)
                    ex.Body = await ReadBodyTextAsync(stream, cl, cts.Token);

                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    await HandleApiAsync(ex);
                    return;
                }

                await ServeStaticAsync(ex);
            }
            catch (Exception e)
            {
                logger.LogDebug($"控制台连接异常：{e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>读 HTTP 请求头（到 \r\n\r\n 为止），返回 (方法, 原始目标, 头字典)。失败返回 null。</summary>
    static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestHeadAsync(
        NetworkStream stream, CancellationToken ct)
    {
        using var buf = new MemoryStream();
        byte[] one = new byte[1];

        while (buf.Length < 65536)
        {
            int n;
            try { n = await stream.ReadAsync(one.AsMemory(0, 1), ct); }
            catch { return null; }
            if (n <= 0) return null;

            buf.WriteByte(one[0]);
            if (buf.Length >= 4)
            {
                byte[] d = buf.GetBuffer();
                int len = (int)buf.Length;
                if (d[len - 4] == 13 && d[len - 3] == 10 && d[len - 2] == 13 && d[len - 1] == 10)
                {
                    string head = Encoding.UTF8.GetString(d, 0, len);
                    string[] lines = head.Split("\r\n");
                    string[] first = lines[0].Split(' ');
                    if (first.Length < 2) return null;

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon <= 0) continue;
                        headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    return (first[0].ToUpperInvariant(), first[1], headers);
                }
            }
        }

        return null;
    }

    static async Task<string> ReadBodyTextAsync(NetworkStream stream, int contentLength, CancellationToken ct)
    {
        byte[] buf = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n;
            try { n = await stream.ReadAsync(buf.AsMemory(read, contentLength - read), ct); }
            catch { break; }
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq) : pair;
            string v = eq >= 0 ? pair.Substring(eq + 1) : "";
            try { k = Uri.UnescapeDataString(k); v = Uri.UnescapeDataString(v); } catch { }
            dict[k] = v;
        }
        return dict;
    }

    // ---------------- 静态文件 ----------------

    async Task ServeStaticAsync(HttpExchange ex)
    {
        string? file = SafeJoin(webRootPath, ex.Path);
        if (file == null)
        {
            await WriteTextAsync(ex.Stream, "404 Not Found", "no such file: " + ex.Path);
            return;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e)
        {
            logger.LogWarning($"读取控制台资源失败：{e.Message}");
            await WriteTextAsync(ex.Stream, "500 Internal Server Error", "read failed");
            return;
        }

        string mime = Mime.TryGetValue(Path.GetExtension(file), out string? known)
            ? known
            : "application/octet-stream";

        string head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: " + mime + "\r\n" +
            "Content-Length: " + bytes.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        byte[] hb = Encoding.ASCII.GetBytes(head);
        await ex.Stream.WriteAsync(hb, 0, hb.Length);
        if (ex.Method != "HEAD")
            await ex.Stream.WriteAsync(bytes, 0, bytes.Length);
        await ex.Stream.FlushAsync();
    }

    /// <summary>把 URL 路径安全地拼到 root 下（防目录穿越）；空路径回落 index.html；不存在返回 null。</summary>
    static string? SafeJoin(string root, string path)
    {
        string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (rel.Length == 0) rel = "index.html";

        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch { return null; }

        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(full) ? full : null;
    }

    // ---------------- REST ----------------

    async Task HandleApiAsync(HttpExchange ex)
    {
        string path = ex.Path;
        try
        {
            switch (path)
            {
                case "/api/state":
                    await ex.WriteJsonAsync(new
                    {
                        ok = true,
                        config = host.Config,
                        loggedIn = host.IsLoggedIn,
                        userName = host.UserName,
                        mid = host.UserMid,
                        running = host.IsRunning,
                        version = host.Version,
                        character = host.CharacterName,
                        lastError = host.LastError,
                        lastResult = host.LastResult,
                        roundsToday = host.RoundsToday,
                        realtimeStatus = host.RealtimeStatus,
                        devTaskStatus = host.DevTaskStatus,
                        pluginDir = host.PluginDirectory,
                        todayReport = Path.Combine(host.PluginDirectory, "out", DateTime.Now.ToString("yyyy-MM-dd") + ".md"),
                        reports = ListReports()
                    });
                    break;

                case "/api/config":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var cfg = JsonSerializer.Deserialize<BiliScrollConfig>(ex.Body, JsonOpts);
                    if (cfg == null) { await ex.WriteErrorAsync("配置解析失败"); break; }
                    host.UpdateConfig(cfg);
                    Broadcast(new { type = "config", config = host.Config });
                    await ex.WriteJsonAsync(new { ok = true });
                    break;

                case "/api/run":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    if (host.IsRunning) { await ex.WriteJsonAsync(new { ok = false, message = "已有一轮在跑" }); break; }
                    _ = Task.Run(() => host.RunRoundAsync("控制台手动"));
                    await ex.WriteJsonAsync(new { ok = true, message = "已开始跑一轮，进度见下方日志" });
                    break;

                case "/api/stop":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    host.StopRound();
                    await ex.WriteJsonAsync(new { ok = true, message = "已请求停止本轮（会在当前这一步结束后生效）" });
                    break;

                case "/api/qr/generate":
                    var (key, url) = await host.QrGenerateAsync();
                    await ex.WriteJsonAsync(new { ok = key.Length > 0, key, url });
                    break;

                case "/api/qr/poll":
                    string qk = ex.GetQuery("key");
                    var poll = await host.QrPollAsync(qk);
                    await ex.WriteJsonAsync(new
                    {
                        ok = true,
                        code = poll.Code,
                        message = poll.Message,
                        loggedIn = host.IsLoggedIn,
                        userName = host.UserName,
                        mid = host.UserMid
                    });
                    break;

                case "/api/logout":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    await host.LogoutAsync();
                    Broadcast(new { type = "auth", loggedIn = false, userName = "" });
                    await ex.WriteJsonAsync(new { ok = true });
                    break;

                case "/api/verify":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    if (!host.IsLoggedIn) { await ex.WriteJsonAsync(new { ok = false, error = "未登录B站，无法复核" }); break; }
                    string verifyText = await host.VerifyCommentsAsync();
                    await ex.WriteJsonAsync(new { ok = true, text = verifyText });
                    break;

                case "/api/reports":
                    await ex.WriteJsonAsync(new { ok = true, reports = ListReports() });
                    break;

                case "/api/report":
                    string date = ex.GetQuery("date");
                    string content = ReadReport(date);
                    await ex.WriteJsonAsync(new { ok = content.Length > 0, date, content });
                    break;

                case "/api/open-folder":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    string sub = ex.GetQuery("sub");
                    string dir = sub == "out"
                        ? Path.Combine(host.PluginDirectory, "out")
                        : host.PluginDirectory;
                    Directory.CreateDirectory(dir);
                    OpenFolder(dir);
                    await ex.WriteJsonAsync(new { ok = true, path = dir });
                    break;

                case "/api/actions":
                    await ex.WriteJsonAsync(new
                    {
                        ok = true,
                        writeEnabled = host.Config.AllowWrite,
                        dryRun = host.Config.DryRun,
                        coinsPerDay = host.Config.CoinsPerDay,
                        actions = host.ActionsStatus()
                    });
                    break;

                case "/api/transcribe/status":
                    await ex.WriteJsonAsync(new { ok = true, cap = host.TranscribeCapability, visionAvailable = host.VisionAvailable, visionModel = host.VisionModelName });
                    break;

                case "/api/transcribe/test":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    await ex.WriteJsonAsync(new { ok = true, text = await host.TestTranscribeAsync() });
                    break;

                case "/api/tools/plan":
                    await ex.WriteJsonAsync(new { ok = true, plan = host.ToolsPlan });
                    break;

                case "/api/tools/fetch":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var treq = Deserialize<ToolsRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.FetchToolsAsync(treq.Which) });
                    break;

                case "/api/interact":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var ireq = Deserialize<InteractRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.InteractAsync(ireq.Bvid, ireq.Action, ireq.Text) });
                    break;

                case "/api/dynamic":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var dreq = Deserialize<TextRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.PublishDynamicAsync(dreq.Text) });
                    break;

                case "/api/draft-upload":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var ureq = Deserialize<DraftRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.DraftUploadAsync(ureq.Bvid, ureq.Title, ureq.Tags, ureq.Desc) });
                    break;

                case "/api/private-reply":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var preq = Deserialize<PrivateRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.PrivateReplyAsync(preq.Mid, preq.Text) });
                    break;

                case "/api/realtime/now":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    await ex.WriteJsonAsync(new { ok = true, text = await host.RealtimeNowAsync() });
                    break;

                case "/api/devtask/run":
                    if (ex.Method != "POST") { await ex.WriteErrorAsync("仅支持 POST"); break; }
                    var dtreq = Deserialize<DevTaskRequest>(ex.Body);
                    await ex.WriteJsonAsync(new { ok = true, text = await host.DevTaskRunAsync(dtreq.Url, dtreq.Mode) });
                    break;

                default:
                    await ex.WriteJsonAsync(new { ok = false, message = "未知接口 " + path }, "404 Not Found");
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning($"控制台接口 {path} 异常：{e.Message}");
            await ex.WriteJsonAsync(new { ok = false, message = e.Message }, "500 Internal Server Error");
        }
    }

    static T Deserialize<T>(string body) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOpts) ?? new T(); }
        catch { return new T(); }
    }

    List<object> ListReports()
    {
        var list = new List<object>();
        try
        {
            string dir = Path.Combine(host.PluginDirectory, "out");
            if (!Directory.Exists(dir))
                return list;
            foreach (string file in Directory.GetFiles(dir, "*.md").OrderByDescending(f => f))
            {
                var info = new FileInfo(file);
                list.Add(new { date = Path.GetFileNameWithoutExtension(file), size = info.Length, time = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
            }
        }
        catch { }
        return list;
    }

    string ReadReport(string date)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(date))
                date = DateTime.Now.ToString("yyyy-MM-dd");
            // 防目录穿越
            date = new string(date.Where(c => char.IsDigit(c) || c == '-').ToArray());
            string file = Path.Combine(host.PluginDirectory, "out", date + ".md");
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
        }
        catch (Exception e)
        {
            logger.LogWarning($"读取日报失败：{e.Message}");
            return "";
        }
    }

    static void OpenFolder(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------------- WebSocket（手写最小 RFC 6455） ----------------

    async Task HandleWebSocketAsync(NetworkStream stream, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("sec-websocket-key", out string? key) || string.IsNullOrEmpty(key))
        {
            await WriteTextAsync(stream, "400 Bad Request", "missing Sec-WebSocket-Key");
            return;
        }

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.UTF8.GetBytes(key.Trim() + WsGuid)));

        string resp =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";

        byte[] hb = Encoding.ASCII.GetBytes(resp);
        await stream.WriteAsync(hb, 0, hb.Length);
        await stream.FlushAsync();

        string id = Guid.NewGuid().ToString("N");
        var client = new WsClient(stream);
        clients[id] = client;
        try
        {
            // 连上先推一次当前状态，页面无需再拉
            await client.SendTextAsync(JsonSerializer.Serialize(
                new { type = "hello", loggedIn = host.IsLoggedIn, userName = host.UserName, running = host.IsRunning, version = host.Version },
                JsonOpts));

            await WsReadLoopAsync(stream, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogDebug($"控制台 WebSocket 异常：{e.Message}");
        }
        finally
        {
            clients.TryRemove(id, out _);
        }
    }

    /// <summary>读客户端帧：只关心 Close/Ping，其余帧丢弃（本插件不需要客户端上行数据）。</summary>
    static async Task WsReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] two = new byte[2];
        byte[] scratch = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, two, 2, ct)) return;

            byte opcode = (byte)(two[0] & 0x0F);
            bool masked = (two[1] & 0x80) != 0;
            long len = two[1] & 0x7F;

            if (len == 126)
            {
                byte[] ext = new byte[2];
                if (!await ReadExactAsync(stream, ext, 2, ct)) return;
                len = ((long)ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                byte[] ext = new byte[8];
                if (!await ReadExactAsync(stream, ext, 8, ct)) return;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }

            if (masked)
            {
                byte[] mask = new byte[4];
                if (!await ReadExactAsync(stream, mask, 4, ct)) return;
            }

            long remaining = len;
            while (remaining > 0)
            {
                int want = (int)Math.Min(scratch.Length, remaining);
                if (!await ReadExactAsync(stream, scratch, want, ct)) return;
                remaining -= want;
            }

            if (opcode == 0x8) return; // Close
        }
    }

    static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n;
            try { n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct); }
            catch { return false; }
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>向所有已连接的控制台页面广播一条消息。</summary>
    public void Broadcast(object payload)
    {
        string text = JsonSerializer.Serialize(payload, JsonOpts);
        foreach (var kv in clients.ToArray())
        {
            var client = kv.Value;
            _ = Task.Run(async () =>
            {
                try { await client.SendTextAsync(text); }
                catch { clients.TryRemove(kv.Key, out _); }
            });
        }
    }

    public void Log(string level, string text) => Broadcast(new { type = "log", level, text });

    /// <summary>一条已打开的 WebSocket 连接（服务端→客户端方向）。</summary>
    sealed class WsClient
    {
        readonly NetworkStream stream;
        readonly SemaphoreSlim gate = new(1, 1);

        public WsClient(NetworkStream stream) => this.stream = stream;

        public async Task SendTextAsync(string text)
        {
            byte[] frame = EncodeFrame(0x1, Encoding.UTF8.GetBytes(text));
            await gate.WaitAsync();
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length);
                await stream.FlushAsync();
            }
            finally
            {
                try { gate.Release(); } catch { }
            }
        }

        /// <summary>服务端帧不加掩码；长度按 7 / 16 / 64 位三档编码。</summary>
        static byte[] EncodeFrame(byte opcode, byte[] payload)
        {
            int len = payload.Length;
            byte[] header;
            int offset;

            if (len < 126)
            {
                header = new byte[2];
                header[1] = (byte)len;
                offset = 2;
            }
            else if (len <= 65535)
            {
                header = new byte[4];
                header[1] = 126;
                header[2] = (byte)(len >> 8);
                header[3] = (byte)(len & 0xFF);
                offset = 4;
            }
            else
            {
                header = new byte[10];
                header[1] = 127;
                for (int i = 0; i < 8; i++)
                    header[2 + i] = (byte)((long)len >> (8 * (7 - i)));
                offset = 10;
            }

            header[0] = (byte)(0x80 | opcode); // FIN + opcode

            byte[] frame = new byte[offset + len];
            Buffer.BlockCopy(header, 0, frame, 0, offset);
            Buffer.BlockCopy(payload, 0, frame, offset, len);
            return frame;
        }
    }

    // ---------------- 通用写回 ----------------

    static async Task WriteTextAsync(NetworkStream stream, string status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                      "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(hb, 0, hb.Length);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        await stream.FlushAsync();
    }

    /// <summary>一次请求的上下文：方法 / 路径 / query / 请求体，外加把 JSON 写回去的便捷方法。</summary>
    sealed class HttpExchange
    {
        public NetworkStream Stream { get; }
        public string Method { get; }
        public string Path { get; }
        public string Body { get; set; } = "";
        readonly Dictionary<string, string> query;

        public HttpExchange(NetworkStream stream, string method, string path, Dictionary<string, string> query)
        {
            Stream = stream;
            Method = method;
            Path = path;
            this.query = query;
        }

        public string GetQuery(string key) => query.TryGetValue(key, out string? v) ? v : "";

        public Task WriteJsonAsync(object payload, string status = "200 OK")
            => WriteTextAsync(Stream, status, JsonSerializer.Serialize(payload, JsonOpts));

        public Task WriteErrorAsync(string message) => WriteJsonAsync(new { ok = false, message }, "400 Bad Request");
    }

    // ---------------- 请求体 DTO ----------------

    sealed class InteractRequest
    {
        public string Bvid { get; set; } = "";
        public string Action { get; set; } = "";
        public string? Text { get; set; }
    }

    sealed class TextRequest
    {
        public string Text { get; set; } = "";
    }

    sealed class DraftRequest
    {
        public string? Bvid { get; set; }
        public string? Title { get; set; }
        public string? Tags { get; set; }
        public string? Desc { get; set; }
    }

    sealed class PrivateRequest
    {
        public long Mid { get; set; }
        public string Text { get; set; } = "";
    }

    sealed class ToolsRequest
    {
        public string Which { get; set; } = "all";
    }

    sealed class DevTaskRequest
    {
        public string Url { get; set; } = "";
        public string Mode { get; set; } = "test";
    }

    public ValueTask DisposeAsync()
    {
        try { cts.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        clients.Clear();
        try { cts.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
