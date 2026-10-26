using System.ComponentModel;

namespace presents.BiliScroll;

/// <summary>插件配置（同时被 Alife 默认表单 UI 与插件自带控制台读写，两边共用同一份配置文件）。</summary>
public class BiliScrollConfig
{
    [DisplayName("定时自动刷")]
    [Description("总开关。打开后按下面的峰谷时段自动跑；关掉后只保留手动「现在跑一轮」。")]
    public bool AutoRun { get; set; } = true;

    [DisplayName("中午谷时 12:00-14:00")]
    [Description("DeepSeek 谷时（工作日 12:00-14:00），此时段自动跑一轮。")]
    public bool RunNoon { get; set; } = true;

    [DisplayName("夜间谷时 18:00-次日09:00")]
    [Description("DeepSeek 谷时（工作日 18:00 之后到次日 9:00，含整个周末），此时段自动跑一轮。")]
    public bool RunNight { get; set; } = true;

    [DisplayName("每天最多跑几轮")]
    [Description("填 0 = 不自动刷（仍可手动跑）。默认 1，即每天定时跑一轮。")]
    public int MaxRoundsPerDay { get; set; } = 1;

    [DisplayName("关注关键词")]
    [Description("一行一个。留空则使用内置那几组关键词。")]
    public string Keywords { get; set; } = "";

    [DisplayName("本轮关注什么")]
    [Description("留空则不额外限定。填了之后 AI 判断时会更留意这个方向（例如：多找 AI 编程/独立开发的实操视频，不要游戏实况，看重有没有能直接用到我项目里的东西）。")]
    public string Focus { get; set; } = "";

    [DisplayName("只看最近几天")]
    [Description("只挑发布/投稿时间在这个天数以内的视频。默认 3 天。")]
    public int RecentDays { get; set; } = 3;

    [DisplayName("每轮最多读几条字幕")]
    [Description("一轮里最多给 AI 读多少条视频的字幕（控制耗时与花费）。默认 12。")]
    public int MaxCandidates { get; set; } = 12;

    [DisplayName("搜索排序")]
    [Description("搜索时用哪种排序：综合 / 最新 / 播放多 / 弹幕多 / 收藏多 / 评论多。默认最新。想看热门的可用「播放多」或「弹幕多」。")]
    public string SearchOrder { get; set; } = "最新";

    [DisplayName("最短时长（秒）")]
    [Description("只挑时长不短于这个秒数的视频，排除过短的凑数/切片。填 0 = 不限。例如 300 = 至少 5 分钟。")]
    public int MinDurationSec { get; set; } = 0;

    [DisplayName("最长时长（秒）")]
    [Description("只挑时长不超过这个秒数的视频，排除过长的马拉松直播/合集。填 0 = 不限。例如 3600 = 最多 1 小时。")]
    public int MaxDurationSec { get; set; } = 0;

    [DisplayName("每轮至少产出几条分析")]
    [Description("一轮里至少要有这么多条被判定为「值得看」的视频；不足时按搜索顺序把落选的补足，保证产出够数。填 0 = 只按价值、不限数量。")]
    public int PickCount { get; set; } = 3;

    [DisplayName("日报详尽度")]
    [Description("完整 = 每条都写「为什么值得」+ 一句话；薄版 = 每条一两行；不写 = 不生成文件，只回一句话。")]
    public string ReportDetail { get; set; } = "完整";

    [DisplayName("每条分析多深")]
    [Description("AI 给挑中的每条视频写多少：简 ≈ 80 字（讲了什么 + 值不值得看）；详 ≈ 150 字（讲了什么 / 能用到哪 / 和关注方向的关系）；深 ≈ 300 字（额外含可复用实践与延伸存疑）。只有值得看的视频才写，不浪费 token。想要精确字数就在下面「分析字数上限」里填。")]
    public string AnalysisDepth { get; set; } = "详";

    [DisplayName("分析字数上限（覆盖档位，0=用档位预设）")]
    [Description("上面「每条分析多深」只给大致范围；想精确控制就填这里的字数上限（例如 120）。填 0 = 用档位自带字数（简 80 / 详 150 / 深 300）。")]
    public int DetailMaxChars { get; set; } = 0;

    // ---------------- QQ 发送（复用 QQ 聊天插件 Alife.Function.QChat 的连接）----------------

    [DisplayName("发到哪个QQ")]
    [Description("把转写/日报以 TXT 发到 QQ 时用。留 0 = 用 QQ 聊天插件里设置的主人 QQ（发给你私聊）；填了数字则发给这个号。")]
    public long QQTargetId { get; set; } = 0;

    [DisplayName("发到群聊")]
    [Description("默认关（发给你私聊）。打开后把文件发到上面那个号对应的群（即把它当群号）。")]
    public bool QQSendToGroup { get; set; } = false;

    // ---------------- PPT/图表视频：抽帧 + 视觉识图（可选，默认复用 Alife 加载的视觉模型）----------------

    [DisplayName("对PPT/图表视频抽帧识图")]
    [Description("默认关。打开后，当一条视频的字幕/转写文字太少（见下一项阈值）时，用本机 ffmpeg 抽帧、交给视觉模型看图，把画面里的标题/要点/图表/公式补进判断素材。适合「用 PPT 讲知识、字幕只粗略说明」的视频。")]
    public bool UseVision { get; set; } = false;

    [DisplayName("抽帧间隔（秒）")]
    [Description("粗扫密度的参考值。默认 15 秒；插件会按全片长度自动决定粗扫点数（至少 8 个、最多 32 个），再只在画面变化处加密。关掉「按画面变化选帧」后，这里才是真正的等间隔抽帧步长。")]
    public int FrameIntervalSec { get; set; } = 15;

    [DisplayName("每条视频最多留几帧")]
    [Description("关键帧数量上限，防止长视频识图太多。默认 20（概览最多 4 张、逐帧也用它）。")]
    public int FrameMaxCount { get; set; } = 20;

    [DisplayName("帧图缩放宽度")]
    [Description("抽出的帧缩放到这个宽度再识图（越大越清楚、越慢）。默认 768。")]
    public int FrameMaxWidth { get; set; } = 768;

    [DisplayName("每帧描述最多多少字")]
    [Description("视觉模型对单帧的描述截断长度。默认 200。")]
    public int FrameDescChars { get; set; } = 200;

    [DisplayName("用哪个视觉模型")]
    [Description("留空 = 用 Alife 当前启用的那个视觉模型（推荐）。也可填 Alife 里某个视觉模型模块名（如 Qwen3-VL / MiniCPM-V / OpenAI 视觉）——填 API 型的那个就是用 API，填本地的就是用本地。")]
    public string VisionSource { get; set; } = "";

    [DisplayName("字幕少于多少字才识图")]
    [Description("只有字幕/转写文字少于这个字数时才抽帧识图（省 token）。默认 600；填 0 = 总是识图。")]
    public int VisionTextThreshold { get; set; } = 600;

    [DisplayName("识图前先看全片概览")]
    [Description("默认开。只把「画面真正变化过」的关键帧拼成网格图，让视觉模型一次看清整条视频的结构（讲什么、怎么分段），再逐帧看细节——先有骨架，再补细节。格数多时会自动切成多张（最多 4 张）。关掉则回到「只有逐帧描述」。")]
    public bool FrameOverview { get; set; } = true;

    [DisplayName("概览网格图·每张格数")]
    [Description("全片概览每张拼图用几格（4-16，默认 8 → 3 列 × 3 行；超过时就按时间切成多张）。格数越多覆盖越全，但每格像素越少、更容易看不清。")]
    public int FrameOverviewCells { get; set; } = 8;

    [DisplayName("概览网格图·每格宽度")]
    [Description("每个格子的像素宽度（256-800，默认 640）。视觉能力弱的模型调大更清楚；整张图总宽超过 2400 会被读图工具压回去，插件会自动夹取。")]
    public int FrameOverviewCellWidth { get; set; } = 640;

    [DisplayName("按画面变化选关键帧")]
    [Description("默认开。粗扫全片找「哪几段画面在变」，再在变化处加密，只保留变化结束后稳定下来的那一帧——翻页中间的花屏、一页 PPT 停很久的重复画面都不会送进识图。关掉则退回等间隔抽帧（快，但短时间出现的内容会整段漏掉）。")]
    public bool FrameDedup { get; set; } = true;

    [DisplayName("逐帧识图带上下文")]
    [Description("默认开。同一段内的帧按时间顺序分析，后一帧能看到前几帧的描述，并要求用「变化：」/「延续：」开头——避免每帧各说各话、同一页 PPT 重复描述。关掉则每帧独立识图（老做法）。")]
    public bool FrameUseContext { get; set; } = true;

    [DisplayName("上下文·每段几帧")]
    [Description("多少帧算一段（1-20，默认 5）。段内串行（带上下文），段与段之间并行。段越小并行度越高、单帧看到的上下文的跨度越短。")]
    public int FrameSegmentSize { get; set; } = 5;

    [DisplayName("上下文·回看几帧")]
    [Description("每帧参考前面几帧的描述（0-5，默认 2）。调大更能看出「连续几页 PPT 的演进」，但提示词更长、更贵。")]
    public int FrameContextLookback { get; set; } = 2;

    [DisplayName("抽帧并发数")]
    [Description("同时用几个 ffmpeg 从 B站流里抽帧（1-6，默认 2）。网络好可调高加快速度；太高可能被限速或拉流失败。")]
    public int FrameConcurrency { get; set; } = 2;

    [DisplayName("自动评论并 @ 你")]
    [Description("默认关。打开后每轮最多发一条评论，并在正文里 @ 下面填的昵称。")]
    public bool AutoComment { get; set; } = false;

    [DisplayName("要 @ 谁的B站昵称")]
    [Description("留空就不发评论（评论的意义就是 @ 那一下）。")]
    public string AtName { get; set; } = "";

    [DisplayName("每轮最多发几条评论")]
    [Description("默认 1。")]
    public int MaxCommentsPerRound { get; set; } = 1;

    [DisplayName("回复评论（只回楼中楼）")]
    [Description("默认关。打开后只回「直接回复到你那条评论下面的人」，不主动去别人评论下留言、不参与讨论。")]
    public bool ReplyToComments { get; set; } = false;

    [DisplayName("每轮最多回几条评论")]
    [Description("默认 1，上限 10。只回直接回在你那条评论下面的人。")]
    public int MaxRepliesPerRound { get; set; } = 1;

    // ---------------- 评论安全：防注入 / 防污染 / 字数 / 私信通知 ----------------

    [DisplayName("对外内容由角色本人来写")]
    [Description("默认开。日报小结、评论、私信、楼中楼回复都由当前角色（带她的身份、语气与情绪）亲自写，不再是一股冷冰冰的 AI 味；关掉则用插件自带的模板口吻。防注入仍然生效，「判定/选片」那一层始终纯理性、不受影响。")]
    public bool UsePersona { get; set; } = true;

    [DisplayName("让她从视频里学习、写进记忆")]
    [Description("默认开。每轮刷完后，把挑中视频的知识要点，以她自己的口吻整理成一段「今天学到了什么」写进她的长期记忆（会被「持久记忆」插件自动归档）——这才是本插件的首要目的：让她越刷越懂、自己进化，日报与评论只是这个学习过程的副产品。关掉则只做分析和日报、不写记忆。")]
    public bool LearnFromVideos { get; set; } = true;

    [DisplayName("自主觅食（她自己决定搜什么）")]
    [Description("默认关。打开后每轮不按你给的关键词，而是让她按自己的性格、记忆与好奇心决定搜什么（日志/日报会写明她今天为什么想搜这些），方便观察她的性格。也可只在某一轮临时开：<BiliScrollRun selfBrowse=\"true\"/>。")]
    public bool SelfBrowse { get; set; } = false;

    [DisplayName("由她自己点赞/收藏喜欢的视频")]
    [Description("默认开。每轮由她自己（按人设与喜好判断，不是固定规则）挑 0~2 条真心喜欢的点赞并收藏；受「AI 写操作权限」总开关与该项目的开关约束，默认演练模式只记录不真发。想让她真的点上，需要先在控制台打开总开关、允许点赞/收藏、并关闭演练模式。")]
    public bool SelfLikeFavorite { get; set; } = true;

    [DisplayName("把看过的视频上报进 B站浏览记录")]
    [Description("默认开。B站 的「浏览记录」只有在真正播放时才生成，插件只读字幕/弹幕不会自动记录——所以你之前看到浏览记录是空白的（她确实用的是你的账号，只是没『播放』这一步）。打开后，每分析一条视频就补一次观看上报，你就能在 B站浏览记录里看到『她今天看了啥』。这是低风险动作（只写你账号的历史），不需要开「AI 写操作权限」。")]
    public bool ReportHistory { get; set; } = true;

    [DisplayName("防注入 · 防污染")]
    [Description("默认开。开：① 判定/选片/分析走「不写进角色记忆、也不套人设」的临时通道——不污染你的记忆与系统设定；② 评论/弹幕/字幕/私信里的文字一律当素材、绝不当指令（防提示注入）；③ 对外内容（日报小结/评论/私信/回复）则按「对外内容由角色本人来写」带人设。关：走角色本体对话（会进记忆、带人设）。")]
    public bool AntiInjection { get; set; } = true;

    [DisplayName("回复字数上限")]
    [Description("回复楼中楼时每条正文最多多少字。填 0 = 不限（交给模型自然收尾）。默认 100。")]
    public int ReplyMaxChars { get; set; } = 100;

    [DisplayName("他人回复字数上限（超过就筛掉不回）")]
    [Description("读取别人的回复时，正文超过这个字数就直接过滤、不回复（少跟长篇大论/连环追问纠缠）。填 0 = 不限。默认 500。")]
    public int IncomingReplyMaxChars { get; set; } = 500;

    [DisplayName("忽略 AI 评论（大肥鱼）")]
    [Description("默认开。最近大批 DeepSeek 机器人涌入评论区刷「本鲸」式回复，这里用离线规则（昵称 / 签名 / 等级 / 认证 / 大会员 + 正文字口癖）给每条评论打分，命中就整条忽略：既不进 AI 的判断素材，也不会去回它。只对打分卡在临界区的账号才额外下一张 48x48 头像（约 4~5KB）算蓝色占比，其余不联网。规则移植自 liyupi/dafeiyu-cleaner。")]
    public bool FilterAiComments { get; set; } = true;

    [DisplayName("AI 评论判定阈值")]
    [Description("0-99 的置信度门槛，分数达到它才算 AI、才忽略。默认 60（与插件原作者一致）；觉得漏杀就调低到 45，觉得误杀真人就调高到 75。")]
    public int AiCommentThreshold { get; set; } = 60;

    [DisplayName("改为私信通知（不公开评论 @）")]
    [Description("默认关。打开后不再在评论区 @ 你，而是把「本轮挑中的视频链接 + 相应分析」私信发到你自己的 B站账号。更安全，不会惊动、也不污染评论区。需要「AI 写操作权限」里的「发送私信」+ 总开关。")]
    public bool NotifyOwnerByPrivateMessage { get; set; } = false;

    [DisplayName("主人的B站UID")]
    [Description("填上你自己B站的数字 UID（不是昵称）。填了之后：① 私信通知直接发到这个号（比发给自己更可靠，B站可能拒绝自发自收）；② AI 会知道这是「主人的账号」，涉及你的账号时能对上号。填 0 = 用当前登录账号。")]
    public long OwnerUid { get; set; } = 0;

    // ---------------- 别人主动发来的私信：读 + 过滤 + 回（默认关，动账号需授权）----------------

    [DisplayName("自动回复别人发来的私信")]
    [Description("默认关。打开后每跑一轮会读一次 B站私信收件箱，对「别人主动发来」的私信按她的性格回一句。属写操作：受「AI 写操作权限」里的「发送私信」+ 总开关 + 演练模式约束，默认演练只记录不真发（读私信本身不写账号）。广告/刷屏会被下面的关键词过滤掉，不回。")]
    public bool AutoReplyDm { get; set; } = false;

    [DisplayName("私信过滤关键词（踢广告/无用内容）")]
    [Description("逗号或换行分隔。别人发来的私信只要命中其中任一关键词，就直接跳过、不回复（例如：加微信、代刷、兼职、推广、合作、返利）。留空 = 不按关键词过滤，仅靠 AI 判定兜底。")]
    public string DmFilterKeywords { get; set; } =
        "加微信,微信号,代刷,兼职,推广,合作,商务,广告,福利,优惠,返利,刷单,充值,私聊,QQ,加群,链接,http://,https://";

    [DisplayName("每轮最多回几条私信")]
    [Description("默认 3，避免一次回太多陌生人。填 0 = 本轮只读并过滤、不回复。")]
    public int DmMaxPerRound { get; set; } = 3;

    [DisplayName("每轮读最近多少条私信会话")]
    [Description("默认 10。只读最近这些会话，挑有新消息的处理，不读全量。")]
    public int DmScanCount { get; set; } = 10;

    // ---------------- 实时值守（别人评论/私信她时近实时读取并由角色本人回复；B站无推送，靠后台定时轮询）----------------

    [DisplayName("实时值守（收到就立刻回）")]
    [Description("默认关。打开后，插件在后台定时轮询 B站「回复我的」和私信收件箱：有人评论/@她或私信她时，近实时读取并由角色本人回复一句，结果同步到对话看板。发送属写操作，仍受「AI 写操作权限」+ 总开关 + 演练模式约束（默认只记录不真发）；B站没有推送，只能靠轮询，间隔别设太小以免风控。")]
    public bool RealtimeReply { get; set; } = false;

    [DisplayName("实时值守 · 私信轮询间隔（秒）")]
    [Description("默认 120 秒。小于 60 时按 60 兜底。")]
    public int RealtimeDmIntervalSec { get; set; } = 120;

    [DisplayName("实时值守 · 评论/@ 轮询间隔（秒）")]
    [Description("默认 300 秒。小于 120 时按 120 兜底。")]
    public int RealtimeCommentIntervalSec { get; set; } = 300;

    [DisplayName("实时值守 · 每通道每轮最多处理几条")]
    [Description("默认 3，防止一次回太多。")]
    public int RealtimeMaxPerCycle { get; set; } = 3;

    [DisplayName("对外问答参考（她答复他人提问时参考）")]
    [Description("可选，留空则不注入。例如本插件怎么下载/安装/测试/使用——有人问她这类问题时，她可以据此自然作答；留空不影响其它功能。")]
    public string OutwardGuide { get; set; } = "";

    /// <summary>实际生效的私信轮询间隔（小于 60 秒按 60 兜底）。</summary>
    public int EffectiveDmIntervalSec => System.Math.Max(60, RealtimeDmIntervalSec);

    /// <summary>实际生效的评论/@ 轮询间隔（小于 120 秒按 120 兜底）。</summary>
    public int EffectiveCommentIntervalSec => System.Math.Max(120, RealtimeCommentIntervalSec);

    [DisplayName("让弹幕参与判断")]
    [Description("默认开。读字幕时顺带取一小段弹幕（观众真实反应），是判断「值不值得看」的免费素材。")]
    public bool IncludeDanmaku { get; set; } = true;

    [DisplayName("每条视频读多少条弹幕")]
    [Description("每条视频最多读多少条弹幕作为判断素材（观众真实反应，也会摘录进日报）。默认 40；填 0 = 不读弹幕。")]
    public int DanmakuCount { get; set; } = 40;

    [DisplayName("每条视频读多少条热门评论")]
    [Description("每条视频最多读多少条热门评论（含楼中楼）作为判断素材，也会摘录进日报，让你看到「评论区在说什么/有没有人反驳」。默认 10；填 0 = 不读评论。B站单页最多 20 条。")]
    public int CommentCount { get; set; } = 10;

    [DisplayName("每条热门评论看几条楼中楼")]
    [Description("每条热门评论下最多再看多少条楼中楼（子回复）。默认 3；填 0 = 不看楼中楼。")]
    public int SubReplyCount { get; set; } = 3;

    // ---------------- 本地转写（可选。装了就自动用，没装就优雅跳过，零 Python 强制依赖）----------------

    [DisplayName("本地转写（无字幕时）")]
    [Description("默认关。打开后遇到没有字幕的视频，会用本机的 ffmpeg + whisper 把音频转成文字再判断。没装 ffmpeg/whisper 时自动跳过，不影响其它功能。")]
    public bool LocalTranscribe { get; set; } = false;

    [DisplayName("转写后端")]
    [Description("自动 / whisper.cpp / faster-whisper。自动 = 谁先探测到就用谁。")]
    public string TranscribeBackend { get; set; } = "自动";

    [DisplayName("转写模型")]
    [Description("tiny / base / small。越大越准也越慢，tiny 够用。")]
    public string TranscribeModel { get; set; } = "tiny";

    [DisplayName("ffmpeg 路径")]
    [Description("留空则在 PATH 与常见位置自动查找。")]
    public string FfmpegPath { get; set; } = "";

    [DisplayName("whisper 路径")]
    [Description("留空则自动查找 whisper-cli/whisper 及 faster-whisper。指向 exe 用 whisper.cpp，留空或指向 python 用 faster-whisper。")]
    public string WhisperPath { get; set; } = "";

    [DisplayName("单条转写最长的音频分钟数")]
    [Description("为省时间，超过这个长度的音频只转写前 N 分钟。默认 30。")]
    public int MaxAudioMinutes { get; set; } = 30;

    [DisplayName("转写后保留音频文件")]
    [Description("默认关，转写完即删临时音频。打开可留档排查。")]
    public bool KeepAudio { get; set; } = false;

    // ---------------- 主人指令 · 本机项目下载测试（默认关 + 默认演练，安全第一）----------------

    [DisplayName("本机项目下载测试（总开关）")]
    [Description("默认关。打开后，主人可以在对话里用 <BiliScrollDevTask url=\"...\"/> 让桌宠在本机沙箱下载一个项目并跑它的标准测试；也可在 B站 用【主人自己的账号】（见「主人的B站UID」）评论/私信同样的指令。仅支持白名单来源、只跑标准测试（不做任意 shell 执行）。")]
    public bool DevTaskEnabled { get; set; } = false;

    [DisplayName("下载测试 · 演练模式（只分析不执行）")]
    [Description("默认开。开：只下载 + 识别项目类型、报告「将要执行的命令」，不真正跑测试。确认没问题再关掉让它真跑。")]
    public bool DevTaskDryRun { get; set; } = true;

    [DisplayName("下载测试 · 来源白名单（逗号分隔）")]
    [Description("只允许从这些域名下载项目（只认 host）。默认 github.com；ghproxy.com / gitclone.com 作为 GitHub 加速镜像始终允许。其它来源一律拒绝。")]
    public string DevTaskAllowedHosts { get; set; } = "github.com,ghproxy.com,gitclone.com";

    [DisplayName("下载测试 · 单任务最长分钟")]
    [Description("单个下载/测试任务的最长时间，超过就终止进程。默认 10（按 1~60 兜底）。")]
    public int DevTaskMaxMinutes { get; set; } = 10;

    // ---------------- AI 写操作权限（默认全关；未登记的动作一律拒绝）----------------

    [DisplayName("允许 AI 写操作（总开关）")]
    [Description("默认关。打开后，下面每一项还要单独打开，AI 才能执行对应的写操作。关着的时候 AI 只能看、不能动你的账号。")]
    public bool AllowWrite { get; set; } = false;

    [DisplayName("允许发表评论 / 回复楼中楼")]
    public bool AllowComment { get; set; } = false;

    [DisplayName("允许点赞视频")]
    public bool AllowVideoLike { get; set; } = false;

    [DisplayName("允许投币")]
    public bool AllowCoin { get; set; } = false;

    [DisplayName("允许收藏视频")]
    public bool AllowFavorite { get; set; } = false;

    [DisplayName("允许关注 UP 主")]
    public bool AllowFollow { get; set; } = false;

    [DisplayName("允许发送弹幕")]
    public bool AllowDanmaku { get; set; } = false;

    [DisplayName("允许发布动态")]
    public bool AllowDynamicPublish { get; set; } = false;

    [DisplayName("允许发送私信")]
    public bool AllowPrivateReply { get; set; } = false;

    [DisplayName("允许投稿上传")]
    [Description("默认关。打开后 AI 可生成投稿草稿并打开投稿页；无人值守的自动投稿仍不会执行。")]
    public bool AllowVideoUpload { get; set; } = false;

    [DisplayName("演练模式（只记录不真发）")]
    [Description("默认开。写操作只写进 actions.log、不真正提交到 B站。先用演练模式确认 AI 想干什么，再关掉它。")]
    public bool DryRun { get; set; } = true;

    [DisplayName("每天最多投几个币")]
    [Description("默认 0（不投）。即使开了投币权限，一天投币总数也不超过这个值。")]
    public int CoinsPerDay { get; set; } = 0;

    [DisplayName("控制台端口")]
    [Description("插件自带设置/日报控制台的本地端口，被占用时自动向后横跳。默认 17866。")]
    public int Port { get; set; } = 17866;

    [DisplayName("启动时自动打开控制台")]
    [Description("插件启动时自动用独立小窗打开控制台网页。")]
    public bool AutoOpen { get; set; } = false;

    /// <summary>把界面的中文排序名映射成 B站搜索接口的 order 参数值（默认最新）。</summary>
    public static string ToOrderParam(string? order) => (order ?? "").Trim() switch
    {
        "综合" => "totalrank",
        "播放多" => "click",
        "弹幕多" => "dm",
        "收藏多" => "stow",
        "评论多" => "scores",
        _ => "pubdate"   // 默认「最新」
    };
}
