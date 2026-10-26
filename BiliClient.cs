using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>接口返回的封装：code / message / data（data 已 Clone，脱离子文档生命周期）。</summary>
public sealed record ApiResponse(int Code, string Message, JsonElement Data);

/// <summary>扫码轮询结果。</summary>
public sealed record QrPollResult(int Code, string Message);

/// <summary>
/// B站 Web 接口客户端（纯 BCL：HttpClient + System.Text.Json，不依赖任何第三方包）。
/// 覆盖：匿名 buvid、wbi 签名、搜索、视频详情、字幕、评论读取、发表评论、扫码登录。
/// </summary>
public sealed class BiliClient : IDisposable
{
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    const string RefererUrl = "https://www.bilibili.com";

    readonly CookieContainer cookies = new();
    readonly HttpClient http;
    readonly ILogger logger;
    readonly Func<BiliScrollConfig>? getConfig;
    readonly CoinBudget coinBudget = new();

    string imgKey = "";
    string subKey = "";
    bool anonymousReady;
    long wbiFetchTime;

    /// <summary>当前生效的登录凭证（未登录时 IsLoggedIn=false）。</summary>
    public BiliCredential Credential { get; private set; } = new();

    public BiliClient(ILogger logger, Func<BiliScrollConfig>? getConfig = null, string? pluginDir = null)
    {
        this.logger = logger;
        this.getConfig = getConfig;
        if (!string.IsNullOrEmpty(pluginDir))
            coinBudget.Attach(Path.Combine(pluginDir, "coins.json"));
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    // ---------------- 写操作安全（权限 + 演练 + 日志）----------------

    /// <summary>某个写动作当前是否被授权（总开关 + 单项开关）。</summary>
    public bool Allowed(string key) => ActionGuard.Allowed(key, getConfig?.Invoke());

    /// <summary>是否演练模式（只记录、不真发）。默认按「是」处理，避免漏检。</summary>
    public bool DryRun => getConfig?.Invoke().DryRun ?? true;

    string ActionsLogPath => getConfig?.Invoke() is { } cfg && !string.IsNullOrEmpty(actionsLogDir)
        ? Path.Combine(actionsLogDir, "actions.log")
        : "";

    string actionsLogDir = "";

    /// <summary>设置操作日志目录（插件目录）。</summary>
    public void SetLogDirectory(string dir) => actionsLogDir = dir ?? "";

    /// <summary>把一次写操作（无论放行、拒绝还是演练）记进 actions.log，方便事后回溯。</summary>
    void LogAction(string key, string detail, string outcome)
    {
        string path = ActionsLogPath;
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{key}\t{outcome}\t{detail}";
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception e)
        {
            logger.LogDebug($"写操作日志失败：{e.Message}");
        }
    }

    /// <summary>统一入口：校验权限 + 处理演练。返回 null 表示「可以继续真正发送」，否则是应直接返回给调用方的结果。</summary>
    WriteOutcome? Gate(string key, string detail)
    {
        if (!Credential.IsLoggedIn)
        {
            string msg = "未登录B站，无法执行该写操作（请先在控制台扫码登录）。";
            LogAction(key, detail, "deny:not-logged-in");
            return WriteOutcome.Deny(msg);
        }
        if (!Allowed(key))
        {
            string msg = $"该写操作未授权（{ActionGuard.Find(key)?.Name ?? key}）。请在控制台「AI 写操作权限」里打开。";
            LogAction(key, detail, "deny:not-authorized");
            return WriteOutcome.Deny(msg);
        }
        if (DryRun)
        {
            string msg = $"演练模式：已记录但未真正发送（{ActionGuard.Find(key)?.Name ?? key}）。确认无误后可在控制台关闭演练模式。";
            LogAction(key, detail, "dry-run");
            return WriteOutcome.Dry(msg);
        }
        return null;
    }

    /// <summary>通用的表单 POST（自动补 csrf、UA、Referer、Cookie）。</summary>
    async Task<ApiResponse?> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        if (Credential.IsLoggedIn && !form.ContainsKey("csrf"))
            form["csrf"] = Credential.BiliJct;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        req.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader());

        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return ParseApiResponse(text, url);
    }

    // ---------------- Cookie / 匿名身份 ----------------

    void SetCookie(string name, string value, string domain = ".bilibili.com")
    {
        if (string.IsNullOrEmpty(value))
            return;
        try
        {
            cookies.Add(new Cookie(name, value, "/", domain));
        }
        catch (Exception e)
        {
            logger.LogDebug($"写入 Cookie {name} 失败：{e.Message}");
        }
    }

    string? GetCookie(string name)
    {
        foreach (var uri in new[] { new Uri("https://api.bilibili.com"), new Uri("https://www.bilibili.com") })
        {
            var c = cookies.GetCookies(uri)[name];
            if (c != null && !string.IsNullOrEmpty(c.Value))
                return c.Value;
        }
        return null;
    }

    /// <summary>取出当前 CookieContainer 里所有 cookie，拼成 Cookie 头（发评论等需要）。</summary>
    string BuildCookieHeader()
    {
        var seen = new Dictionary<string, string>();
        foreach (var uri in new[] { new Uri("https://api.bilibili.com"), new Uri("https://www.bilibili.com"), new Uri("https://passport.bilibili.com") })
        {
            foreach (Cookie c in cookies.GetCookies(uri))
                seen[c.Name] = c.Value;
        }
        return string.Join("; ", seen.Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>确保拿到 buvid3/buvid4（未登录也要，否则搜索会被风控 -412）。</summary>
    public async Task EnsureAnonymousAsync(CancellationToken ct = default)
    {
        if (anonymousReady && GetCookie("buvid3") != null)
            return;

        try
        {
            var spi = await GetApiAsync("https://api.bilibili.com/x/frontend/finger/spi", null, false, ct);
            if (spi != null && spi.Code == 0)
            {
                if (spi.Data.TryGetProperty("b_3", out var b3))
                    SetCookie("buvid3", b3.GetString() ?? "");
                if (spi.Data.TryGetProperty("b_4", out var b4))
                    SetCookie("buvid4", b4.GetString() ?? "");
            }
        }
        catch (Exception e)
        {
            logger.LogDebug($"获取 buvid 失败：{e.Message}");
        }

        // 顺带访问一次首页，让服务端补发 b_nut 等 cookie
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RefererUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await http.SendAsync(req, ct);
        }
        catch (Exception e)
        {
            logger.LogDebug($"预热首页失败：{e.Message}");
        }

        anonymousReady = true;
    }

    /// <summary>刷新 wbi 密钥（每日更替，缓存 6 小时）。</summary>
    public async Task EnsureWbiKeysAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(imgKey) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - wbiFetchTime < 6 * 3600)
            return;

        await EnsureAnonymousAsync(ct);

        try
        {
            var nav = await GetApiAsync("https://api.bilibili.com/x/web-interface/nav", null, false, ct);
            if (nav != null && nav.Data.ValueKind == JsonValueKind.Object
                && nav.Data.TryGetProperty("wbi_img", out var wbi))
            {
                string imgUrl = wbi.TryGetProperty("img_url", out var iu) ? iu.GetString() ?? "" : "";
                string subUrl = wbi.TryGetProperty("sub_url", out var su) ? su.GetString() ?? "" : "";
                imgKey = WbiSigner.ExtractKey(imgUrl);
                subKey = WbiSigner.ExtractKey(subUrl);
                wbiFetchTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }
        catch (Exception e)
        {
            logger.LogWarning($"获取 wbi 密钥失败：{e.Message}");
        }
    }

    // ---------------- 通用请求 ----------------

    /// <summary>GET 一个 B站 接口，返回解析后的 JSON。</summary>
    async Task<ApiResponse?> GetApiAsync(string url, Dictionary<string, string>? parameters, bool sign, CancellationToken ct)
    {
        string query;
        if (sign)
        {
            await EnsureWbiKeysAsync(ct);
            query = string.IsNullOrEmpty(imgKey)
                ? BuildPlainQuery(parameters)
                : WbiSigner.Sign(parameters ?? new Dictionary<string, string>(), imgKey, subKey);
        }
        else
        {
            query = BuildPlainQuery(parameters);
        }

        string finalUrl = string.IsNullOrEmpty(query) ? url : url + "?" + query;

        using var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return ParseApiResponse(text, url);
    }

    static string BuildPlainQuery(Dictionary<string, string>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return "";
        return string.Join("&", parameters
            .Where(kv => kv.Value != null)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    ApiResponse? ParseApiResponse(string text, string url)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            logger.LogWarning($"B站接口返回空：{url}");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            int code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
            string message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
            JsonElement data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            return new ApiResponse(code, message, data);
        }
        catch (Exception e)
        {
            logger.LogWarning($"解析 B站接口响应失败（{url}）：{e.Message}");
            return null;
        }
    }

    // ---------------- 搜索 ----------------

    /// <summary>
    /// 按关键词搜索视频（返回该页 20 条）。
    /// <paramref name="order"/>：搜索排序，pubdate=最新（默认）、totalrank=综合、click=播放多、dm=弹幕多、stow=收藏多、scores=评论多。
    /// <paramref name="durationBucket"/>：B站时长档位，0=全部、1=10分钟以下、2=10-30分钟、3=30-60分钟、4=60分钟以上（本插件多在本地按秒精确过滤，此处一般传 0）。
    /// </summary>
    public async Task<List<VideoCandidate>> SearchAsync(string keyword, int page = 1, CancellationToken ct = default, string order = "pubdate", int durationBucket = 0)
    {
        var pars = new Dictionary<string, string>
        {
            ["search_type"] = "video",
            ["keyword"] = keyword,
            ["page"] = page.ToString(),
            ["order"] = order,
            ["duration"] = durationBucket.ToString()
        };
        var resp = await GetApiAsync("https://api.bilibili.com/x/web-interface/wbi/search/type", pars, true, ct);
        var list = new List<VideoCandidate>();
        if (resp == null || resp.Code != 0)
            return list;

        if (!resp.Data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in result.EnumerateArray())
        {
            string bvid = GetString(item, "bvid");
            if (string.IsNullOrEmpty(bvid))
                continue;
            list.Add(new VideoCandidate
            {
                Bvid = bvid,
                Aid = GetLong(item, "aid"),
                Title = StripHtml(GetString(item, "title")),
                Author = GetString(item, "author"),
                AuthorMid = GetLong(item, "mid"),
                DurationSec = ParseDurationText(GetString(item, "duration")),
                PubDate = GetLong(item, "pubdate"),
                Description = StripHtml(GetString(item, "description")),
                Play = GetLong(item, "play"),
                Like = GetLong(item, "like"),
                Danmaku = GetLong(item, "danmaku"),
                Favorite = GetLong(item, "favorites"),
                Coin = GetLong(item, "coin"),
                Reply = GetLong(item, "review"),
                Keyword = keyword
            });
        }
        return list;
    }

    // ---------------- 视频详情 ----------------

    public async Task<VideoDetail?> GetVideoAsync(string bvid, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string> { ["bvid"] = bvid };
        var resp = await GetApiAsync("https://api.bilibili.com/x/web-interface/wbi/view", pars, true, ct);
        if (resp == null || resp.Code != 0)
            return null;

        var d = resp.Data;
        var detail = new VideoDetail
        {
            Bvid = GetString(d, "bvid"),
            Aid = GetLong(d, "aid"),
            Cid = GetLong(d, "cid"),
            Title = GetString(d, "title"),
            Desc = GetString(d, "desc"),
            DurationSec = GetLong(d, "duration"),
            PubDate = GetLong(d, "pubdate")
        };
        if (d.TryGetProperty("owner", out var owner))
        {
            detail.Author = GetString(owner, "name");
            detail.AuthorMid = GetLong(owner, "mid");
        }
        if (d.TryGetProperty("stat", out var stat))
        {
            detail.View = GetLong(stat, "view");
            detail.Danmaku = GetLong(stat, "danmaku");
            detail.Reply = GetLong(stat, "reply");
            detail.Favorite = GetLong(stat, "favorite");
            detail.Coin = GetLong(stat, "coin");
            detail.Share = GetLong(stat, "share");
            detail.Like = GetLong(stat, "like");
        }
        // 分P：多P视频逐集列出（cid 也在里面）
        if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pages.EnumerateArray())
            {
                detail.Pages.Add(new VideoPage
                {
                    Page = (int)GetLong(p, "page"),
                    Cid = GetLong(p, "cid"),
                    Part = GetString(p, "part"),
                    DurationSec = GetLong(p, "duration")
                });
            }
        }
        // 若 cid 缺失，取第一个分P
        if (detail.Cid == 0 && detail.Pages.Count > 0)
            detail.Cid = detail.Pages[0].Cid;
        return detail;
    }

    // ---------------- 字幕 ----------------

    /// <summary>
    /// 从字幕轨里按语言优先级挑一条：ai-zh（AI 中文）→ zh-CN → zh-Hans → zh → 其它中文 → 第一条。
    /// （吸收自 BiliNotes：AI 中文字幕通常最完整，故优先。）
    /// </summary>
    public static SubtitleTrack? PickSubtitle(List<SubtitleTrack> tracks)
    {
        if (tracks.Count == 0)
            return null;
        string[] order = { "ai-zh", "zh-CN", "zh-Hans", "zh-Hans-CN", "zh", "zh-TW" };
        foreach (string lan in order)
        {
            var hit = tracks.FirstOrDefault(t => string.Equals(t.Lan, lan, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return hit;
        }
        return tracks.FirstOrDefault(t => t.Lan.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ?? tracks[0];
    }

    /// <summary>取字幕轨列表（需登录，否则为空）。</summary>
    public async Task<List<SubtitleTrack>> GetSubtitlesAsync(long aid, long cid, string bvid, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string>
        {
            ["aid"] = aid.ToString(),
            ["cid"] = cid.ToString()
        };
        if (!string.IsNullOrEmpty(bvid))
            pars["bvid"] = bvid;

        var resp = await GetApiAsync("https://api.bilibili.com/x/player/wbi/v2", pars, true, ct);
        var tracks = new List<SubtitleTrack>();
        if (resp == null || resp.Code != 0)
            return tracks;

        if (!resp.Data.TryGetProperty("subtitle", out var sub) || sub.ValueKind != JsonValueKind.Object)
            return tracks;
        if (!sub.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array)
            return tracks;

        foreach (var s in subs.EnumerateArray())
        {
            string url = GetString(s, "subtitle_url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            tracks.Add(new SubtitleTrack
            {
                Lan = GetString(s, "lan"),
                LanDoc = GetString(s, "lan_doc"),
                Url = url.StartsWith("//") ? "https:" + url : url,
                IsAi = GetLong(s, "ai_status") != 0
            });
        }
        return tracks;
    }

    /// <summary>下载字幕 JSON 并拼成纯文本。</summary>
    public async Task<string> GetSubtitleTextAsync(string subtitleUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl))
            return "";

        using var req = new HttpRequestMessage(HttpMethod.Get, subtitleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                return "";
            var sb = new StringBuilder();
            foreach (var line in body.EnumerateArray())
            {
                string content = GetString(line, "content");
                if (!string.IsNullOrWhiteSpace(content))
                    sb.AppendLine(content);
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            logger.LogDebug($"解析字幕 JSON 失败：{e.Message}");
            return "";
        }
    }

    // ---------------- 弹幕 ----------------

    /// <summary>
    /// 取弹幕（吸收自 dsh-bili-scroll 的 bili_danmaku / BiliNotes）：走经典 XML 接口，无需 protobuf。
    /// 按出现时间抽样，最多返回 limit 条 —— 弹幕是判断"值不值得看"的免费素材（观众真实反应）。
    /// </summary>
    public async Task<List<DanmakuItem>> GetDanmakuAsync(long cid, int limit = 60, CancellationToken ct = default)
    {
        var list = new List<DanmakuItem>();
        if (cid == 0)
            return list;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://comment.bilibili.com/{cid}.xml");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
            using var resp = await http.SendAsync(req, ct);
            string xml = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(xml))
                return list;

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var nodes = doc.Descendants("d").ToList();
            foreach (var node in SampleEvenly(nodes, limit))
            {
                string p = node.Attribute("p")?.Value ?? "";
                string text = node.Value;
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var seg = p.Split(',');
                list.Add(new DanmakuItem
                {
                    Time = seg.Length > 0 && double.TryParse(seg[0], out double t) ? t : 0,
                    Mode = seg.Length > 1 && int.TryParse(seg[1], out int m) ? m : 1,
                    Text = StripHtml(text)
                });
            }
        }
        catch (Exception e)
        {
            logger.LogDebug($"获取弹幕 {cid} 失败：{e.Message}");
        }
        return list;
    }

    /// <summary>把一整条时间轴上的弹幕等距抽样成 limit 条（避免只拿到开头那几秒的刷屏）。</summary>
    static IEnumerable<System.Xml.Linq.XElement> SampleEvenly(List<System.Xml.Linq.XElement> all, int limit)
    {
        if (all.Count <= limit)
            return all;
        var picked = new List<System.Xml.Linq.XElement>(limit);
        double step = (double)all.Count / limit;
        for (int i = 0; i < limit; i++)
            picked.Add(all[(int)(i * step)]);
        return picked;
    }

    // ---------------- 评论 ----------------

    /// <summary>复核一条自己发的评论是否公开可见（发送成功 ≠ 公开可见：新号/风控会进审核）。
    /// root=0 时查视频评论列表，root!=0 时查那条根评论的楼中楼。</summary>
    public async Task<bool> CheckCommentVisibleAsync(long aid, long rpid, long root = 0, CancellationToken ct = default)
    {
        try
        {
            for (int pn = 1; pn <= 3; pn++)
            {
                var pars = new Dictionary<string, string>
                {
                    ["type"] = "1",
                    ["oid"] = aid.ToString(),
                    ["ps"] = "20",
                    ["pn"] = pn.ToString()
                };
                string api = root != 0
                    ? "https://api.bilibili.com/x/v2/reply/reply"
                    : "https://api.bilibili.com/x/v2/reply/main";
                if (root != 0)
                    pars["root"] = root.ToString();
                else
                    pars["mode"] = "3";

                var resp = await GetApiAsync(api, pars, false, ct);
                if (resp == null || resp.Code != 0)
                    continue;
                if (ContainsRpid(resp.Data, rpid))
                    return true;
            }
        }
        catch (Exception e)
        {
            logger.LogDebug($"复核评论 {rpid} 失败：{e.Message}");
        }
        return false;
    }

    static bool ContainsRpid(JsonElement el, long rpid)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("rpid", out var r) && GetLongValue(r) == rpid)
                    return true;
                if (el.TryGetProperty("rpid_str", out var rs) && GetLongValue(rs) == rpid)
                    return true;
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && ContainsRpid(prop.Value, rpid))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (ContainsRpid(item, rpid))
                        return true;
                break;
        }
        return false;
    }

    static long GetLongValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out long l) ? l : 0,
        JsonValueKind.String => long.TryParse(v.GetString(), out long l2) ? l2 : 0,
        _ => 0
    };

    /// <summary>读取视频热门评论（含楼中楼），返回最多 limit 条。upMid 传 UP 主 uid，用于豁免「UP 主本人」；不确定就留 0。</summary>
    public async Task<List<CommentItem>> GetCommentsAsync(long aid, int limit = 20, long upMid = 0, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = aid.ToString(),
            ["mode"] = "3",
            ["next"] = "1",
            ["ps"] = "20"
        };
        var resp = await GetApiAsync("https://api.bilibili.com/x/v2/reply/wbi/main", pars, true, ct);
        var list = new List<CommentItem>();
        if (resp == null || resp.Code != 0)
            return list;

        if (resp.Data.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in replies.EnumerateArray())
            {
                list.Add(ParseComment(r));
                if (list.Count >= limit)
                    break;
            }
        }
        await FilterAiCommentsAsync(list, upMid, ct).ConfigureAwait(false);
        return list;
    }

    /// <summary>读取某条根评论下的楼中楼（用于"回复评论"：只看直接回在我那条评论下面的人）。</summary>
    public async Task<List<CommentItem>> GetRepliesAsync(long aid, long root, int limit = 20, long upMid = 0, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = aid.ToString(),
            ["root"] = root.ToString(),
            ["ps"] = "20",
            ["pn"] = "1"
        };
        var resp = await GetApiAsync("https://api.bilibili.com/x/v2/reply/reply", pars, false, ct);
        var list = new List<CommentItem>();
        if (resp == null || resp.Code != 0)
            return list;

        if (resp.Data.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in replies.EnumerateArray())
            {
                list.Add(ParseComment(r));
                if (list.Count >= limit)
                    break;
            }
        }
        await FilterAiCommentsAsync(list, upMid, ct).ConfigureAwait(false);
        return list;
    }

    /// <summary>
    /// 「忽略 AI 评论」：开关打开时，按离线规则给评论打分，命中阈值的整条移除（不进判断素材、也不去回它）。
    /// 判定主要靠接口本来就返回的昵称/签名/等级/认证/会员；只有在「加了头像分可能翻盘」的账号上才额外下一张
    /// 48x48 头像（约 4~5KB）算蓝色占比——头像满蓝是那套机器人的默认皮肤。
    /// </summary>
    async Task FilterAiCommentsAsync(List<CommentItem> list, long upMid, CancellationToken ct)
    {
        if (list.Count == 0) return;
        BiliScrollConfig? cfg = getConfig?.Invoke();
        if (cfg == null || !cfg.FilterAiComments) return;

        BiliAiCommentFilter.FilterResult r;
        try
        {
            r = await BiliAiCommentFilter.ApplyAsync(list, cfg.AiCommentThreshold, upMid, GetAvatarBlueAsync, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // 判定本身出错绝不能让「读评论」这件事失败
            logger.LogWarning("忽略 AI 评论时出错，已按不过滤处理：{Msg}", e.Message);
            return;
        }

        if (r.Removed > 0)
            logger.LogInformation("忽略 AI 评论：移除 {Count} 条（阈值 {Threshold}，看了 {Avatars} 张头像）：{Why}",
                r.Removed, cfg.AiCommentThreshold, r.AvatarsChecked, r.Detail);
    }

    /// <summary>URL → 头像蓝色占比（0~1，-1 表示没取到）。同一账号在一次浏览里只下一张头像。</summary>
    readonly ConcurrentDictionary<string, double> avatarBlueCache = new();

    /// <summary>
    /// 下载头像并算「鲸鱼蓝」占比。统一取 48x48 的 PNG 变体（约 4~5KB；原图可能几十上百 KB）。
    /// 头像只是加分项，所以只给 3 秒超时——取不到就当未知处理，绝不拖慢一轮。
    /// </summary>
    async Task<double> GetAvatarBlueAsync(string avatar, CancellationToken ct)
    {
        string url = NormalizeAvatarUrl(avatar);
        if (url.Length == 0) return -1;
        if (avatarBlueCache.TryGetValue(url, out double cached)) return cached;

        double ratio = -1;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            byte[] bytes = await http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
            ratio = BiliAiCommentFilter.BlueRatio(bytes);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 超时/404/风控都按「没看过」处理，下次还能重试
            logger.LogDebug("取头像失败，按未知处理：{Url} {Msg}", url, e.Message);
            return -1;
        }

        // 只缓存成功结果；失败不缓存，免得一次网络抖动就永久放弃这个账号
        if (ratio >= 0)
        {
            if (avatarBlueCache.Count > 4096) avatarBlueCache.Clear();
            avatarBlueCache[url] = ratio;
        }
        return ratio;
    }

    /// <summary>B站头像的各种尺寸变体都挂在同一个 URL 上、用 @ 后缀区分，这里统一换成 48x48 的 PNG。</summary>
    static string NormalizeAvatarUrl(string avatar)
    {
        string baseUrl = avatar.Split('@')[0].Trim();
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "https://" + baseUrl[7..];
        if (baseUrl.Length == 0 || !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
        return baseUrl + "@48w_48h.png";
    }

    static CommentItem ParseComment(JsonElement r)
    {
        var item = new CommentItem
        {
            Rpid = GetLong(r, "rpid"),
            Mid = GetLong(r, "mid"),
            Message = GetString(r, "message"),
            Ctime = GetLong(r, "ctime"),
            Like = GetLong(r, "like"),
            IsUp = r.TryGetProperty("member", out var m) && m.TryGetProperty("mid", out _) && GetLong(m, "mid") == 0
        };
        if (r.TryGetProperty("content", out var content))
        {
            string msg = GetString(content, "message");
            if (!string.IsNullOrEmpty(msg))
                item.Message = msg;
        }
        if (r.TryGetProperty("member", out var member))
        {
            item.UserName = GetString(member, "uname");
            // 下面几项是「忽略 AI 评论」的判定特征：签名、等级、官方认证、大会员、头像
            item.Sign = GetString(member, "sign");
            item.Avatar = GetString(member, "avatar");
            if (member.TryGetProperty("level_info", out var lvl))
                item.Level = (int)GetLong(lvl, "current_level");
            if (member.TryGetProperty("official_verify", out var ov))
                item.Verify = (int)GetLong(ov, "type");
            if (member.TryGetProperty("vip", out var vip))
            {
                long status = GetLong(vip, "vipStatus");
                if (status == 0) status = GetLong(vip, "status");
                if (status == 0) status = GetLong(vip, "vipType");
                item.Vip = (int)status;
            }
        }
        if (item.Mid == 0 && r.TryGetProperty("member", out var m2))
            item.Mid = GetLong(m2, "mid");

        if (r.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
        {
            foreach (var rr in replies.EnumerateArray())
                item.Replies.Add(ParseComment(rr));
        }
        return item;
    }

    // ---------------- 发评论 ----------------

    /// <summary>在视频下发一条评论；root/parent 非 0 时为回复楼中楼。受「发表评论/回复」权限与演练模式约束。</summary>
    public async Task<(WriteOutcome Outcome, long Rpid)> PostCommentAsync(
        long aid, string message, long root = 0, long parent = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (WriteOutcome.Deny("评论内容为空"), 0);

        string key = root != 0 ? "reply_comment" : "public_comment";
        WriteOutcome? gate = Gate(key, $"aid={aid} root={root} {message}");
        if (gate != null)
            return (gate, 0);

        var form = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = aid.ToString(),
            ["message"] = message,
            ["plat"] = "1",
            ["csrf"] = Credential.BiliJct
        };
        if (root != 0)
            form["root"] = root.ToString();
        if (parent != 0)
            form["parent"] = parent.ToString();

        ApiResponse? parsed = await PostFormAsync("https://api.bilibili.com/x/v2/reply/add", form, ct);
        if (parsed == null)
            return (WriteOutcome.Deny("发评论请求失败（网络或响应异常）"), 0);
        if (parsed.Code != 0)
        {
            LogAction(key, $"aid={aid}", "error:" + parsed.Code);
            return (WriteOutcome.Deny($"发评论失败：code={parsed.Code} {parsed.Message}"), 0);
        }

        long rpid = parsed.Data.ValueKind == JsonValueKind.Object ? GetLong(parsed.Data, "rpid") : 0;
        LogAction(key, $"aid={aid} rpid={rpid}", "ok");
        return (WriteOutcome.Done("评论已发出"), rpid);
    }

    // ---------------- 写操作：点赞 / 投币 / 收藏 / 关注 / 弹幕 / 动态 / 私信 ----------------

    /// <summary>点赞（like=true）或取消点赞。</summary>
    public async Task<WriteOutcome> LikeAsync(string bvid, bool like, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("video_like", $"{bvid} like={(like ? 1 : 2)}");
        if (gate != null)
            return gate;

        var form = new Dictionary<string, string> { ["bvid"] = bvid, ["like"] = like ? "1" : "2" };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/web-interface/archive/like", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("video_like", bvid, "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"{(like ? "点赞" : "取消点赞")}失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("video_like", bvid, "ok");
        return WriteOutcome.Done($"已{(like ? "点赞" : "取消点赞")} {bvid}。");
    }

    /// <summary>投币（num=1/2，可同时点赞）；受「每日投币上限」与权限双重约束。</summary>
    public async Task<WriteOutcome> CoinAsync(string bvid, int num = 1, bool alsoLike = false, CancellationToken ct = default)
    {
        num = Math.Clamp(num, 1, 2);
        int max = getConfig?.Invoke().CoinsPerDay ?? 0;
        WriteOutcome? gate = Gate("coin", $"{bvid} num={num}");
        if (gate != null)
            return gate;

        int spent = coinBudget.SpentToday();
        if (max <= 0)
            return WriteOutcome.Deny("未设置每日投币上限（配置「每天最多投几个币」为 0），不投币。");
        if (spent + num > max)
            return WriteOutcome.Deny($"今日投币额度不足（已投 {spent}/{max}），跳过。");

        var form = new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["num"] = num.ToString(),
            ["select_like"] = alsoLike ? "1" : "0"
        };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/web-interface/coin/add", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("coin", bvid, "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"投币失败：code={resp?.Code} {resp?.Message}");
        }
        coinBudget.TryConsume(max, num);
        LogAction("coin", $"{bvid} num={num}", $"ok 今日 {coinBudget.SpentToday()}/{max}");
        return WriteOutcome.Done($"已给 {bvid} 投 {num} 个币（今日 {coinBudget.SpentToday()}/{max}）。");
    }

    /// <summary>收藏视频到「默认收藏夹」。</summary>
    public async Task<WriteOutcome> FavoriteAsync(long aid, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("favorite", $"aid={aid}");
        if (gate != null)
            return gate;

        long folderId = await GetDefaultFavoriteFolderIdAsync(ct);
        if (folderId <= 0)
            return WriteOutcome.Deny("找不到默认收藏夹，无法收藏。");

        var form = new Dictionary<string, string>
        {
            ["rid"] = aid.ToString(),
            ["type"] = "2",
            ["add_media_ids"] = folderId.ToString()
        };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/v3/fav/resource/deal", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("favorite", $"aid={aid} folder={folderId}", "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"收藏失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("favorite", $"aid={aid} folder={folderId}", "ok");
        return WriteOutcome.Done($"已收藏到默认收藏夹（aid={aid}）。");
    }

    /// <summary>
    /// 把「看过这个视频」上报到 B站，从而在你的「浏览记录」里能看到她今天看了啥。
    /// 说明：B站 的浏览记录只有在真正播放并上报心跳时才写入，插件只读字幕/弹幕不会自动生成记录，
    /// 所以在分析每条视频时补一次上报。这是低风险动作（只写你账号的浏览历史），不经「AI 写操作权限」，只要求登录。
    /// 遇到 B站 反爬（可能需 wbi 签名）会失败，此时如实返回失败信息、不影响后续流程。
    /// </summary>
    public async Task<(bool Ok, string Message)> ReportHistoryAsync(long aid, long cid, long durationSec, CancellationToken ct = default)
    {
        if (!Credential.IsLoggedIn)
            return (false, "未登录，无法上报浏览记录。");
        if (aid <= 0 || cid <= 0)
            return (false, "缺少 aid/cid，跳过上报。");

        int progress = (int)Math.Max(5, Math.Min(durationSec > 0 ? durationSec : 60, 120));
        var form = new Dictionary<string, string>
        {
            ["aid"] = aid.ToString(),
            ["cid"] = cid.ToString(),
            ["progress"] = progress.ToString(),
            ["type"] = "3"
        };
        try
        {
            ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/v2/history/report", form, ct);
            if (resp == null)
            {
                LogAction("history_report", $"aid={aid}", "error:null");
                return (false, "上报浏览记录失败（无响应）。");
            }
            if (resp.Code != 0)
            {
                LogAction("history_report", $"aid={aid}", $"error:{resp.Code}");
                return (false, $"上报浏览记录失败：code={resp.Code} {resp.Message}");
            }
            LogAction("history_report", $"aid={aid} cid={cid} progress={progress}", "ok");
            return (true, $"已上报观看（{progress}s）");
        }
        catch (Exception e)
        {
            LogAction("history_report", $"aid={aid}", "exception:" + e.Message);
            return (false, $"上报浏览记录异常：{e.Message}");
        }
    }

    /// <summary>关注（follow=true）或取关某个 UP 主。</summary>
    public async Task<WriteOutcome> FollowAsync(long upMid, bool follow = true, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("follow_up", $"mid={upMid}");
        if (gate != null)
            return gate;

        var form = new Dictionary<string, string>
        {
            ["fid"] = upMid.ToString(),
            ["act"] = follow ? "1" : "2",
            ["re_src"] = "11"
        };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/relation/modify", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("follow_up", $"mid={upMid}", "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"{(follow ? "关注" : "取消关注")}失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("follow_up", $"mid={upMid}", "ok");
        return WriteOutcome.Done($"已{(follow ? "关注" : "取消关注")} UP{upMid}。");
    }

    /// <summary>发一条弹幕（progressSec 为出现时间，秒）。</summary>
    public async Task<WriteOutcome> SendDanmakuAsync(string bvid, long cid, string message, double progressSec = 0, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("send_danmaku", $"{bvid} {message}");
        if (gate != null)
            return gate;

        var form = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = cid.ToString(),
            ["msg"] = message,
            ["bvid"] = bvid,
            ["progress"] = ((long)(progressSec * 1000)).ToString(),
            ["mode"] = "1",
            ["fontsize"] = "25",
            ["color"] = "16777215"
        };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/v2/dm/post", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("send_danmaku", bvid, "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"发弹幕失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("send_danmaku", bvid, "ok");
        return WriteOutcome.Done($"已在 {bvid} 发出一条弹幕。");
    }

    /// <summary>发一条纯文字动态。</summary>
    public async Task<WriteOutcome> PublishDynamicAsync(string text, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("dynamic_publish", text);
        if (gate != null)
            return gate;

        string dynReq = JsonSerializer.Serialize(new
        {
            dyn_id_str = "0",
            platform = "web",
            attach_card = new { },
            text,
            pic = Array.Empty<object>(),
            content = Array.Empty<object>()
        });
        var form = new Dictionary<string, string> { ["dyn_req"] = dynReq, ["csrf_token"] = Credential.BiliJct };
        ApiResponse? resp = await PostFormAsync("https://api.bilibili.com/x/dynamic/feed/create/dyn", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("dynamic_publish", text, "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"发动态失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("dynamic_publish", text, "ok");
        return WriteOutcome.Done("动态已发出。");
    }

    /// <summary>给某个用户发一条私信（receiver_type=1 表示用户）。</summary>
    public async Task<WriteOutcome> SendPrivateMessageAsync(long receiverMid, string text, CancellationToken ct = default)
    {
        WriteOutcome? gate = Gate("private_reply", $"to={receiverMid}");
        if (gate != null)
            return gate;

        string content = JsonSerializer.Serialize(new { content = text });
        var form = new Dictionary<string, string>
        {
            ["msg[sender_uid]"] = Credential.Mid.ToString(),
            ["msg[receiver_id]"] = receiverMid.ToString(),
            ["msg[receiver_type]"] = "1",
            ["msg[msg_type]"] = "1",
            ["msg[content]"] = content,
            ["msg[timestamp]"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(),
            ["msg[dev_id]"] = Guid.NewGuid().ToString("D").ToUpper(),
            ["csrf_token"] = Credential.BiliJct
        };
        ApiResponse? resp = await PostFormAsync("https://api.vc.bilibili.com/web_im/v1/web_im/send_msg", form, ct);
        if (resp == null || resp.Code != 0)
        {
            LogAction("private_reply", $"to={receiverMid}", "error:" + (resp?.Code.ToString() ?? "null"));
            return WriteOutcome.Deny($"发私信失败：code={resp?.Code} {resp?.Message}");
        }
        LogAction("private_reply", $"to={receiverMid}", "ok");
        return WriteOutcome.Done($"已给用户 {receiverMid} 发出一条私信。");
    }

    // ---------------- 读操作：私信收件箱（只读，不写账号）----------------

    /// <summary>读最近的私信会话列表（含未读数与最后一条）。仅登录可用；被风控/未登录时返回空表并如实记日志。</summary>
    public async Task<List<DmSession>> GetDmSessionsAsync(int count, CancellationToken ct = default)
    {
        var result = new List<DmSession>();
        if (!Credential.IsLoggedIn)
            return result;

        var pars = new Dictionary<string, string>
        {
            ["session_type"] = "1",
            ["group_fold"] = "1",
            ["unfollow_fold"] = "0",
            ["sort_rule"] = "2",
            ["build"] = "0",
            ["mobi_app"] = "web"
        };
        ApiResponse? resp = await GetApiAsync("https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions", pars, false, ct);
        if (resp == null || resp.Code != 0 || resp.Data.ValueKind != JsonValueKind.Object
            || !resp.Data.TryGetProperty("session_list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning($"读私信会话失败：code={resp?.Code} {resp?.Message}");
            LogAction("dm_sessions", "read", "error:" + (resp?.Code.ToString() ?? "null"));
            return result;
        }

        foreach (var s in list.EnumerateArray())
        {
            if (result.Count >= Math.Max(1, count))
                break;
            long talkerId = s.TryGetProperty("talker_id", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            if (talkerId <= 0)
                continue;
            int unread = s.TryGetProperty("unread_count", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0;
            long ts = s.TryGetProperty("session_ts", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt64() : 0;
            string last = "";
            if (s.TryGetProperty("last_msg", out var lm) && lm.ValueKind == JsonValueKind.Object)
                last = ExtractDmText(lm);
            result.Add(new DmSession { TalkerId = talkerId, LastText = last, Unread = unread, Ts = ts });
        }
        LogAction("dm_sessions", "read", $"ok:{result.Count}");
        return result;
    }

    /// <summary>读某个会话最近若干条消息（按时间升序）。仅登录可用。</summary>
    public async Task<List<DmMessage>> GetDmMessagesAsync(long talkerId, int size, CancellationToken ct = default)
    {
        var result = new List<DmMessage>();
        if (!Credential.IsLoggedIn || talkerId <= 0)
            return result;

        var pars = new Dictionary<string, string>
        {
            ["talker_id"] = talkerId.ToString(),
            ["session_type"] = "1",
            ["size"] = Math.Clamp(size, 1, 50).ToString(),
            ["build"] = "0",
            ["mobi_app"] = "web"
        };
        ApiResponse? resp = await GetApiAsync("https://api.vc.bilibili.com/svr_sync/v1/svr_sync/fetch_session_msgs", pars, false, ct);
        if (resp == null || resp.Code != 0 || resp.Data.ValueKind != JsonValueKind.Object
            || !resp.Data.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning($"读私信消息失败：code={resp?.Code} {resp?.Message}");
            return result;
        }

        foreach (var m in msgs.EnumerateArray())
        {
            long sender = m.TryGetProperty("sender_uid", out var su) && su.ValueKind == JsonValueKind.Number ? su.GetInt64() : 0;
            long ts = m.TryGetProperty("timestamp", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt64() : 0;
            string text = ExtractDmText(m);
            if (text.Length == 0)
                continue;
            result.Add(new DmMessage { SenderUid = sender, Text = text, Ts = ts });
        }
        result.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        return result;
    }

    /// <summary>从私信消息节点里抽正文（content 字段通常是「字符串套 JSON」，形如 {"content":"你好"}）。</summary>
    static string ExtractDmText(JsonElement node)
    {
        string raw = "";
        if (node.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String)
                raw = c.GetString() ?? "";
            else if (c.ValueKind == JsonValueKind.Object)
                raw = c.ToString();
        }
        if (raw.Length == 0)
            return "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("content", out var inner))
                return (inner.GetString() ?? "").Trim();
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return (doc.RootElement.GetString() ?? "").Trim();
        }
        catch
        {
            // content 本身就是纯文本（某些消息类型）
        }
        return raw.Trim();
    }

    // ---------------- 读操作：「回复我的」消息流（别人回复我的评论 / @ 我，只读）----------------

    /// <summary>
    /// 读「回复我的」消息流（web 端 x/msgfeed/reply，靠 CookieContainer 自动带凭证）。
    /// 防御式解析 data.items[]：每条尽量取出 rpid / 昵称 / uid / oid(aid) / 楼层 / 对方正文 / 原评论内容 / 时间；
    /// 取不到的字段容忍为空/0 并打日志，绝不抛异常。
    /// </summary>
    public async Task<List<FeedReplyItem>> GetReplyFeedAsync(int count, CancellationToken ct = default)
    {
        var result = new List<FeedReplyItem>();
        if (!Credential.IsLoggedIn)
            return result;

        var pars = new Dictionary<string, string>
        {
            ["platform"] = "web",
            ["build"] = "0",
            ["mobi_app"] = "web"
        };
        ApiResponse? resp = await GetApiAsync("https://api.bilibili.com/x/msgfeed/reply", pars, false, ct);
        if (resp == null || resp.Code != 0 || resp.Data.ValueKind != JsonValueKind.Object
            || !resp.Data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning($"读「回复我的」失败：code={resp?.Code} {resp?.Message}");
            return result;
        }

        foreach (var it in items.EnumerateArray())
        {
            if (result.Count >= Math.Max(1, count))
                break;
            if (it.ValueKind != JsonValueKind.Object)
                continue;

            var fi = new FeedReplyItem();
            JsonElement itemNode = default;
            // 顶层 id 就是这条回复本身的 rpid（判重用）
            fi.Rpid = GetLong(it, "id");

            if (it.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                fi.Mid = GetLong(user, "mid");
                fi.UserName = GetString(user, "nickname");
            }

            if (it.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                itemNode = item;
                fi.BusinessId = GetLong(item, "business_id");
                fi.Oid = GetLong(item, "subject_id");      // 视频评论里就是 aid/oid
                fi.RootId = GetLong(item, "root_id");
                fi.SourceId = GetLong(item, "target_id");  // 被直接回复的那条（通常是我发的那条）
                fi.Title = GetString(item, "title");
                fi.Uri = GetString(item, "uri");
                fi.SourceMessage = GetString(item, "source_content");
                string bizType = GetString(item, "type");
                fi.IsAt = string.Equals(bizType, "at", StringComparison.OrdinalIgnoreCase);
            }

            // 对方正文：优先 reply.content.message，其次整块 content，再退回顶层 content
            if (it.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.Object)
            {
                if (reply.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                    fi.Message = GetString(content, "message");
                if (string.IsNullOrEmpty(fi.Message))
                    fi.Message = ExtractDmText(reply);
                if (string.IsNullOrEmpty(fi.SourceMessage))
                    fi.SourceMessage = GetString(reply, "source_content");
            }
            if (string.IsNullOrEmpty(fi.Message))
                fi.Message = GetString(it, "content");

            if (string.IsNullOrEmpty(fi.Bvid) && !string.IsNullOrEmpty(fi.Uri))
                fi.Bvid = ExtractBvidFromUri(fi.Uri);

            // 时间：reply_time 可能是 unix 数字，也可能是 "yyyy-MM-dd HH:mm:ss" 字符串；再退回 ctime
            fi.Ts = ParseUnixTime(it, "reply_time");
            if (fi.Ts <= 0)
                fi.Ts = ParseUnixTime(it, "ctime");
            if (fi.Ts <= 0 && itemNode.ValueKind == JsonValueKind.Object)
            {
                fi.Ts = ParseUnixTime(itemNode, "reply_time");
                if (fi.Ts <= 0)
                    fi.Ts = ParseUnixTime(itemNode, "ctime");
            }

            if (fi.Rpid == 0)
            {
                logger.LogDebug($"「回复我的」条目取不到 rpid（id 字段缺失），已跳过：nickname={fi.UserName}");
                continue;
            }
            if (string.IsNullOrEmpty(fi.Message))
                logger.LogDebug($"「回复我的」条目 rpid={fi.Rpid} 取不到正文（字段可能变动），按空正文处理。");
            if (fi.Oid <= 0)
                logger.LogDebug($"「回复我的」条目 rpid={fi.Rpid} 取不到 oid（subject_id 缺失），可能无法回复。");
            result.Add(fi);
        }
        return result;
    }

    /// <summary>把一个字段按 unix 时间解析（数字直接用；字符串先试纯数字，再试日期时间）。取不到返回 0。</summary>
    static long ParseUnixTime(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt64(out long l) ? l : 0;
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = v.GetString() ?? "";
            if (long.TryParse(s, out long l2) && l2 > 0)
                return l2;
            if (DateTimeOffset.TryParse(s, out var dt))
                return dt.ToUnixTimeSeconds();
        }
        return 0;
    }

    /// <summary>从 uri / 链接里尽力抠出 bvid（形如 BV1xx411c7mD，共 12 位）；取不到返回空串。</summary>
    static string ExtractBvidFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "";
        int i = uri.IndexOf("BV", StringComparison.Ordinal);
        if (i < 0)
            return "";
        int j = i;
        while (j < uri.Length && char.IsLetterOrDigit(uri[j]))
            j++;
        string cand = uri.Substring(i, j - i);
        return cand.Length >= 12 ? cand.Substring(0, 12) : "";
    }

    /// <summary>取「默认收藏夹」的 id（优先标题为「默认收藏夹」，否则用第一个）。</summary>
    async Task<long> GetDefaultFavoriteFolderIdAsync(CancellationToken ct)
    {
        var pars = new Dictionary<string, string> { ["up_mid"] = Credential.Mid.ToString() };
        ApiResponse? resp = await GetApiAsync("https://api.bilibili.com/x/v3/fav/folder/created/list-all", pars, false, ct);
        if (resp == null || resp.Code != 0 || resp.Data.ValueKind != JsonValueKind.Object)
            return 0;
        if (!resp.Data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return 0;

        long first = 0;
        foreach (var f in list.EnumerateArray())
        {
            long id = GetLong(f, "id");
            if (first == 0)
                first = id;
            if (string.Equals(GetString(f, "title"), "默认收藏夹", StringComparison.Ordinal))
                return id;
        }
        return first;
    }

    // ---------------- 音频（本地转写用）----------------

    /// <summary>取音频流地址（playurl fnval=16 的 DASH audio，挑带宽最小的一条最省流量/最快）。</summary>
    public async Task<string> GetAudioStreamUrlAsync(string bvid, long cid, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1"
        };
        ApiResponse? resp = await GetApiAsync("https://api.bilibili.com/x/player/wbi/playurl", pars, true, ct);
        if (resp == null || resp.Code != 0)
            resp = await GetApiAsync("https://api.bilibili.com/x/player/playurl", pars, false, ct);
        if (resp == null || resp.Code != 0)
            return "";
        if (!resp.Data.TryGetProperty("dash", out var dash)
            || !dash.TryGetProperty("audio", out var audio)
            || audio.ValueKind != JsonValueKind.Array)
            return "";

        string best = "";
        long bestBw = long.MaxValue;
        foreach (var a in audio.EnumerateArray())
        {
            string url = GetString(a, "baseUrl");
            if (string.IsNullOrEmpty(url))
                url = GetString(a, "base_url");
            if (string.IsNullOrEmpty(url))
                continue;
            long bw = GetLong(a, "bandwidth");
            if (bw <= 0)
                bw = long.MaxValue - 1;
            if (bw < bestBw)
            {
                bestBw = bw;
                best = url.StartsWith("//") ? "https:" + url : url;
            }
        }
        return best;
    }

    /// <summary>取视频流地址（playurl fnval=16 的 DASH video，挑带宽最高的一条：读画面里的文字更清楚）。</summary>
    public async Task<string> GetVideoStreamUrlAsync(string bvid, long cid, CancellationToken ct = default)
    {
        var pars = new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1"
        };
        ApiResponse? resp = await GetApiAsync("https://api.bilibili.com/x/player/wbi/playurl", pars, true, ct);
        if (resp == null || resp.Code != 0)
            resp = await GetApiAsync("https://api.bilibili.com/x/player/playurl", pars, false, ct);
        if (resp == null || resp.Code != 0)
            return "";
        if (!resp.Data.TryGetProperty("dash", out var dash)
            || !dash.TryGetProperty("video", out var video)
            || video.ValueKind != JsonValueKind.Array)
            return "";

        string best = "";
        long bestBw = -1;
        foreach (var vid in video.EnumerateArray())
        {
            string url = GetString(vid, "baseUrl");
            if (string.IsNullOrEmpty(url))
                url = GetString(vid, "base_url");
            if (string.IsNullOrEmpty(url))
                continue;
            long bw = GetLong(vid, "bandwidth");
            if (bw > bestBw)
            {
                bestBw = bw;
                best = url.StartsWith("//") ? "https:" + url : url;
            }
        }
        return best;
    }

    /// <summary>下载一个媒体地址到本地文件（自动带 UA/Referer：B站的音视频 CDN 会校验）。</summary>
    public async Task<bool> DownloadBinaryAsync(string url, string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return false;
        await using var fs = File.Create(filePath);
        await resp.Content.CopyToAsync(fs, ct);
        return true;
    }

    // ---------------- 扫码登录 ----------------

    /// <summary>申请登录二维码，返回 (qrcode_key, 待编码的登录 URL)。</summary>
    public async Task<(string Key, string Url)> QrGenerateAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);

        var parsed = ParseApiResponse(text, "qrcode/generate");
        if (parsed == null || parsed.Code != 0)
            return ("", "");
        string key = GetString(parsed.Data, "qrcode_key");
        string url = GetString(parsed.Data, "url");
        return (key, url);
    }

    /// <summary>轮询扫码状态；code=0 时把 Set-Cookie 里的凭证收下来。</summary>
    public async Task<QrPollResult> QrPollAsync(string qrcodeKey, CancellationToken ct = default)
    {
        string target = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key="
                        + Uri.EscapeDataString(qrcodeKey);
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", RefererUrl);
        using var resp = await http.SendAsync(req, ct);

        // 手动抓 Set-Cookie（CookieContainer 已自动收，这里双保险）
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (string header in setCookies)
                CaptureSetCookie(header);
        }

        string text = await resp.Content.ReadAsStringAsync(ct);
        var parsed = ParseApiResponse(text, "qrcode/poll");
        if (parsed == null)
            return new QrPollResult(-1, "轮询失败（网络异常）");

        int code = parsed.Data.ValueKind == JsonValueKind.Object && parsed.Data.TryGetProperty("code", out var c)
            ? c.GetInt32()
            : parsed.Code;

        if (code == 0)
            HarvestCredential();

        return new QrPollResult(code, parsed.Message);
    }

    void CaptureSetCookie(string header)
    {
        string pair = header.Split(';')[0];
        int eq = pair.IndexOf('=');
        if (eq <= 0)
            return;
        string name = pair.Substring(0, eq).Trim();
        string value = pair.Substring(eq + 1).Trim();
        SetCookie(name, value);
    }

    /// <summary>把 CookieContainer 里的登录 cookie 汇总成凭证对象。</summary>
    void HarvestCredential()
    {
        var c = new BiliCredential
        {
            SessData = GetCookie("SESSDATA") ?? "",
            BiliJct = GetCookie("bili_jct") ?? "",
            DedeUserId = GetCookie("DedeUserID") ?? "",
            DedeUserIdCkMd5 = GetCookie("DedeUserID__ckMd5") ?? "",
            Sid = GetCookie("sid") ?? "",
            FetchTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        long.TryParse(c.DedeUserId, out long mid);
        c.Mid = mid;
        Credential = c;
    }

    /// <summary>把已保存的凭证注入 CookieContainer。</summary>
    public void ApplyCredential(BiliCredential c)
    {
        Credential = c ?? new BiliCredential();
        if (!Credential.IsLoggedIn)
            return;
        SetCookie("SESSDATA", Credential.SessData);
        SetCookie("bili_jct", Credential.BiliJct);
        SetCookie("DedeUserID", Credential.DedeUserId);
        SetCookie("DedeUserID__ckMd5", Credential.DedeUserIdCkMd5);
        if (!string.IsNullOrEmpty(Credential.Sid))
            SetCookie("sid", Credential.Sid);
    }

    /// <summary>登录后拉一次 nav 补上用户名。</summary>
    public async Task<string> FetchUserNameAsync(CancellationToken ct = default)
    {
        var nav = await GetApiAsync("https://api.bilibili.com/x/web-interface/nav", null, false, ct);
        if (nav == null || nav.Code != 0)
            return "";
        string name = GetString(nav.Data, "uname");
        Credential.UserName = name;
        return name;
    }

    public void Logout()
    {
        Credential = new BiliCredential();
        foreach (string name in new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid" })
            cookies.Add(new Cookie(name, "", "/", ".bilibili.com"));
    }

    // ---------------- 小工具 ----------------

    static string GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static long GetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long l) ? l : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out long l2) ? l2 : 0,
            _ => 0
        };
    }

    /// <summary>搜索结果里 title/description 含 &lt;em class="keyword"&gt; 高亮标签，需剥掉。</summary>
    static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0)
            return input ?? "";
        var sb = new StringBuilder(input.Length);
        bool inTag = false;
        foreach (char ch in input)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return WebUtility.HtmlDecode(sb.ToString());
    }

    /// <summary>搜索结果的 duration 形如 "58:6" 或 "12:34:56"，转为秒。</summary>
    static long ParseDurationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var parts = text.Split(':');
        long total = 0;
        foreach (var p in parts)
        {
            if (!long.TryParse(p, out long v))
                return 0;
            total = total * 60 + v;
        }
        return total;
    }

    public void Dispose() => http.Dispose();
}
