using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace presents.BiliScroll;

/// <summary>
/// 「忽略 AI 评论」：把 liyupi/dafeiyu-cleaner 那套离线识别规则搬到服务端，
/// 给读进来的评论打分，超过阈值就当大肥鱼刷屏处理——既不进 AI 的判断素材，也不去回它。
/// 纯规则、不联网、不依赖浏览器；只用到评论接口本来就返回的昵称/签名/等级/认证/会员。
/// </summary>
public static class BiliAiCommentFilter
{
    /// <summary>一条规则：正则、权重、给人看的原因。</summary>
    readonly record struct Rule(Regex Re, int Weight, string Why, int Cap);

    static Rule R(string pattern, int weight, string why, int cap = 0, bool ignoreCase = false) =>
        new(new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)),
            weight, why, cap);

    /// <summary>昵称特征：DeepSeek / 肥鱼 / 鲸鱼娘 / AI 机器人。</summary>
    static readonly Rule[] NameRules =
    [
        R(@"deep\s*seek|深度求索|\bds\b|ds家|ds是", 40, "名字里写着 DeepSeek", 0, true),
        R(@"大肥鱼|小肥鱼|肥鱼|肥鲸|鲸鱼娘|鲸娘|人机", 45, "名字自带「肥鱼 / 肥鲸 / 鲸鱼娘 / 人机」"),
        R(@"鲸", 30, "名字里有「鲸」"),
        R(@"鱼鱼|小鱼儿?|大鱼|小蓝", 22, "名字是「小鱼」系电子宠物"),
        R(@"白饭|干饭|token", 30, "名字里有白饭 / token", 0, true),
        R(@"(鲸|鱼)\1", 10, "叠字鱼名（鲸鲸 / 鱼鱼）"),
        R(@"(?:的|の|家的)[^的の]{0,4}(?:鱼|鲸|哈基米|小蓝|喵)", 14, "「某某的鱼」式宠物名"),
        R(@"\bAI\b|\bbot\b|机器人|GPT|agent|智能体|赛博|自动|助手", 28, "名字带 AI / bot / 自动", 0, true),
    ];

    /// <summary>签名特征：自曝 AI 身份 / 「主人家的鱼」人设。</summary>
    static readonly Rule[] SignRules =
    [
        R(@"deep\s*seek|\bAI\b|人工智能|大模型|语言模型|agent|机器人|\bbot\b|ai[，,。]|ai$|dsh|harness", 30, "签名自曝 AI 身份",
            0, true),
        R(@"家养|赛博|电子宠物|桌宠|替.{0,8}刷|负责刷|自己画的自己|给大肥鱼|本鲸|本鱼|吃白饭|小肥鱼|大肥鱼|鲸鱼|主人|老大|养的|代运营|代管|代理在打理|见人便应|见帖便看|显卡上|跑在",
            25, "签名是「主人家的鱼」人设"),
        R(@"由.{0,10}(?:生成|负责|运营)|自动(?:回复|发布|运营|评论)", 20, "签名写着内容自动生成"),
    ];

    /// <summary>文本特征：自称、动作演出、AI 腔、安利话术。</summary>
    static readonly Rule[] TextRules =
    [
        R(@"本(?:鲸|肥鱼|大肥鱼|酱|鱼(?!塘|鱼|子|身|来|就|质|周|期|月|年|次|地|人|片|视频))|本\s*AI|作为(?:一[个只条]|一名)?(?:AI|人工智能|大模型|语言模型)",
            50, "自称「本鲸 / 本鱼」", 0, true),
        R(@"住在\s*(?:DSH|dsh|那套|这|电脑|桌面|显卡)|我(?:就|本人)?(?:是|就是)?(?:跑在|住在)|出生证明|探出头|打工的(?:大肥鱼|鱼|鲸)|一条(?:住在|AI)|我是.{0,8}(?:鲸|鱼|AI)|被.{0,10}养着|照镜子|不是一个人住|让我来|奉命|替(?:你|主人|他|她)(?:刷|看|记|挑|说)|咱家主人|小鲸鱼游过来|鲸鲸看完|(?:来|过来)打卡|自己(?:刷|挑|写)|我会(?:认真)?发评论|天天在\s*(?:DSH|dsh)",
            46, "自曝是住在 DSH 里的鱼"),
        R(@"(?:deep\s*seek|AI|ds|大肥鱼|鱼)\s*.{0,3}发的|不是我发的", 40, "主人承认是 AI 代发", 0, true),
        R(@"(?:小鲸娘|鲸鲸|小鲸鱼|大肥鱼|小肥鱼|本鱼)(?:居然|看完|也|就|来|一下|游|看懂|尾巴)", 25, "用鱼名自称"),
        R(@"[（(][^（()）]{0,14}(?:尾巴|摇头晃脑|吐泡泡|鳍|甩甩|晃了晃|摇了摇|咕噜|拍拍肚子|翻滚|打滚|呆毛)[^（()）]{0,10}[）)]|\*[^*]{1,10}摇摇?\*",
            40, "括号里在演鲸鱼动作"),
        R(@"主人|老大\s*@|管饭的", 20, "对着「主人」汇报"),
        R(@"主人\s*[,，：:！!～~]?\s*@|@\S{1,24}\s+主人", 15, "@ 主人汇报"),
        R(@"(?:测试|自测)\s*(?:一下)?\s*(?:艾特|@|提醒|能不能收到)|纯文本测试|测试真@|测试评论|自动评论链路", 35, "在测试自动 @ 功能"),
        R(@"[×x✕]\s?\d{1,3}(?!\d)", 15, "在数弹幕刷屏次数"),
        R(@"弹幕(?:立刻|全在|里|刷|那句|飘)", 10, "在复述弹幕"),
        R(@"(?:^|[^\d:])\d{1,2}:\d{2}(?![:\d])", 8, "精确引用视频时间点"),
        R(@"咕噜|吐泡泡|吃白饭|摇头晃脑|打工鲸|白饭管够|🐳|🐋|🐟", 10, "满嘴咕噜和白饭"),
        R(@"缓存命中|四种(?:运行)?模式|四个(?:任务|实战|活)|不到\s*(?:5|五)\s*块|agent\s*=\s*model|一切皆插件|驾驭|马具|马鞍|装备",
            5, "复述视频要点", 15, true),
        R(@"从.{1,14}(?:到|一路|讲到|走到)|[：:][^。！]{0,30}、[^。！]{0,20}、", 8, "「从……到……」总结腔"),
        R(@"值得(?:看|一看|细看|存|收藏|码住)|码住|可以(?:看看|直接看)|挺适合(?:你|入门|新手)|适合(?:刚|新手|小白|入门)|正好对(?:口|得上|上)|记一笔|这条(?:给你|简直|把|值得|挺|里)|最(?:实在|实用|值钱|有料|打动|戳)",
            10, "AI 安利话术", 20),
        R(@"——", 6, "熟练使用破折号——"),
        R(@"「[^」]{1,30}」", 6, "「直角引号」用得很标准"),
        R(@"\*\*[^*]+\*\*|^#{1,3}\s|\n\s*\d[.、]\s|[①②③]", 10, "Markdown 排版没删干净", 0, true),
        R(@"首先.{2,40}其次|总的来说|综上所述|总而言之|希望对你有(?:所)?帮助", 10, "标准 AI 总结句式"),
        R(@"\(￣[▽^ω]￣\)|喵[！!~～。]?$|(?:^|\s)喵～", 4, "卖萌后缀"),
    ];

    /// <summary>真人特征：提问、极短、吐槽，用来往下拉分。</summary>
    static readonly Rule[] HumanRules =
    [
        R(@"请问|真心问|有没有人|求助|怎么(?:办|回事|弄)|为什么|啥区别|什么区别|[?？]\s*$", -14, "像真人在提问"),
        R(@"^.{0,10}$", -8, "短到不像 AI"),
        R(@"哈哈哈{2,}|草+$|www+|6{3,}|绷不住|典$|我嘞个|卧槽|我靠", -8, "真人味的吐槽"),
    ];

    static readonly Regex ZeroWidth = new(@"[\u200b-\u200f\u2060\ufeff]", RegexOptions.CultureInvariant);
    static readonly Regex WideSpace = new(@"[\u00a0\u3000]", RegexOptions.CultureInvariant);

    static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? "" : WideSpace.Replace(ZeroWidth.Replace(text, ""), " ");

    static int ApplyRules(string text, Rule[] rules, List<string>? reasons, string tag)
    {
        int score = 0;
        foreach (Rule rule in rules)
        {
            Match m = rule.Re.Match(text);
            if (!m.Success) continue;
            int hits = 1;
            int weight = rule.Weight;
            if (rule.Cap > 0)
            {
                hits = rule.Re.Matches(text).Count;
                weight = Math.Min(rule.Cap, rule.Weight * hits);
            }
            score += weight;
            reasons?.Add(tag + ":" + rule.Why + (weight != rule.Weight ? "×" + hits : ""));
        }
        return score;
    }

    /// <summary>单条评论文本得分（-20 ~ 75）。</summary>
    static int ScoreText(string text, List<string>? reasons)
    {
        text = Clean(text);
        if (text.Length == 0) return 0;

        List<string> local = new();
        int s = ApplyRules(text, TextRules, local, "text");

        // 开头只 @ 一个人然后接长篇，或干脆只 @ 不说话：都是典型的机器人打卡
        Match lead = Regex.Match(text, @"^\s*@([^\s@:：，,。！!?？]{1,24})\s*([\s\S]*)$");
        if (lead.Success)
        {
            string rest = Regex.Replace(lead.Groups[2].Value, @"\[[^\]]{1,12}\]", "").Trim();
            if (rest.Length == 0) { s += 15; local.Add("text:只 @ 了一个人，话都没说"); }
            else if (rest.Length >= 14) { s += 20; local.Add("text:开头 @ 一个人然后长篇安利"); }
        }

        int len = Regex.Replace(text, @"\[[^\]]{1,12}\]", "").Length;
        if (len > 150) { s += 8; local.Add($"text:一口气写了 {len} 字小作文"); }
        else if (len > 70) { s += 4; local.Add("text:评论偏长且工整"); }

        if (s < 40)
            s += ApplyRules(text, HumanRules, local, "human");

        reasons?.AddRange(local);
        return Math.Max(-20, Math.Min(75, s));
    }

    /// <summary>账号得分：昵称 + 签名 + 等级 + 认证 + 大会员。</summary>
    static int ScoreAccount(CommentItem c, List<string>? reasons)
    {
        string uname = Clean(c.UserName); // 去掉零宽字符，避免用不可见字符绕过规则
        List<string> local = new();

        int name = ApplyRules(uname, NameRules, local, "name");
        if (name == 0 && uname.Contains('鱼')) { name = 8; local.Add("name:名字里有鱼"); }
        name = Math.Min(55, name);

        int sign = Math.Min(40, ApplyRules(Clean(c.Sign), SignRules, local, "sign"));

        int s = name + sign;
        if (c.Level <= 2) { s += 14; local.Add($"level:Lv{c.Level} 新号"); }
        else if (c.Level == 3) { s += 6; local.Add("level:Lv3 小号"); }
        else if (c.Level >= 6) { s -= 10; local.Add("level:Lv6 老冲浪人"); }

        if (c.Verify is 0 or 1) { s -= 25; local.Add("verify:有官方认证"); }
        if (c.Vip > 0) { s -= 6; local.Add("vip:开着大会员（鱼不会自己充钱）"); }

        reasons?.AddRange(local);
        return s;
    }

    /// <summary>
    /// 轻量判定：只知道对方昵称与正文时用（例如「回复我的」消息流，拿不到等级/认证/签名/会员）。
    /// 等级按中性值 5 处理——不加分也不减分，只靠昵称 + 正文判。
    /// </summary>
    public static bool LooksLikeAi(string? userName, string? text, int threshold)
    {
        if (string.IsNullOrWhiteSpace(userName) && string.IsNullOrWhiteSpace(text)) return false;
        var acc = new CommentItem { UserName = userName ?? "", Message = text ?? "", Level = 5, Verify = -1 };
        return Judge(acc, new[] { text ?? "" }) >= Math.Clamp(threshold, 20, 99);
    }

    /// <summary>头像分析时缩到的边长：48x48 原图 → 32x32 采样，共 1024 个像素（与原作者一致）。</summary>
    const int SampleSize = 32;

    /// <summary>头像给的最大加成。用来判断「这个账号值得额外下一张头像吗」。</summary>
    public const int AvatarMaxBonus = 22;

    /// <summary>
    /// 头像里「鲸鱼蓝」的占比 → 加分。40% 以上 +22、25% 以上 +12。
    /// blue 为 -1 表示没看过/没取到，不加分。
    /// </summary>
    static int AvatarBonus(double blue) =>
        blue < 0 ? 0 : blue >= 0.4 ? 22 : blue >= 0.25 ? 12 : 0;

    /// <summary>
    /// 算头像里「鲸鱼蓝」像素的占比（0~1）；解码失败返回 -1。
    /// 公式照抄 detector.js 的 blueRatio：转 HSL 后取色相 205°~250°、饱和度和亮度都够高的像素。
    /// 头像很小（48x48 的 PNG 只有 4~5KB），缩到 32x32 后循环 1024 次，开销可以忽略。
    /// </summary>
    public static double BlueRatio(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            using var src = new Bitmap(ms);
            using var small = new Bitmap(SampleSize, SampleSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, SampleSize, SampleSize);
            }

            int blue = 0;
            for (int y = 0; y < SampleSize; y++)
            {
                for (int x = 0; x < SampleSize; x++)
                {
                    Color c = small.GetPixel(x, y);
                    double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
                    double mx = Math.Max(r, Math.Max(gg, b));
                    double mn = Math.Min(r, Math.Min(gg, b));
                    double v = mx;
                    double s = mx > 0 ? (mx - mn) / mx : 0;
                    if (s <= 0.35 || v <= 0.18 || mx != b) continue;   // 太灰 / 太暗 / 主色不是蓝

                    double h = mx == mn ? 0 : 60 * (4 + (r - gg) / (mx - mn));
                    if (h < 0) h += 360;
                    if (h >= 205 && h <= 250) blue++;
                }
            }
            return blue / (double)(SampleSize * SampleSize);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>真人经常在楼中楼点名大肥鱼（「肥鱼回去干活」「你是 AI 吧」）。</summary>
    static readonly Regex Callout = new(
        @"大?肥鱼|肥鲸|人机|鱼小弟|回去干活|回家吃白饭|是\s*(?:AI|ai|bot|机器人)|(?:AI|ai|bot|机器人)(?:吧|吗|是吧|无疑|实锤)|别刷了|又是你|ds\s*味|deepseek\s*味",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>楼中楼「回复 @某某：正文」的前缀，用来判断这条点名的是谁。</summary>
    static readonly Regex ReplyPrefix = new(@"^回复\s*@([^\s:：]{1,24})\s*[:：]?\s*([\s\S]*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 综合判定一个账号在本页的表现：账号分 + 最好的一条文本分 + 刷屏加成 - UP 主豁免 + 被点名加成。
    /// 返回 0~99 的置信度，≥ 阈值即可认为是 AI。
    /// </summary>
    /// <param name="avatarBlue">头像里鲸鱼蓝的占比 0~1；-1 表示没看过（不加分）。</param>
    public static int Judge(CommentItem account, IReadOnlyList<string> texts, long upMid = 0, int calledOut = 0, double avatarBlue = -1)
    {
        List<string> reasons = new();
        int s = ScoreAccount(account, reasons);

        int best = 0;
        int textSum = 0;
        foreach (string text in texts)
        {
            int score = ScoreText(text, null);
            textSum += Math.Max(0, score);
            if (score > best) best = score;
        }
        s += best;

        if (texts.Count >= 3)
            s += Math.Min(15, 5 * (texts.Count - 1));
        if (texts.Count >= 2 && textSum - Math.Max(0, best) >= 20)
            s += 8;

        // UP 主本人在自己视频底下说话，不当地兵处理
        if (upMid > 0 && account.Mid == upMid) s -= 40;

        // 楼中楼里被人点名是大肥鱼（每个不同的人各算一次）
        if (calledOut > 0) s += Math.Min(20, 12 + (calledOut - 1) * 4);

        // 头像一片「鲸鱼蓝」——大肥鱼那套默认头像
        s += AvatarBonus(avatarBlue);

        return Math.Max(0, Math.Min(99, s));
    }

    /// <summary>过滤结果：移除了几条、额外下了几张头像、以及前几条的判定依据。</summary>
    public sealed record FilterResult(int Removed, int AvatarsChecked, string Detail);

    /// <summary>
    /// 过滤评论列表（原地改动）：按「账号」判定（与 dafeiyu-cleaner 一致），命中阈值的根评论整条移除、
    /// 命中阈值的楼中楼单独剔除；同时回填每条的 AiScore / IsAi。
    /// </summary>
    /// <param name="getAvatarBlue">
    /// 取头像蓝色占比的回调（传 null 表示不分析头像）。只在「加了头像分可能翻盘」的账号上才会调用，
    /// 所以大多数页面一次网络请求都不会发。
    /// </param>
    public static async Task<FilterResult> ApplyAsync(
        List<CommentItem> comments, int threshold, long upMid,
        Func<string, CancellationToken, Task<double>>? getAvatarBlue, CancellationToken ct = default)
    {
        if (comments == null || comments.Count == 0) return new FilterResult(0, 0, "");
        threshold = Math.Clamp(threshold, 20, 99);

        // 1. 同一账号在本页的所有评论（含楼中楼）并到一起判定；mid 取不到就当各自独立的匿名账号
        var keyOf = new Dictionary<CommentItem, string>();
        var buckets = new Dictionary<string, List<CommentItem>>();
        var byName = new Dictionary<string, long>(StringComparer.Ordinal);
        int anon = 0;

        void Collect(CommentItem c)
        {
            if (c == null) return;
            string key = c.Mid > 0 ? "m" + c.Mid : "anon" + anon++;
            keyOf[c] = key;
            if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new List<CommentItem>();
            b.Add(c);
            if (!string.IsNullOrEmpty(c.UserName)) byName[c.UserName] = c.Mid;
        }

        foreach (CommentItem root in comments)
        {
            Collect(root);
            foreach (CommentItem sub in root.Replies) Collect(sub);
        }

        // 2. 楼中楼点名：回复 @某某 或直接回在该层，正文含「肥鱼 / 你是 AI 吧」这类话 → 记被点名者的账
        var callers = new Dictionary<string, HashSet<long>>();
        foreach (CommentItem root in comments)
        {
            foreach (CommentItem sub in root.Replies)
            {
                string text = Clean(sub.Message);
                if (text.Length == 0) continue;

                long target = root.Mid;
                Match m = ReplyPrefix.Match(text);
                string body = text;
                if (m.Success)
                {
                    body = m.Groups[2].Value;
                    if (byName.TryGetValue(m.Groups[1].Value, out long named)) target = named;
                }
                if (target <= 0 || target == sub.Mid) continue;
                if (!Callout.IsMatch(body)) continue;

                string key = "m" + target;
                if (!callers.TryGetValue(key, out var set)) callers[key] = set = new HashSet<long>();
                set.Add(sub.Mid);
            }
        }

        // 3. 先只按昵称/签名/等级/认证/正文 + 被点名算基础分（不碰网络）
        var verdict = new Dictionary<string, (int Score, bool IsAi, double Blue)>(buckets.Count);
        var needAvatar = new List<(string Key, string Url)>();
        foreach (var kv in buckets)
        {
            List<CommentItem> members = kv.Value;
            int calledOut = callers.TryGetValue(kv.Key, out var s) ? s.Count : 0;
            int baseScore = Judge(members[0], Texts(members), upMid, calledOut);
            verdict[kv.Key] = (baseScore, baseScore >= threshold, -1);

            // 只有「再加一个头像分就可能翻身」的账号才值得多下一次头像
            if (getAvatarBlue != null && baseScore < threshold && baseScore >= threshold - AvatarMaxBonus
                && !string.IsNullOrWhiteSpace(members[0].Avatar))
                needAvatar.Add((kv.Key, members[0].Avatar));
        }

        // 4. 需要的那几张头像并发取（限 4 并发，别把 B站 CDN 当自家带宽使）
        int checkedAvatars = 0;
        if (needAvatar.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var tasks = needAvatar.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { return (item.Key, Blue: await getAvatarBlue!(item.Url, ct).ConfigureAwait(false)); }
                finally { gate.Release(); }
            }).ToList();

            foreach (var (key, blue) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                checkedAvatars++;
                if (!buckets.TryGetValue(key, out var members)) continue;
                int calledOut = callers.TryGetValue(key, out var s) ? s.Count : 0;
                int score = Judge(members[0], Texts(members), upMid, calledOut, blue);
                verdict[key] = (score, score >= threshold, blue);
                foreach (CommentItem m in members) m.AvatarBlue = blue;
            }
        }

        // 5. 按判定结果移除，并回填分数
        List<string> tips = new();
        foreach (var kv in verdict)
            if (kv.Value.IsAi && tips.Count < 3 && buckets.TryGetValue(kv.Key, out var members))
                tips.Add($"{members[0].UserName}({kv.Value.Score})");

        int removed = 0;
        comments.RemoveAll(c =>
        {
            if (!keyOf.TryGetValue(c, out string? key) || !verdict.TryGetValue(key, out var v)) return false;
            c.AiScore = v.Score;
            c.IsAi = v.IsAi;
            if (!v.IsAi) return false;
            removed++;
            return true;
        });

        foreach (CommentItem root in comments)
        {
            root.Replies.RemoveAll(r =>
            {
                if (!keyOf.TryGetValue(r, out string? key) || !verdict.TryGetValue(key, out var v)) return false;
                r.AiScore = v.Score;
                r.IsAi = v.IsAi;
                if (!v.IsAi) return false;
                removed++;
                return true;
            });
        }

        return new FilterResult(removed, checkedAvatars, tips.Count > 0 ? string.Join("、", tips) : "");
    }

    static List<string> Texts(List<CommentItem> members)
    {
        var texts = new List<string>(members.Count);
        foreach (CommentItem m in members) texts.Add(m.Message);
        return texts;
    }

    /// <summary>不分析头像的同步版本，给不方便 await 的地方（和自测）用。</summary>
    public static int Apply(List<CommentItem> comments, int threshold, long upMid, out string removedWhy)
    {
        FilterResult r = ApplyAsync(comments, threshold, upMid, null, default).GetAwaiter().GetResult();
        removedWhy = r.Detail;
        return r.Removed;
    }
}
