# 刷B站（presents.BiliScroll）

让角色陪你刷 B 站：抓视频关键帧看画面，同时把整片音频转成文字当台词素材，
再按节奏开口。没有视觉模型也能靠本机 OCR 读 PPT/图表上的原文。

## 能力分工（听觉 / 视觉 / OCR）

| 谁 | 负责什么 | 说明 |
|---|---|---|
| 本插件 | 刷视频的节奏、抓帧、把素材组织进模型输入 | |
| **SenseVoice（框架听觉）** | **整片音频转写** | 取代原来的 whisper tiny / `-l zh` 硬编码；流式识别，内置 VAD 自动跳 BGM |
| 视觉模型 | 画面版式/图形/场景理解 | 配了就走它，更懂画面 |
| **presents.Shared.Media（OCR）** | 关键帧画面文字原文 | **Windows 自带引擎，零依赖、不出网**；无视觉模型也能读 PPT 原文，有模型时可附原文（可关） |
| whisper | 降级备胎 | provider 为空 / 解码异常时自动回退，不会哑掉 |

依赖：`presents.Shared.Media`（`MediaOcr.ReadAsync`）。SenseVoice 由框架提供，本插件只注入 provider。

## 音频转写：SenseVoice（v5.6.3）

**之前**：whisper tiny，命令里 `-l zh` 硬编码。
**现在**：`Transcriber` 构造注入 provider，整片音频用 ffmpeg 解成 f32le → 流式喂识别器；
内置 VAD 会自动跳过 BGM 段。识别失败自动回退 whisper。

验证日志：

```
本地转写引擎：SenseVoice（框架）
```

看到这行就说明走的是框架 SenseVoice，不是 whisper。

### 前一阶段：whisper 路径的 VAD + 过滤（v5.6.1，源自陪看）

在接入 SenseVoice **之前**，先做过一轮来自 `presents.WatchTogether`（视频陪看）的经验同步——
陪看踩过的"whisper 对着 BGM 编字幕"的坑，B站这边更严重：陪看是 20 秒切片，
B站是**整片转写**（`MaxAudioMinutes` 默认 30），一支 10 分钟的 PPT+BGM 视频，
whisper 能编出 10 分钟假字幕全混进当轮总结。已做（5.6.1，重载成功）：

| 改动 | 位置 |
|---|---|
| `TranscribeVad` 配置（默认开） | `BiliScrollConfig` |
| whisper.cpp 命令加 `--vad -vm`，模型自动找（复用陪看下载的同一个 0.84 MB silero） | `RunWhisperCppAsync` |
| 整片文本逐行幻觉过滤 | `CleanTranscript` |

`CleanTranscript` 过滤的内容：

- `(upbeat music)` 这类场景标注
- `字幕:` 署名
- 订阅 / 点赞套话
- 重复拟声（`啊啊啊啊`、`♪♪♪` 等）
- 有效字符占比 < 55% 的行

**这条路径现在仍在**：SenseVoice 失败回退 whisper 时，走的还是这套加了 VAD + 过滤的 whisper 路径，
所以降级时也不会对着 BGM 空转。

## OCR：画面文字兜底

关键帧抓下来后走 `presents.Shared.Media` 的 OCR：

- 无视觉模型时 —— 读 PPT / 图表 / 代码 / 数字原文
- 有视觉模型时 —— 可附上 OCR 原文（可关）
- 零依赖、不出网：`Windows.Media.Ocr` 是系统组件，不装模型、不上传图片

## 歌词与音乐：表现与边界

 SenseVoice 对唱歌也会转，实际表现分场景：

| 场景 | 表现 |
|---|---|
|  music / 歌曲区 | **歌词就是干货** ✓ |
| 普通视频 | OP/ED 那 90 秒歌词会混进整片转写，但占比小 |

**两个已知边界（诚实说）：**

1. **歌词转写质量差**：whisper 对唱歌的准确率远低于说话（拖长音、气声、和声都糊），中文歌尤甚，转出来的歌词会有错字。
2. **罕见误杀**：歌词里恰好出现「字幕」「订阅」这类词且整行 ≤30 字时，会被幻觉过滤器丢掉（比如某歌词正好是"感谢订阅我们的频道"——概率极低）。

**已做的缓解（已编译重载）**：转写文本进提示词时，若标注来源是"本地转写"，会附一句：

```
（注意：这是 whisper 对音频的转写，可能混有 OP/ED 歌词、哼唱与 BGM 误转——
唱歌/音乐段落的文字可信度低于正常台词，别把它当剧情或知识点）
```

这样模型拿到歌词时会自己降权，而不是把"你说的每一句都是错"这类歌词当台词接。

## 版本

| 项 | 值 |
|---|---|
| 插件版本 | **5.6.3**（5.6.1 先加 whisper VAD+过滤，5.6.3 接 SenseVoice） |
| 语音引擎 | SenseVoice（框架），whisper（带 VAD+过滤）降级备胎 |
| OCR | presents.Shared.Media（Windows 自带） |
