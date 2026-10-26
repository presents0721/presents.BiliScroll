using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace presents.BiliScroll;

/// <summary>
/// 写操作权限阀门（移植自 bilibili_learning_bot 的 action_permissions 思路）：
/// 任何会对账号产生副作用的动作，都必须先登记在下表里；未登记的写操作默认拒绝。
/// 这是一个「白名单 + 默认拒绝」的双保险 —— 即使以后新增了写接口忘记登记，也不会被 AI 顺手调用。
/// </summary>
public static class ActionGuard
{
    /// <param name="Key">内部键（与 B站接口路径映射，日志与落盘用）</param>
    /// <param name="Name">给用户看的中文名</param>
    /// <param name="Scope">platform=会动账号；local=只动本机文件</param>
    /// <param name="DefaultOn">登记表默认值（实际是否放行以配置为准）</param>
    public sealed record ActionDef(string Key, string Name, string Scope, bool DefaultOn);

    /// <summary>登记在册的全部写/敏感动作。</summary>
    public static readonly IReadOnlyList<ActionDef> Actions = new List<ActionDef>
    {
        new("video_like",       "点赞视频",           "platform", false),
        new("coin",             "投币",               "platform", false),
        new("favorite",         "收藏视频",           "platform", false),
        new("follow_up",        "关注 UP 主",         "platform", false),
        new("send_danmaku",     "发送弹幕",           "platform", false),
        new("dynamic_publish",  "发布动态",           "platform", false),
        new("private_reply",    "发送私信",           "platform", false),
        new("public_comment",   "发表评论",           "platform", false),
        new("reply_comment",    "回复评论（楼中楼）", "platform", false),
        new("video_upload",     "投稿上传",           "platform", false),
        new("local_transcribe", "本地音频转写",       "local",    false),
    };

    public static ActionDef? Find(string key)
        => Actions.FirstOrDefault(a => a.Key == key);

    /// <summary>生成当前权限状态清单（控制台「AI 写操作权限」网格用）。</summary>
    public static List<ActionStatus> Status(BiliScrollConfig? cfg)
        => Actions.Select(a => new ActionStatus(a.Key, a.Name, a.Scope, Allowed(a.Key, cfg))).ToList();

    /// <summary>按配置解析某个动作当前是否被授权。总开关 AllowWrite 关掉时，所有 platform 动作一律拒绝。</summary>
    public static bool Allowed(string key, BiliScrollConfig? cfg)
    {
        if (cfg == null)
            return false;
        ActionDef? def = Find(key);
        if (def == null)
            return false;                                  // 未登记的写操作默认拒绝
        if (def.Scope == "platform" && !cfg.AllowWrite)
            return false;                                  // 总开关未开，账号类写操作全部拒绝

        return key switch
        {
            "video_like" => cfg.AllowVideoLike,
            "coin" => cfg.AllowCoin,
            "favorite" => cfg.AllowFavorite,
            "follow_up" => cfg.AllowFollow,
            "send_danmaku" => cfg.AllowDanmaku,
            "dynamic_publish" => cfg.AllowDynamicPublish,
            "private_reply" => cfg.AllowPrivateReply,
            "public_comment" => cfg.AllowComment,
            "reply_comment" => cfg.AllowComment,
            "video_upload" => cfg.AllowVideoUpload,
            "local_transcribe" => cfg.LocalTranscribe,
            _ => false
        };
    }
}

/// <summary>一个写动作的当前状态（给控制台渲染权限网格）。</summary>
public sealed record ActionStatus(string Key, string Name, string Scope, bool Allowed);

/// <summary>一次写操作的结果：Ok=是否受理；DryRun=是否只是演练（没真发）；Message=给人看的话。</summary>
public sealed record WriteOutcome(bool Ok, bool DryRun, string Message)
{
    public static WriteOutcome Deny(string message) => new(false, false, message);
    public static WriteOutcome Dry(string message) => new(true, true, message);
    public static WriteOutcome Done(string message) => new(true, false, message);
}

/// <summary>
/// 每日投币预算（移植自 BiliLearn 的 coin_budget）：落盘 date/spent，跨天自动清零。
/// 即使开了投币权限，一天投出的硬币总数也不超过配置上限。
/// </summary>
public sealed class CoinBudget
{
    public string Date { get; set; } = "";
    public int Spent { get; set; }

    string path = "";

    public void Attach(string filePath) => path = filePath;

    void Load()
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        try
        {
            var loaded = JsonSerializer.Deserialize<CoinBudget>(File.ReadAllText(path));
            if (loaded != null)
            {
                Date = loaded.Date;
                Spent = loaded.Spent;
            }
        }
        catch
        {
            // 预算文件损坏就当没花过，不阻断流程
        }
    }

    void Save()
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    /// <summary>今天已花几个币（跨天自动归零）。</summary>
    public int SpentToday()
    {
        Load();
        return Date == DateTime.Now.ToString("yyyy-MM-dd") ? Spent : 0;
    }

    /// <summary>尝试占用 amount 个硬币额度；额度够才成功并持久化。</summary>
    public bool TryConsume(int maxPerDay, int amount)
    {
        if (amount <= 0 || maxPerDay <= 0)
            return false;
        Load();
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Date != today)
        {
            Date = today;
            Spent = 0;
        }
        if (Spent + amount > maxPerDay)
            return false;
        Spent += amount;
        Save();
        return true;
    }
}
