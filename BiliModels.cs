using System;
using System.Collections.Generic;

namespace presents.BiliScroll;

/// <summary>B站登录凭证（扫码登录成功后落盘在插件目录 credentials.json，不上传任何地方）。</summary>
public class BiliCredential
{
    public string SessData { get; set; } = "";
    public string BiliJct { get; set; } = "";
    public string DedeUserId { get; set; } = "";
    public string DedeUserIdCkMd5 { get; set; } = "";
    public string Sid { get; set; } = "";
    public string UserName { get; set; } = "";
    public long Mid { get; set; }
    public long FetchTime { get; set; }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(SessData) && !string.IsNullOrWhiteSpace(BiliJct);

    /// <summary>拼成本插件 HTTP 客户端使用的 Cookie 串。</summary>
    public string ToCookieString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(SessData)) parts.Add("SESSDATA=" + SessData);
        if (!string.IsNullOrEmpty(BiliJct)) parts.Add("bili_jct=" + BiliJct);
        if (!string.IsNullOrEmpty(DedeUserId)) parts.Add("DedeUserID=" + DedeUserId);
        if (!string.IsNullOrEmpty(DedeUserIdCkMd5)) parts.Add("DedeUserID__ckMd5=" + DedeUserIdCkMd5);
        if (!string.IsNullOrEmpty(Sid)) parts.Add("sid=" + Sid);
        return string.Join("; ", parts);
    }
}

/// <summary>搜索命中的一条视频。</summary>
public class VideoCandidate
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long Cid { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public long AuthorMid { get; set; }
    public long DurationSec { get; set; }
    public long PubDate { get; set; }
    public string Description { get; set; } = "";
    public long Play { get; set; }
    public long Like { get; set; }
    public long Danmaku { get; set; }
    public long Favorite { get; set; }
    public long Coin { get; set; }
    public long Reply { get; set; }
    public string Keyword { get; set; } = "";

    public DateTime PubTime => DateTimeOffset.FromUnixTimeSeconds(PubDate).LocalDateTime;
    public string Url => "https://www.bilibili.com/video/" + Bvid;
    public string DurationText => TimeSpan.FromSeconds(DurationSec).ToString(DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

/// <summary>一个分P（多P视频的其中一集）。</summary>
public class VideoPage
{
    public int Page { get; set; }
    public long Cid { get; set; }
    public string Part { get; set; } = "";
    public long DurationSec { get; set; }
    public string DurationText => TimeSpan.FromSeconds(DurationSec).ToString(DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

/// <summary>视频详情回填所需的字段。</summary>
public class VideoDetail
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long Cid { get; set; }
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Author { get; set; } = "";
    public long AuthorMid { get; set; }
    public long DurationSec { get; set; }
    public long PubDate { get; set; }
    // 完整 stat
    public long View { get; set; }
    public long Danmaku { get; set; }
    public long Reply { get; set; }
    public long Favorite { get; set; }
    public long Coin { get; set; }
    public long Share { get; set; }
    public long Like { get; set; }
    public List<VideoPage> Pages { get; set; } = new();

    public bool IsMultiPart => Pages.Count > 1;
}

/// <summary>一条字幕轨。</summary>
public class SubtitleTrack
{
    public string Lan { get; set; } = "";
    public string LanDoc { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsAi { get; set; }
}

/// <summary>一条弹幕。</summary>
public class DanmakuItem
{
    /// <summary>出现时间（秒）。</summary>
    public double Time { get; set; }
    /// <summary>弹幕模式（1/2/3 滚动，4 底端，5 顶端）。</summary>
    public int Mode { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>评论区里的一条评论（含楼中楼）。</summary>
public class CommentItem
{
    public long Rpid { get; set; }
    public long Mid { get; set; }
    public string UserName { get; set; } = "";
    public string Message { get; set; } = "";
    public long Ctime { get; set; }
    public long Like { get; set; }
    public bool IsUp { get; set; }
    public List<CommentItem> Replies { get; set; } = new();

    /// <summary>个性签名，识别「AI 评论」时的重要特征。</summary>
    public string Sign { get; set; } = "";
    /// <summary>账号等级（Lv0-6）。</summary>
    public int Level { get; set; }
    /// <summary>官方认证类型：0/1 为个人或机构认证，-1 表示无。</summary>
    public int Verify { get; set; } = -1;
    /// <summary>大会员状态：大于 0 表示已开通。</summary>
    public int Vip { get; set; }
    /// <summary>头像原始 URL（识别「AI 评论」时用来算蓝色占比）。</summary>
    public string Avatar { get; set; } = "";
    /// <summary>头像里「鲸鱼蓝」像素的占比 0~1；-1 表示没取到/没看过。</summary>
    public double AvatarBlue { get; set; } = -1;
    /// <summary>「忽略 AI 评论」给出的置信度 0~99，未判定时为 0。</summary>
    public int AiScore { get; set; }
    /// <summary>是否已被判定为 AI 机器人评论（命中阈值才会置位）。</summary>
    public bool IsAi { get; set; }
}

/// <summary>AI 对一条视频的判定。</summary>
public class VideoVerdict
{
    public string Bvid { get; set; } = "";
    /// <summary>视频标题（由 runner 从候选里回填，供对话看板/日报直接引用）。</summary>
    public string Title { get; set; } = "";
    /// <summary>视频链接（由 runner 回填）。</summary>
    public string Url { get; set; } = "";
    public bool Worth { get; set; }
    public string Reason { get; set; } = "";
    public string OneLine { get; set; } = "";
    /// <summary>深度分析（只有 worth=true 的视频才有）：讲了什么 / 能直接用到哪 / 与关注方向的关系。篇幅由「每条分析多深」配置决定。</summary>
    public string Detail { get; set; } = "";
    /// <summary>理性地多想一层：这条哪里可能不对 / 有偏见 / 存疑（只有 worth=true 的视频才有）。视频讲的不一定对，要理性地质疑。</summary>
    public string Critique { get; set; } = "";
}

/// <summary>我在某个视频下发过的一条评论（判重表用：有了 rpid 下一轮能直接定位，不用在几百条里翻）。</summary>
public class MyCommentRecord
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long Rpid { get; set; }
    /// <summary>若这条是楼中楼回复，记它回复的那条根评论 rpid；复核可见性时要用。</summary>
    public long RootRpid { get; set; }
    public string Text { get; set; } = "";
    public string Time { get; set; } = "";
    public long Ctime { get; set; }
}

/// <summary>一轮刷的结果汇总。</summary>
public class RoundResult
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Trigger { get; set; } = "手动";
    public int ScannedVideos { get; set; }
    public int ReadSubtitles { get; set; }
    public int SkippedNoSubtitle { get; set; }
    public int Picked { get; set; }
    public bool CommentPosted { get; set; }
    public string CommentText { get; set; } = "";
    /// <summary>角色本人带情绪写的本轮二次总结（给对话看板与日报开头用；原始逐条分析仍原样保留）。</summary>
    public string Summary { get; set; } = "";
    public int RepliesPosted { get; set; }
    public string ReportPath { get; set; } = "";
    public string Error { get; set; } = "";
    public List<VideoVerdict> Verdicts { get; set; } = new();
    /// <summary>本轮回复了几条别人发来的私信。</summary>
    public int DmReplied { get; set; }
    /// <summary>本轮按关键词/AI 判定过滤掉了几条私信（广告、机器人、无意义）。</summary>
    public int DmFiltered { get; set; }
}

/// <summary>一条私信会话（收件箱里的一个对话）。</summary>
public sealed class DmSession
{
    public long TalkerId { get; set; }
    public string TalkerName { get; set; } = "";
    public string LastText { get; set; } = "";
    public int Unread { get; set; }
    public long Ts { get; set; }
}

/// <summary>某个会话里的一条私信消息。</summary>
public sealed class DmMessage
{
    public long SenderUid { get; set; }
    public string Text { get; set; } = "";
    public long Ts { get; set; }
}

/// <summary>「回复我的」消息流里的一条（别人回复了我的评论 / @ 了我）。字段取不到的按空/0 容忍，不抛异常。</summary>
public sealed class FeedReplyItem
{
    /// <summary>对方这条回复的 rpid（判重用）。</summary>
    public long Rpid { get; set; }
    /// <summary>对方的 uid。</summary>
    public long Mid { get; set; }
    /// <summary>对方昵称（取不到则为空）。</summary>
    public string UserName { get; set; } = "";
    /// <summary>业务类型：1=视频评论，11=动态，12=专栏，17=音频…（取不到默认 0）。</summary>
    public long BusinessId { get; set; }
    /// <summary>被回复内容所在的对象 id（视频评论里就是 aid/oid）。</summary>
    public long Oid { get; set; }
    /// <summary>所属楼层的根评论 rpid（0 表示我那条评论本身就是根）。</summary>
    public long RootId { get; set; }
    /// <summary>被直接回复的那条评论 rpid（通常就是我发的那条）。</summary>
    public long SourceId { get; set; }
    /// <summary>所在视频/动态的标题（取不到则为空）。</summary>
    public string Title { get; set; } = "";
    /// <summary>原文链接/uri（取不到则为空；用于尽力解析 bvid）。</summary>
    public string Uri { get; set; } = "";
    /// <summary>从 uri 尽力解析出的 bvid（取不到则为空）。</summary>
    public string Bvid { get; set; } = "";
    /// <summary>对方回复的正文（他说的）。</summary>
    public string Message { get; set; } = "";
    /// <summary>被回复的原评论内容（我说的/上下文）。</summary>
    public string SourceMessage { get; set; } = "";
    /// <summary>是否 @ 我（而非回复评论）。</summary>
    public bool IsAt { get; set; }
    /// <summary>发生时间（unix 秒；取不到为 0）。</summary>
    public long Ts { get; set; }
}

/// <summary>复核一条自己评论后的可见性结论。</summary>
public class VerifyResult
{
    public string Bvid { get; set; } = "";
    public long Rpid { get; set; }
    public string Text { get; set; } = "";
    /// <summary>true=公开可见；false=没查到（可能仍在审核）。</summary>
    public bool Visible { get; set; }
}
