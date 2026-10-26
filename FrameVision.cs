using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.Vision;
using Microsoft.Extensions.Logging;

namespace presents.BiliScroll;

/// <summary>
/// PPT / 图表类视频的「关键帧 + 视觉识图」补充通道：用本机 ffmpeg 沿时间轴看画面，
/// 只把「画面真的变了」之后的稳定帧交给视觉模型（默认复用 Alife 里已启用的那个视觉模型）。
///
/// 做法对齐 extract_keyframes 参考实现，但受限于 B站是远程流，不能整片解码：
///   1) 粗扫：按时间均匀取 8~32 个低分辨率样点，逐对比较相似度，找出「哪几段在变」；
///   2) 加密：只在变化段里补 1~3 帧，等相似度回到「没变」的那一帧——变化停下后的稳定画面
///      才是完整的一页 PPT / 一个完整镜头，而不是翻页中间的花屏；
///   3) 概览：只拼这些关键帧，每张最多 9 格（3×3），格多时按时间切成多张；
///   4) 逐帧带上下文：同一段内按时间顺序分析，后一帧能看到前几帧的描述；
///   5) 三路互校：标题、同片字幕摘录一起给模型对照，明确「画面与字幕冲突以画面为准、
///      看不清就说没看到」。
///
/// 任何一环缺失（没有 ffmpeg / 没有视觉模型）都优雅降级（返回空串），调用方照原样跳过。
/// </summary>
public sealed class FrameVision
{
    const string Referer = "https://www.bilibili.com/";
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    // 概览网格图的尺寸安全线：每格宽度可调，但总宽超过上限会被读图工具整体压回去，格子反而更糊
    const int DefaultOverviewCells = 8;
    const int MinOverviewCells = 4;
    const int MaxOverviewCells = 16;
    const int DefaultOverviewCellWidth = 640;
    const int MinCellWidth = 256;
    const int MaxCellWidth = 800;
    const int MaxSheetWidth = 2400;
    /// <summary>总览最多切几张（每张一次识图，张数越多越贵）</summary>
    const int MaxOverviewSheets = 4;

    // 粗扫 + 变化处加密（远程流友好：不下载整片，只抽有限个样点）
    const int CoarseWidth = 320;
    const double CoarseMinGapSec = 3.0;
    const int CoarseMaxSamples = 32;
    const int CoarseMinSamples = 8;
    const double ChangeNoiseK = 3.0;
    const double ChangeFloor = 0.004;
    const double ChangeCeil = 0.05;
    const int RefineMaxGaps = 6;
    const int RefineSamplesPerGap = 3;
    /// <summary>两帧 SSIM 高于这个值就算「画面没变」——加密时用它找变化结束的那一帧</summary>
    const double StableSsim = 0.985;
    const double MinKeyGapSec = 0.3;

    readonly ILogger logger;
    readonly Func<BiliScrollConfig> getConfig;
    readonly Transcriber transcriber;
    readonly IVisionModel? injected;      // 构造时注入的单个视觉模型（框架能稳定解析，作主路径）
    readonly IServiceProvider? services;  // 运行时重新解析（本地视觉模型注册往往晚于本模块构造）

    public FrameVision(
        ILogger logger,
        Func<BiliScrollConfig> getConfig,
        Transcriber transcriber,
        IVisionModel? visionModel,
        IServiceProvider? serviceProvider)
    {
        this.logger = logger;
        this.getConfig = getConfig;
        this.transcriber = transcriber;
        this.injected = visionModel;
        this.services = serviceProvider;
    }

    /// <summary>本次识图实际逐帧描述成功的帧数（供日志/看板显示）。</summary>
    public int LastFrameCount { get; private set; }

    /// <summary>本次识图用到的视觉模型名（供日志/看板显示）。</summary>
    public string LastModelName { get; private set; } = "";

    /// <summary>本次全片概览实际用了几格（0 = 没跑概览或失败）。</summary>
    public int LastOverviewCells { get; private set; }

    /// <summary>本次全片概览切成了几张（0 = 没跑概览或失败）。</summary>
    public int LastOverviewSheets { get; private set; }

    /// <summary>本次粗扫里「画面没变、被合并掉」的样点数。</summary>
    public int LastDedupDropped { get; private set; }

    /// <summary>当前会用的视觉模型名（未探测到返回空串）。</summary>
    public string ActiveModelName => ResolveModel() is { } m ? ModelName(m) : "";

    /// <summary>抽帧识图是否可用：本机有 ffmpeg 且能解析到视觉模型。</summary>
    public bool IsAvailable()
    {
        if (transcriber == null || !transcriber.Detect().FfmpegFound)
            return false;
        return ResolveModel() != null;
    }

    // ---------------- 视觉模型解析（照抄 presents.Video 的可用做法）----------------

    static string ModelName(IVisionModel m)
        => m.GetType().GetCustomAttribute<ModuleAttribute>()?.Name ?? m.GetType().Name;

    IEnumerable<IVisionModel> Models()
    {
        // 1) 运行时从容器重新解析（后注册的本地视觉模型也能识别）
        if (services != null)
        {
            try
            {
                if (services.GetService(typeof(IEnumerable<IVisionModel>)) is IEnumerable<IVisionModel> many
                    && many.Any())
                    return many;
            }
            catch
            {
                // 解析失败则走下面的兜底
            }
        }

        // 2) 兜底：构造时注入的单个视觉模型（框架能稳定解析）
        if (injected != null)
            return new[] { injected };

        return Array.Empty<IVisionModel>();
    }

    IVisionModel? ResolveModel()
    {
        var models = Models().Where(m => m != null).ToList();
        if (models.Count == 0)
            return null;

        string want = (getConfig().VisionSource ?? "").Trim();
        if (want.Length > 0)
        {
            IVisionModel? match = models.FirstOrDefault(m =>
            {
                string name = ModelName(m);
                return string.Equals(name, want, StringComparison.OrdinalIgnoreCase)
                       || name.Contains(want, StringComparison.OrdinalIgnoreCase)
                       || m.GetType().Name.Contains(want, StringComparison.OrdinalIgnoreCase);
            });
            if (match != null)
                return match;
            logger.LogWarning($"指定的视觉模型「{want}」没找到，改用第一个已注册的视觉模型。");
        }

        return models[0];
    }

    // ---------------- 抽帧识图 ----------------

    /// <summary>
    /// 对一条视频（网络流地址）选出「画面真正变过」的关键帧并识图，返回按时间轴拼好的画面描述：
    /// 先一段「【全片概览】」（网格图一次识图），再逐帧的「[mm:ss] 描述」。
    /// title / subtitleHint 用于三路互校（画面与字幕对照）；不传也不影响主流程。
    /// 无 ffmpeg / 无视觉模型 / 无画面结果时返回空串，调用方按「无补充素材」处理。
    /// </summary>
    public async Task<string> DescribeAsync(
        string videoUrl,
        double durationSec,
        CancellationToken ct = default,
        string title = "",
        string subtitleHint = "")
    {
        LastFrameCount = 0;
        LastModelName = "";
        LastOverviewCells = 0;
        LastOverviewSheets = 0;
        LastDedupDropped = 0;
        if (string.IsNullOrWhiteSpace(videoUrl))
            return "";

        TranscribeCapability cap = transcriber?.Detect() ?? new TranscribeCapability();
        if (!cap.FfmpegFound)
        {
            logger.LogDebug("抽帧识图不可用：本机未找到 ffmpeg。");
            return "";
        }

        IVisionModel? model = ResolveModel();
        if (model == null)
        {
            logger.LogDebug("抽帧识图不可用：没解析到视觉模型（请在角色里启用一个视觉模型模块）。");
            return "";
        }

        BiliScrollConfig cfg = getConfig();
        int interval = Math.Max(3, cfg.FrameIntervalSec);
        int maxCount = Math.Clamp(cfg.FrameMaxCount, 1, 200);
        int maxWidth = Math.Clamp(cfg.FrameMaxWidth, 160, 1920);
        int chars = Math.Clamp(cfg.FrameDescChars, 40, 2000);
        int concurrency = Math.Clamp(cfg.FrameConcurrency, 1, 6);
        bool useContext = cfg.FrameUseContext;
        int segSize = useContext ? Math.Clamp(cfg.FrameSegmentSize, 1, 20) : 1;
        int lookback = useContext ? Math.Clamp(cfg.FrameContextLookback, 0, 5) : 0;

        // 时长未知时（少数分P）按「间隔 × 上限」估一个整体长度
        double dur = durationSec > 0 ? durationSec : (double)interval * maxCount;

        // 概览网格尺寸：格数/每格宽度都由配置决定，并受总宽安全线约束
        int cells = Math.Clamp(cfg.FrameOverviewCells <= 0 ? DefaultOverviewCells : cfg.FrameOverviewCells,
            MinOverviewCells, MaxOverviewCells);
        int columns = cells <= 9 ? 3 : 4;
        int rows = (int)Math.Ceiling(cells / (double)columns);
        int perSheet = columns * rows;

        int cellWidth = Math.Clamp(
            cfg.FrameOverviewCellWidth <= 0 ? DefaultOverviewCellWidth : cfg.FrameOverviewCellWidth,
            MinCellWidth, MaxCellWidth);
        cellWidth = Math.Min(cellWidth, Math.Max(MinCellWidth, (MaxSheetWidth - 20) / columns));

        // 关键帧只抽一次：概览和逐帧识图共用同一张图（不再为拼图重复拉流）
        int frameWidth = Math.Clamp(Math.Max(maxWidth, cellWidth),
            MinCellWidth, Math.Max(MinCellWidth, (MaxSheetWidth - 20) / columns));

        string dir = Path.Combine(Path.GetTempPath(), "biliscroll_frames_" + Guid.NewGuid().ToString("N"));
        string overview = "";
        try
        {
            Directory.CreateDirectory(dir);

            // 1) 选关键帧：默认粗扫 + 变化处加密；关掉则退回等间隔抽帧（老做法）
            List<double> keys = cfg.FrameDedup
                ? await SelectKeyframesAsync(cap.FfmpegPath, videoUrl, dur, interval, maxCount, concurrency, dir, ct)
                : UniformTimes(dur, interval, maxCount);
            if (keys.Count == 0)
            {
                logger.LogDebug("抽帧识图：一帧都没抽出来，跳过。");
                return "";
            }

            // 2) 抽关键帧（统一宽度：概览格子与逐帧识图共用，拼图不会因尺寸不一而错位）
            string keyDir = Path.Combine(dir, "keys");
            Directory.CreateDirectory(keyDir);
            string?[] keyFiles = new string?[keys.Count];
            using (var gate = new SemaphoreSlim(concurrency))
            {
                await Task.WhenAll(Enumerable.Range(0, keys.Count).Select(async i =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        string path = Path.Combine(keyDir, $"k{i:000}.jpg");
                        if (await ExtractFrameAsync(cap.FfmpegPath, videoUrl, keys[i], path, frameWidth, ct))
                            keyFiles[i] = path;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            var kept = new List<(double Time, string Path)>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (keyFiles[i] is { } file)
                    kept.Add((keys[i], file));
            }
            if (kept.Count == 0)
            {
                logger.LogDebug("抽帧识图：关键帧一帧都没抽出来，跳过。");
                return "";
            }

            // 3) 全片概览：只拼这些「画面真正变过」的关键帧，格数多时按时间切成多张
            if (cfg.FrameOverview)
            {
                var overviewResult = await CaptureOverviewAsync(
                    model, cap.FfmpegPath, kept, columns, rows, perSheet, cellWidth, title, chars, dir, ct);
                if (overviewResult is { Text.Length: > 0 } ov)
                {
                    overview = ov.Text;
                    LastOverviewCells = ov.Cells;
                    LastOverviewSheets = ov.Sheets;
                }
            }

            // 4) 逐帧分析：段内串行（后一帧能看到前几帧的描述），段间有限并发
            int segmentCount = (kept.Count + segSize - 1) / segSize;
            var described = new ConcurrentDictionary<int, string>();
            using (var segGate = new SemaphoreSlim(Math.Min(2, Math.Max(1, segmentCount))))
            {
                await Task.WhenAll(Enumerable.Range(0, segmentCount).Select(async s =>
                {
                    await segGate.WaitAsync(ct);
                    try
                    {
                        var history = new List<string>();
                        int end = Math.Min(kept.Count, (s + 1) * segSize);
                        for (int i = s * segSize; i < end; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            (double time, string path) = kept[i];

                            string prompt = BuildFramePrompt(
                                time, title, subtitleHint, overview, history, chars, lookback > 0);

                            string desc = "";
                            try
                            {
                                desc = await model.QueryAsync(path, prompt, Math.Max(96, chars * 2), ct);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception e)
                            {
                                logger.LogDebug($"视觉识图单帧失败：{e.Message}");
                            }
                            TryDelete(path);

                            if (string.IsNullOrWhiteSpace(desc))
                                continue;

                            desc = Normalize(desc, chars);
                            if (desc.Length == 0)
                                continue;

                            described[i] = desc;
                            if (lookback > 0)
                            {
                                history.Add(desc);
                                while (history.Count > lookback)
                                    history.RemoveAt(0);
                            }
                        }
                    }
                    finally
                    {
                        segGate.Release();
                    }
                }));
            }

            // 5) 按时间轴拼装（概览在前，逐帧在后）
            var sb = new StringBuilder();
            if (overview.Length > 0)
            {
                sb.AppendLine("【全片概览（只含画面真正变化过的关键帧）】");
                sb.AppendLine(Clip(overview, Math.Max(chars * 2, 400)));
            }
            for (int i = 0; i < kept.Count; i++)
            {
                if (described.TryGetValue(i, out string? desc))
                    sb.Append('[').Append(Stamp(kept[i].Time)).Append("] ").Append(desc).Append('\n');
            }

            LastFrameCount = described.Count;
            LastModelName = LastFrameCount > 0 ? ModelName(model) : "";
            return sb.ToString().Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning($"抽帧识图失败（已跳过，不影响本轮）：{e.Message}");
            return "";
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------- 关键帧选择（粗扫 + 变化处加密）----------------

    /// <summary>
    /// 远程流版「逐帧比差异」：先粗扫找变化段，再只在变化段里加密，取「变化结束后稳定」的那一帧。
    /// 相比等间隔抽帧，静止的 PPT 页不会隔几秒重复出现，短时间出现的内容也不会整段漏掉。
    /// </summary>
    async Task<List<double>> SelectKeyframesAsync(
        string ffmpeg,
        string url,
        double dur,
        int interval,
        int maxKeys,
        int concurrency,
        string dir,
        CancellationToken ct)
    {
        // 粗扫密度：优先按用户的抽帧间隔，但至少覆盖全片 8~32 个点
        double step = Math.Max(CoarseMinGapSec, Math.Max(interval * 0.75, dur / CoarseMaxSamples));
        int n = Math.Clamp((int)Math.Ceiling(dur / step), CoarseMinSamples, CoarseMaxSamples);
        var times = new List<double>(n);
        for (int i = 0; i < n; i++)
            times.Add(dur * (i + 0.5) / n);

        string coarseDir = Path.Combine(dir, "coarse");
        Directory.CreateDirectory(coarseDir);
        string?[] frames = new string?[n];
        using (var gate = new SemaphoreSlim(concurrency))
        {
            await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(coarseDir, $"{i:000}.jpg");
                    if (await ExtractFrameAsync(ffmpeg, url, times[i], path, CoarseWidth, ct))
                        frames[i] = path;
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        // 逐对相似度：score = 1 - SSIM，越大越「变了」
        var scores = new double[n];
        var scoresKnown = new bool[n];
        for (int i = 0; i + 1 < n; i++)
        {
            if (frames[i] == null || frames[i + 1] == null)
                continue;
            double ssim = await SimilarityAsync(ffmpeg, frames[i]!, frames[i + 1]!, ct);
            if (ssim < 0)
                continue;
            scores[i] = Math.Clamp(1 - ssim, 0, 1);
            scoresKnown[i] = true;
        }

        double threshold = ChangeThreshold(scores, scoresKnown);
        var gaps = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        {
            if (scoresKnown[i] && scores[i] > threshold)
                gaps.Add(i);
        }

        var keys = new List<double> { times[0] };
        // 变化段里加密：找到「变化结束、画面重新稳定」的那一帧
        for (int g = 0; g < gaps.Count && g < RefineMaxGaps; g++)
        {
            ct.ThrowIfCancellationRequested();
            int index = gaps[g];
            double stable = await RefineGapAsync(
                ffmpeg, url, times[index], times[index + 1], frames[index], dir, g, ct);
            keys.Add(stable);
        }

        // 片尾还在变：补上结尾画面，别丢掉最后一个状态
        if (gaps.Count > 0 && gaps[^1] >= n - 2)
            keys.Add(Math.Max(0, dur - 0.1));

        // 压缩到上限：按时间均匀抽稀（在已经选好的好帧里均匀挑）
        keys = keys.Where(t => t >= 0 && t <= Math.Max(0, dur)).OrderBy(t => t).ToList();
        var merged = new List<double>();
        foreach (double t in keys)
        {
            if (merged.Count == 0 || t - merged[^1] >= MinKeyGapSec)
                merged.Add(t);
        }
        if (merged.Count > maxKeys)
        {
            var thinned = new List<double>(maxKeys);
            for (int i = 0; i < maxKeys; i++)
                thinned.Add(merged[(int)Math.Round(i * (merged.Count - 1) / (double)(maxKeys - 1))]);
            merged = thinned.Distinct().ToList();
        }

        LastDedupDropped = Math.Max(0, n - merged.Count);
        logger.LogDebug($"关键帧选择：粗扫 {n} 点（阈值 {threshold:0.000}），变化段 {gaps.Count} 个，最终 {merged.Count} 帧。");
        if (frames.Length > 0)
            TryDeleteDirectory(coarseDir);
        return merged;
    }

    /// <summary>
    /// 在一个粗扫变化段里补几帧，返回「画面重新稳定」那一刻的时间。
    /// 稳定 = 与上一帧 SSIM ≥ StableSsim；整段都没稳定就取段末。
    /// </summary>
    async Task<double> RefineGapAsync(
        string ffmpeg,
        string url,
        double start,
        double end,
        string? previous,
        string dir,
        int gapIndex,
        CancellationToken ct)
    {
        if (end <= start)
            return start;

        string gapDir = Path.Combine(dir, $"gap{gapIndex}");
        Directory.CreateDirectory(gapDir);
        double step = Math.Max(1.0, (end - start) / RefineSamplesPerGap);

        string? prev = previous;
        double lastTime = start;
        for (int j = 1; j <= RefineSamplesPerGap; j++)
        {
            ct.ThrowIfCancellationRequested();
            double t = Math.Min(end, start + step * j);
            if (t <= lastTime)
                continue;
            lastTime = t;

            string path = Path.Combine(gapDir, $"{j:000}.jpg");
            if (!await ExtractFrameAsync(ffmpeg, url, t, path, CoarseWidth, ct))
                continue;

            if (prev != null)
            {
                double ssim = await SimilarityAsync(ffmpeg, prev, path, ct);
                if (ssim >= StableSsim)
                    return t;
            }
            prev = path;
        }

        return lastTime;
    }

    /// <summary>自适应阈值：大部分时间是静止的，中位数约等于底噪；取底噪 × 3 并夹在上下限内</summary>
    static double ChangeThreshold(double[] scores, bool[] known)
    {
        var values = new List<double>();
        for (int i = 0; i < scores.Length; i++)
        {
            if (known[i])
                values.Add(scores[i]);
        }
        if (values.Count == 0)
            return ChangeFloor;
        values.Sort();
        double median = values[values.Count / 2];
        return Math.Clamp(median * ChangeNoiseK, ChangeFloor, ChangeCeil);
    }

    /// <summary>关掉「按画面变化选帧」时的老做法：沿时间轴等间隔取中点，首尾不漏</summary>
    static List<double> UniformTimes(double dur, int interval, int maxCount)
    {
        int n = (int)Math.Floor(dur / interval);
        if (n <= 0)
            n = 1;
        n = Math.Clamp(n, 1, maxCount);
        var times = new List<double>(n);
        for (int i = 0; i < n; i++)
            times.Add(dur * (i + 0.5) / n);
        return times;
    }

    // ---------------- 全片概览（网格图）----------------

    /// <summary>
    /// 把关键帧按时间顺序拼成网格图：每张最多 cells 格（3 列或 4 列），
    /// 格数多时按时间切成最多 <see cref="MaxOverviewSheets"/> 张 —— 每张都保证每格看得清。
    /// 失败返回 null（不影响后续逐帧分析）。
    /// </summary>
    async Task<(string Text, int Cells, int Sheets)?> CaptureOverviewAsync(
        IVisionModel model,
        string ffmpeg,
        IReadOnlyList<(double Time, string Path)> frames,
        int columns,
        int rows,
        int perSheet,
        int cellWidth,
        string title,
        int chars,
        string dir,
        CancellationToken ct)
    {
        var picked = frames.OrderBy(f => f.Time).ToList();
        if (picked.Count == 0)
            return null;

        int sheetCount = Math.Clamp((int)Math.Ceiling(picked.Count / (double)perSheet), 1, MaxOverviewSheets);
        int capacity = sheetCount * perSheet;
        if (picked.Count > capacity)
        {
            var thinned = new List<(double Time, string Path)>(capacity);
            for (int k = 0; k < capacity; k++)
                thinned.Add(picked[(int)Math.Round(k * (picked.Count - 1) / (double)(capacity - 1))]);
            picked = thinned.Distinct().ToList();
        }
        sheetCount = Math.Clamp((int)Math.Ceiling(picked.Count / (double)perSheet), 1, MaxOverviewSheets);

        string sheetRoot = Path.Combine(dir, "sheets");
        var sb = new StringBuilder();
        int totalCells = 0;
        int doneSheets = 0;
        int actualColumns = columns;
        int actualRows = rows;

        for (int s = 0; s < sheetCount; s++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = picked.Skip(s * perSheet).Take(perSheet).ToList();
            if (chunk.Count == 0)
                continue;

            string sheetDir = Path.Combine(sheetRoot, $"s{s}");
            Directory.CreateDirectory(sheetDir);
            for (int i = 0; i < chunk.Count; i++)
            {
                try
                {
                    File.Copy(chunk[i].Path, Path.Combine(sheetDir, $"{i:000}.jpg"), true);
                }
                catch (Exception e)
                {
                    logger.LogDebug($"全片概览第 {s + 1} 张：复制第 {i + 1} 格失败（{e.Message}）");
                }
            }

            int sheetColumns = Math.Min(columns, chunk.Count);
            int sheetRows = (int)Math.Ceiling(chunk.Count / (double)columns);
            string grid = Path.Combine(sheetDir, "grid.jpg");
            string args =
                "-nostdin -hide_banner -loglevel error -y "
                + "-framerate 1 -start_number 0 "
                + $"-i \"{Path.Combine(sheetDir, "%03d.jpg")}\" "
                + $"-vf \"tile={sheetColumns}x{sheetRows}:padding=4:margin=4\" "
                + "-q:v 2 -frames:v 1 "
                + $"\"{grid}\"";
            var (ok, _, err) = await RunProcessAsync(ffmpeg, args, ct, TimeSpan.FromSeconds(120));
            if (!ok || !File.Exists(grid) || new FileInfo(grid).Length == 0)
            {
                logger.LogDebug($"全片概览第 {s + 1} 张拼图失败：{Trim(err, 200)}");
                continue;
            }

            string prompt = BuildOverviewPrompt(
                chunk.Select(c => c.Time).ToList(), sheetColumns, sheetRows, title, chars, s + 1, sheetCount);
            string text = await model.QueryAsync(grid, prompt, Math.Max(192, chars * 2), ct);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (sheetCount > 1)
                sb.AppendLine($"【第 {s + 1}/{sheetCount} 张：{Stamp(chunk[0].Time)} – {Stamp(chunk[chunk.Count - 1].Time)}】");
            sb.AppendLine(text.Trim());
            if (totalCells == 0)
            {
                actualColumns = sheetColumns;
                actualRows = sheetRows;
            }
            totalCells += chunk.Count;
            doneSheets++;
        }

        if (totalCells == 0)
            return null;

        // 这里需要 ffmpeg 路径，由调用方持有；拼图已经在上面完成，直接返回结果
        logger.LogDebug($"全片概览完成：{doneSheets} 张 / {totalCells} 格，每格 {cellWidth}px。");
        return (sb.ToString().Trim(), totalCells, doneSheets);
    }

    static string BuildOverviewPrompt(
        IReadOnlyList<double> stamps, int columns, int rows, string title, int chars,
        int sheetIndex, int sheetCount)
    {
        var map = string.Join("，", stamps.Select((t, i) => $"第{i + 1}格={Stamp(t)}"));
        var sb = new StringBuilder();
        if (columns == 1 && rows == 1)
        {
            sb.AppendLine("这是一条中文视频里唯一一个「画面发生实质变化」的关键帧（整片画面基本没变时的代表帧）。");
        }
        else
        {
            sb.AppendLine("这是一条中文视频的全片概览：只保留「画面真正发生实质变化」后的稳定帧（不是等间隔抽帧），"
                          + $"按时间顺序拼成一张 {columns} 列 × {rows} 行的截图网格，请从左到右、从上到下阅读。");
        }
        if (sheetCount > 1)
            sb.AppendLine($"这是第 {sheetIndex}/{sheetCount} 张，覆盖其中一段连续时间；多张按时间顺序排列。");
        sb.AppendLine("格子与时间对照：" + map + "。");
        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine("视频标题：" + Clip(title, 80));
        sb.AppendLine("请用中文输出三部分：");
        sb.AppendLine("1) 一句话说明这条视频在讲什么、属于哪类（PPT 讲课 / 录屏演示 / 图示讲解 / 实拍等）；");
        sb.AppendLine("2) 按时间段概括结构：从第几格（mm:ss）到第几格（mm:ss）在讲什么，指出主要主题与画面切换点；");
        sb.AppendLine("3) 如果全程画面基本没变（例如一直是同一页 PPT 或同一个录屏界面），直接说明「全程画面基本未变」。");
        sb.AppendLine($"不要逐格报流水账；看不出来的内容就说没看清，不要猜。最多 {chars} 字。");
        return sb.ToString();
    }

    static string BuildFramePrompt(
        double time,
        string title,
        string subtitleHint,
        string overview,
        IReadOnlyList<string> history,
        int chars,
        bool useContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"这是一条中文知识类视频在 {Stamp(time)} 的截图（多为 PPT / 图表 / 板书 / 代码）。");
        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine("视频标题：" + Clip(title, 80));
        if (overview.Length > 0)
            sb.AppendLine("【全片概览（供定位，不要照抄）】" + Clip(overview, 300));
        if (!string.IsNullOrWhiteSpace(subtitleHint))
            sb.AppendLine("【同片字幕摘录（可能不全，仅作对照）】" + Clip(subtitleHint, 300));
        if (useContext && history.Count > 0)
        {
            sb.AppendLine("【前情（同一段里更早的画面，从远到近）】");
            foreach (string h in history)
                sb.AppendLine("- " + Clip(h, 160));
        }
        sb.AppendLine("请只描述这一帧画面上真实存在的内容：标题、要点条目、图表或示意图的含义、公式、代码片段、任何关键文字。");
        if (useContext && history.Count > 0)
            sb.AppendLine("和上一帧比较：有明显变化就用「变化：」开头，写清新增/替换了什么；"
                         + "基本没变就用「延续：」开头，只写和上一帧不同或补充的信息，不要重复前情里的内容。");
        sb.AppendLine("若字幕与画面冲突，以画面为准；看不清就说「没看清」，不要猜视频在讲什么、不要编造。");
        sb.AppendLine($"最多 {chars} 字。");
        return sb.ToString();
    }

    /// <summary>把「秒」格式化成 mm:ss。</summary>
    static string Stamp(double sec)
    {
        int s = (int)Math.Round(sec);
        if (s < 0)
            s = 0;
        return $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>把模型返回的整段文字压成一行并截断。</summary>
    static string Normalize(string text, int chars)
    {
        text = text.Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("  ", " ");
        while (text.Contains("  "))
            text = text.Replace("  ", " ");
        return text.Length > chars ? text.Substring(0, chars) : text;
    }

    static string Clip(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    /// <summary>
    /// 用 ffmpeg 从网络视频流里抽一帧：带 Referer/UA 头（B站的流必须带 Referer 才能拉），
    /// -ss 放在 -i 之前做快速定位，缩放到指定宽度（高度按比例自动、取偶），只出一帧。
    /// </summary>
    async Task<bool> ExtractFrameAsync(string ffmpeg, string url, double t, string outPath, int maxWidth, CancellationToken ct)
    {
        // 只有 http(s) 输入才带协议头；本地文件/其它输入带 -headers 会被 ffmpeg 当未知选项拒掉
        string headersPart = "";
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            string headers = "Referer: " + Referer + "\\r\\nUser-Agent: " + UserAgent + "\\r\\n";
            headersPart = $"-headers \"{headers}\" ";
        }
        string args =
            "-nostdin -hide_banner -loglevel error "
            + headersPart
            + $"-ss {t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
            + $"-i \"{url}\" "
            + $"-frames:v 1 -vf \"scale={maxWidth}:-2\" -q:v 4 -y \"{outPath}\"";

        var (ok, _, _) = await RunProcessAsync(ffmpeg, args, ct, TimeSpan.FromSeconds(90));
        return ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0;
    }

    static readonly Regex SsimRegex = new(@"All:\s*([0-9.]+)", RegexOptions.Compiled);

    /// <summary>
    /// 两张已抽出的帧（同尺寸）的结构相似度：用 ffmpeg 的 ssim 滤镜算，解析失败返回 -1。
    /// SSIM ≥ <see cref="StableSsim"/> 视为「画面没变」。
    /// </summary>
    async Task<double> SimilarityAsync(string ffmpeg, string a, string b, CancellationToken ct)
    {
        string args = $"-nostdin -hide_banner -loglevel info -i \"{a}\" -i \"{b}\" -lavfi ssim -f null -";
        var (ok, _, err) = await RunProcessAsync(ffmpeg, args, ct, TimeSpan.FromSeconds(30));
        if (!ok)
            return -1;
        Match m = SsimRegex.Match(err);
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ssim))
            return -1;
        return ssim;
    }

    // ---------------- 进程调用（对齐 Transcriber 的既有模式）----------------

    async Task<(bool Ok, string StdOut, string StdErr)> RunProcessAsync(string ffmpeg, string args, CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, args)
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
                return (false, "", "");

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
                logger.LogDebug($"ffmpeg 抽帧超时（{timeout.TotalSeconds:0}s），已终止。");
                return (false, stdout, stderr);
            }
            if (p.ExitCode != 0)
                logger.LogDebug($"ffmpeg 抽帧退出码 {p.ExitCode}：{Trim(stderr, 200)}");
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug($"运行 ffmpeg 抽帧失败：{e.Message}");
            return (false, "", "");
        }
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

    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
