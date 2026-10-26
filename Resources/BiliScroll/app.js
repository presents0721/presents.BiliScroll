/* ============================================================
   刷B站 · 控制台 前端
   交互借鉴 dsh-bili-scroll 设置页：开关级联显隐、防抖保存、
   徽章行、消息条、日志块、二维码卡片 —— 视觉用本插件自有主题
   ============================================================ */
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);

  let cfg = null;         // 本地配置副本
  let state = {};         // 最新 /api/state
  let saveTimer = null;
  let saving = false;     // 是否有一份配置正在提交
  let dirty = false;      // 提交期间又改过表单
  let lastSaved = '';     // 已落盘内容的 JSON，关页面时用来判断还需不需要补发
  let qrTimer = null;
  let running = false;

  // ---------------- 工具 ----------------
  function msg(text, tone = '') {
    const bar = $('msgBar');
    if (!text) { bar.className = 'msgbar hidden'; bar.textContent = ''; return; }
    bar.className = 'msgbar ' + tone;
    bar.textContent = text;
    if (tone === 'ok' || tone === 'warn') {
      clearTimeout(msg._t);
      msg._t = setTimeout(() => { if (bar.textContent === text) msg('', ''); }, 4200);
    }
  }

  async function api(path, opts) {
    const res = await fetch(path, opts);
    try { return await res.json(); } catch { return { ok: false, message: '返回内容无法解析' }; }
  }

  function postJson(path, body) {
    return api(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
  }

  // ---------------- 表单读写 ----------------
  const numFields = ['maxRoundsPerDay', 'recentDays', 'maxCandidates', 'minDurationSec', 'maxDurationSec', 'pickCount', 'maxCommentsPerRound', 'maxRepliesPerRound', 'port', 'coinsPerDay', 'maxAudioMinutes', 'qqTargetId', 'frameIntervalSec', 'frameMaxCount', 'frameMaxWidth', 'frameDescChars', 'visionTextThreshold', 'frameOverviewCells', 'frameOverviewCellWidth', 'frameSegmentSize', 'frameContextLookback', 'replyMaxChars', 'incomingReplyMaxChars', 'aiCommentThreshold', 'detailMaxChars', 'ownerUid', 'danmakuCount', 'commentCount', 'subReplyCount', 'dmMaxPerRound', 'dmScanCount', 'realtimeDmIntervalSec', 'realtimeCommentIntervalSec', 'realtimeMaxPerCycle', 'devTaskMaxMinutes'];
  const boolFields = ['autoRun', 'runNoon', 'runNight', 'includeDanmaku', 'autoComment', 'replyToComments', 'autoOpen', 'allowWrite', 'dryRun', 'localTranscribe', 'keepAudio', 'qqSendToGroup', 'useVision', 'frameOverview', 'frameDedup', 'frameUseContext', 'usePersona', 'learnFromVideos', 'selfBrowse', 'selfLikeFavorite', 'reportHistory', 'antiInjection', 'filterAiComments', 'notifyOwnerByPrivateMessage', 'autoReplyDm', 'realtimeReply', 'devTaskEnabled', 'devTaskDryRun'];
  const textFields = ['ffmpegPath', 'whisperPath', 'focus', 'visionSource', 'dmFilterKeywords', 'outwardGuide', 'devTaskAllowedHosts'];
  const selectFields = ['transcribeBackend', 'transcribeModel', 'analysisDepth', 'searchOrder'];

  // 写操作权限网格：动作 → 配置字段（与后端 ActionGuard 一一对应）
  const permItems = [
    { field: 'allowComment', label: '发表评论 / 回复楼中楼' },
    { field: 'allowVideoLike', label: '点赞视频' },
    { field: 'allowCoin', label: '投币' },
    { field: 'allowFavorite', label: '收藏视频' },
    { field: 'allowFollow', label: '关注 UP 主' },
    { field: 'allowDanmaku', label: '发送弹幕' },
    { field: 'allowDynamicPublish', label: '发布动态' },
    { field: 'allowPrivateReply', label: '发送私信' },
    { field: 'allowVideoUpload', label: '投稿上传（只生成草稿）' }
  ];

  function fillForm(c) {
    cfg = c;
    boolFields.forEach(k => { const el = $(k); if (el) el.checked = !!c[k]; });
    numFields.forEach(k => { const el = $(k); if (el) el.value = c[k] ?? 0; });
    textFields.forEach(k => { const el = $(k); if (el) el.value = c[k] || ''; });
    $('transcribeBackend').value = c.transcribeBackend || '自动';
    $('transcribeModel').value = c.transcribeModel || 'tiny';
    $('analysisDepth').value = c.analysisDepth || '详';
    $('searchOrder').value = c.searchOrder || '最新';
    $('keywords').value = Array.isArray(c.keywords) ? c.keywords.join('\n') : (c.keywords || '');
    $('reportDetail').value = c.reportDetail || '完整';
    $('atName').value = c.atName || '';
    renderPermGrid();
    applyCascade();
    lastSaved = JSON.stringify(readForm());   // 记住服务端基线，关页面时才判断要不要补发
  }

  function readForm() {
    const c = Object.assign({}, cfg || {});
    boolFields.forEach(k => { const el = $(k); if (el) c[k] = el.checked; });
    numFields.forEach(k => { const el = $(k); if (el) c[k] = parseInt(el.value, 10) || 0; });
    textFields.forEach(k => { const el = $(k); if (el) c[k] = el.value.trim(); });
    document.querySelectorAll('.perm-sw').forEach(sw => { c[sw.dataset.field] = sw.checked; });
    c.transcribeBackend = $('transcribeBackend').value;
    c.transcribeModel = $('transcribeModel').value;
    c.analysisDepth = $('analysisDepth').value;
    c.searchOrder = $('searchOrder').value;
    c.keywords = $('keywords').value;   // 后端字段是字符串（按换行/逗号切分），不能传数组
    c.reportDetail = $('reportDetail').value;
    c.atName = $('atName').value.trim();
    return c;
  }

  function renderPermGrid() {
    const grid = $('permGrid');
    if (!grid) return;
    grid.innerHTML = '';
    permItems.forEach(it => {
      const row = document.createElement('label');
      row.className = 'perm-item';
      row.innerHTML = '<span class="grow"><b>' + escapeHtml(it.label) + '</b></span>'
        + '<span class="perm-badge">账号</span>';
      const sw = document.createElement('input');
      sw.type = 'checkbox';
      sw.setAttribute('data-sw', '');
      sw.className = 'perm-sw';
      sw.dataset.field = it.field;
      sw.checked = !!(cfg && cfg[it.field]);
      sw.addEventListener('change', saveNow);
      row.appendChild(sw);
      grid.appendChild(row);
    });
  }

  // 输入时 500ms 防抖；离开页面/切后台时再用 sendBeacon 补发一次，
  // 避免关掉控制台或切页把最后这半秒的修改丢掉（重开后又回到旧预设）。
  function saveSoon() {
    if (!cfg) return;   // 首屏配置还没拉到，先别拿空表单覆盖服务端
    cfg = readForm();
    clearTimeout(saveTimer);
    saveTimer = setTimeout(saveNow, 500);
  }

  async function saveNow() {
    clearTimeout(saveTimer);
    saveTimer = null;
    if (!cfg) return;
    cfg = readForm();
    if (saving) { dirty = true; return; }

    const body = JSON.stringify(cfg);
    saving = true;
    try {
      const r = await postJson('/api/config', cfg);
      if (!r.ok) { msg('保存失败：' + (r.message || '未知错误'), 'err'); return; }
      lastSaved = body;
      msg('设置已保存', 'ok');
      applyCascade();
    } catch (e) {
      msg('保存失败：' + ((e && e.message) || '请求没发出去'), 'err');
    } finally {
      saving = false;
      if (dirty) { dirty = false; saveNow(); }
    }
  }

  function flushSave() {
    if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
    if (!cfg) return;
    const body = JSON.stringify(readForm());
    if (body === lastSaved) return;
    if (navigator.sendBeacon) {
      navigator.sendBeacon('/api/config', new Blob([body], { type: 'application/json' }));
      lastSaved = body;
    } else {
      fetch('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true
      });
      lastSaved = body;
    }
  }

  function applyCascade() {
    const set = (group, on) => {
      const el = $(group);
      if (el) el.classList.toggle('disabled', !on);
    };
    set('valleyGroup', $('autoRun').checked);
    set('commentGroup', $('autoComment').checked);
    set('replyGroup', $('replyToComments').checked);
    set('writeGroup', $('allowWrite').checked);
    set('transcribeGroup', $('localTranscribe').checked);
    set('visionGroup', $('useVision').checked);
    set('realtimeGroup', $('realtimeReply').checked);
    set('devTaskGroup', $('devTaskEnabled').checked);
  }

  // 抽帧场景预设：一键填表，之后仍可逐项手改
  const framePresets = {
    daily:   { useVision: false, frameIntervalSec: 20, frameMaxCount: 8,  frameMaxWidth: 640,  frameDescChars: 300,  visionTextThreshold: 600, frameOverview: true, frameOverviewCells: 6,  frameOverviewCellWidth: 256, frameDedup: true, frameUseContext: true, frameSegmentSize: 6, frameContextLookback: 1 },
    general: { useVision: true,  frameIntervalSec: 15, frameMaxCount: 16, frameMaxWidth: 960,  frameDescChars: 600,  visionTextThreshold: 600, frameOverview: true, frameOverviewCells: 8,  frameOverviewCellWidth: 480, frameDedup: true, frameUseContext: true, frameSegmentSize: 5, frameContextLookback: 2 },
    tech:    { useVision: true,  frameIntervalSec: 8,  frameMaxCount: 24, frameMaxWidth: 1280, frameDescChars: 1000, visionTextThreshold: 400, frameOverview: true, frameOverviewCells: 12, frameOverviewCellWidth: 640, frameDedup: true, frameUseContext: true, frameSegmentSize: 4, frameContextLookback: 2 },
    paper:   { useVision: true,  frameIntervalSec: 4,  frameMaxCount: 32, frameMaxWidth: 1600, frameDescChars: 1600, visionTextThreshold: 300, frameOverview: true, frameOverviewCells: 12, frameOverviewCellWidth: 640, frameDedup: true, frameUseContext: true, frameSegmentSize: 4, frameContextLookback: 2 },
    ainews:  { useVision: true,  frameIntervalSec: 4,  frameMaxCount: 32, frameMaxWidth: 1600, frameDescChars: 1400, visionTextThreshold: 300, frameOverview: true, frameOverviewCells: 12, frameOverviewCellWidth: 640, frameDedup: true, frameUseContext: true, frameSegmentSize: 4, frameContextLookback: 2 }
  };

  function applyFramePreset(name) {
    const preset = framePresets[name];
    if (!preset) return;
    if (!cfg) { msg('控制台还在加载配置，请稍后再点', 'warn'); return; }
    Object.keys(preset).forEach(k => {
      const el = $(k);
      if (!el) return;
      if (el.type === 'checkbox') el.checked = !!preset[k];
      else el.value = preset[k];
    });
    applyCascade();
    saveNow();   // 预设立刻落盘，不等防抖
    msg('已套用场景预设并保存，可继续逐项手改', 'ok');
  }

  function bindForm() {
    // input = 打字过程中防抖保存；change = 失焦/选完立即落盘，缩短没保存的窗口
    [...numFields, ...boolFields].forEach(k => {
      const el = $(k);
      if (!el) return;
      el.addEventListener('change', saveNow);
      if (el.type === 'number') el.addEventListener('input', saveSoon);
    });
    [...textFields, ...selectFields, 'keywords', 'reportDetail', 'atName'].forEach(id => {
      const el = $(id);
      if (el) { el.addEventListener('input', saveSoon); el.addEventListener('change', saveNow); }
    });
  }

  // ---------------- 渲染状态 ----------------
  function renderState() {
    $('ver').textContent = 'v' + (state.version || '4.9.5');
    $('charBadge').textContent = '角色 ' + (state.character || '—');
    if ($('realtimeStatus')) $('realtimeStatus').textContent = state.realtimeStatus || '—';
    if ($('devTaskStatus')) $('devTaskStatus').textContent = state.devTaskStatus || '—';

    const auth = $('authBadge');
    if (state.loggedIn) { auth.textContent = '已登录'; auth.className = 'badge on'; }
    else { auth.textContent = '未登录'; auth.className = 'badge off'; }

    const max = (state.config && state.config.maxRoundsPerDay) || 0;
    $('roundBadge').textContent = max > 0
      ? `今天已跑 ${state.roundsToday || 0}/${max} 轮`
      : `今天已跑 ${state.roundsToday || 0} 轮`;

    running = !!state.running;
    renderRunState(running);

    $('homePath').textContent = state.pluginDir || '—';
    $('reportPath').textContent = state.todayReport || '—';
    $('portInfo').textContent = (state.config && state.config.port) || '—';

    // 登录态切换
    const logged = !!state.loggedIn;
    $('loggedBox').classList.toggle('hidden', !logged);
    $('loginBox').classList.toggle('hidden', logged);
    if (logged) {
      $('userName').textContent = state.userName || '已登录';
      $('userMid').textContent = 'UID ' + (state.mid || '—');
      $('userAvatar').textContent = (state.userName || 'U').trim().charAt(0).toUpperCase();
      stopQr();
    }
  }

  function renderRunState(on) {
    const pill = $('runState');
    pill.textContent = on ? '正在跑…' : '空闲';
    pill.className = 'pill' + (on ? ' running' : '');
    $('btnRun').disabled = on;
    $('btnRun').textContent = on ? '跑着呢…' : '现在跑一轮';
    const stop = $('btnStop');
    if (stop) stop.classList.toggle('hidden', !on);
  }

  // ---------------- 日志 ----------------
  function logLine(level, text) {
    const box = $('log');
    const atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 24;
    const line = document.createElement('span');
    line.className = 'log-line log-' + (level || 'info');
    const t = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    line.innerHTML = `<span class="t">${t}</span>${escapeHtml(text)}`;
    box.appendChild(line);
    box.appendChild(document.createTextNode('\n'));
    while (box.childElementCount > 400) box.removeChild(box.firstChild);
    if (atBottom) box.scrollTop = box.scrollHeight;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // ---------------- WebSocket ----------------
  function connectWs() {
    let retry = 0;
    const open = () => {
      const ws = new WebSocket(`ws://${location.host}/ws`);
      ws.onopen = () => { retry = 0; };
      ws.onmessage = (ev) => {
        let m; try { m = JSON.parse(ev.data); } catch { return; }
        if (m.type === 'log') logLine(m.level, m.text);
        else if (m.type === 'hello') { logLine('info', `控制台已连接（v${m.version}）`); }
        else if (m.type === 'round') { logLine('ok', '一轮结束'); onRoundDone(m.result); }
        else if (m.type === 'auth') { refresh(); }
        else if (m.type === 'verify') { msg(`复核完成：${m.visible}/${m.count} 条公开可见`, 'ok'); }
      };
      ws.onclose = () => {
        retry++;
        if (retry <= 30) setTimeout(open, Math.min(8000, 800 * retry));
      };
    };
    open();
  }

  function onRoundDone(result) {
    running = false;
    renderRunState(false);
    if (result && result.error) msg('本轮出错：' + result.error, 'err');
    else if (result) {
      const skip = result.skippedNoSubtitle || 0;
      msg(`本轮完成：读字幕 ${result.readSubtitles || 0} 条，挑出 ${result.picked || 0} 条`
          + (skip ? `，因无字幕跳过 ${skip} 条` : ''), 'ok');
    }
    refresh();
    loadReports();
  }

  // ---------------- 动作 ----------------
  async function runNow() {
    if (running) return;
    renderRunState(true);
    msg('开始跑一轮，进度见右侧日志…');
    const r = await postJson('/api/run');
    if (!r.ok) { renderRunState(false); msg(r.message || '启动失败', 'err'); }
  }

  async function stopRound() {
    const stop = $('btnStop');
    if (stop) stop.disabled = true;
    msg('已请求停止，会在当前这一步结束后生效…', 'warn');
    const r = await postJson('/api/stop');
    if (!r.ok) msg(r.message || '停止请求失败', 'err');
    if (stop) setTimeout(() => { stop.disabled = false; }, 1500);
  }

  async function startQr() {
    const r = await postJson('/api/qr/generate');
    if (!r.ok || !r.key) { msg('生成二维码失败', 'err'); return; }
    $('qrWrap').classList.remove('hidden');
    renderQr(r.url);
    $('qrTip').textContent = '用手机 B站 App 扫码，确认时选「记住我 / 长期」';
    pollQr(r.key);
  }

  function pollQr(key) {
    stopQr();
    qrTimer = setInterval(async () => {
      const r = await postJson('/api/qr/poll?key=' + encodeURIComponent(key));
      const code = String(r.code);
      const map = {
        '86101': '等待扫码…', '86090': '已扫码，请在手机上确认…',
        '86038': '二维码已过期，请重新生成', '0': '登录成功'
      };
      $('qrTip').textContent = r.message || map[code] || '等待扫码…';
      if (code === '0' || r.loggedIn) { stopQr(); msg('登录成功', 'ok'); refresh(); }
      else if (code === '86038') stopQr();
    }, 2000);
  }

  function stopQr() {
    if (qrTimer) { clearInterval(qrTimer); qrTimer = null; }
  }

  function renderQr(url) {
    const box = $('qrImg');
    try {
      // qrcode-generator：typeNumber 0 = 自动
      const qr = qrcode(0, 'M');
      qr.addData(url);
      qr.make();
      box.innerHTML = qr.createSvgTag({ cellSize: 4, margin: 2, scalable: true });
    } catch (e) {
      box.innerHTML = `<img alt="二维码" src="https://api.qrserver.com/v1/create-qr-code/?size=150x150&data=${encodeURIComponent(url)}">`;
    }
  }

  async function logout() {
    if (!confirm('确定退出登录？退出后读字幕、发评论都用不了。')) return;
    await postJson('/api/logout');
    msg('已退出登录', 'ok');
    refresh();
  }

  async function verifyComments() {
    msg('正在复核评论可见性…');
    const r = await postJson('/api/verify');
    if (!r.ok) { msg(r.error || r.message || '复核失败', 'err'); return; }
    msg('复核完成，结论已追加到今天的日报末尾', 'ok');
    logLine('ok', '评论复核：' + (r.text || '').split('\n')[0]);
    loadReports();
    const d = new Date();
    openReport(`${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`);
  }

  async function openFolder(sub) {
    const r = await api('/api/open-folder' + (sub ? '?sub=' + sub : ''), { method: 'POST' });
    if (!r.ok) msg(r.message || '打开失败', 'err');
  }

  // ---------------- 写操作权限 / 本地转写 ----------------
  async function loadCapability() {
    const r = await api('/api/transcribe/status');
    const cap = (r && r.cap) || {};
    setDot('capFfmpeg', cap.ffmpegFound);
    setDot('capWhisper', cap.whisperFound);
    $('capFfmpegTxt').textContent = cap.ffmpegFound ? '已找到' : '未找到';
    $('capWhisperTxt').textContent = cap.whisperFound ? (cap.backend || '已找到') : '未找到';
    setDot('capVision', r && r.visionAvailable);
    $('capVisionTxt').textContent = (r && r.visionAvailable)
      ? (r.visionModel || '已启用')
      : (cap.ffmpegFound ? '未启用视觉模型' : '缺 ffmpeg');
  }

  function setDot(id, on) {
    const el = $(id);
    if (el) el.className = 'dot ' + (on ? 'on' : 'off');
  }

  async function testTranscribe() {
    const box = $('transcribeResult');
    box.classList.remove('hidden');
    box.textContent = '测试中…';
    const r = await postJson('/api/transcribe/test');
    box.textContent = (r && r.text) || '（无输出）';
    loadCapability();
  }

  // ---------------- 缺失工具一键下载 ----------------
  async function loadToolsPlan() {
    const box = $('toolsPlan');
    if (!box) return;
    const r = await api('/api/tools/plan');
    const plan = (r && r.plan) || [];
    if (!plan.length) { box.textContent = '（拿不到镜像清单）'; return; }
    box.innerHTML = plan.map(p =>
      '<div>• ' + escapeHtml(p.name)
      + '<br>　' + ((p.urls || []).map(escapeHtml).join('<br>　'))
      + '</div>'
    ).join('<div style="height:8px"></div>');
  }

  async function fetchTools(which, btn) {
    const box = $('toolsResult');
    box.classList.remove('hidden');
    box.textContent = '下载中…（ffmpeg/模型体积较大，可能要几分钟；进度见右侧实时日志）';
    if (btn) btn.disabled = true;
    try {
      const r = await postJson('/api/tools/fetch', { which });
      box.textContent = (r && r.text) || '（无输出）';
      msg('下载任务结束', 'ok');
      loadCapability();
    } catch (e) {
      box.textContent = '请求失败：' + e.message;
      msg('下载请求失败', 'err');
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  function showOp(text) {
    const box = $('opResult');
    box.classList.remove('hidden');
    box.textContent = typeof text === 'string' ? text : JSON.stringify(text, null, 2);
  }

  async function doInteract() {
    showOp('执行中…');
    const r = await postJson('/api/interact', {
      bvid: $('opBvid').value.trim(),
      action: $('opAction').value,
      text: $('opText').value.trim()
    });
    showOp(r.text || r.message || '无返回');
  }

  async function doDynamic() {
    showOp('发送中…');
    const r = await postJson('/api/dynamic', { text: $('dynText').value.trim() });
    showOp(r.text || r.message || '无返回');
  }

  async function doDraft() {
    showOp('生成中…');
    const r = await postJson('/api/draft-upload', { bvid: $('opBvid').value.trim() });
    showOp(r.text || r.message || '无返回');
  }

  async function doPrivate() {
    showOp('发送中…');
    const r = await postJson('/api/private-reply', {
      mid: parseInt($('privMid').value, 10) || 0,
      text: $('privText').value.trim()
    });
    showOp(r.text || r.message || '无返回');
  }

  // ---------------- 实时值守：立刻检查一次 ----------------
  async function doRealtimeNow() {
    const box = $('realtimeResult');
    const btn = $('btnRealtimeNow');
    box.classList.remove('hidden');
    box.textContent = '检查中…（读「回复我的」与私信收件箱，可能要十几秒）';
    if (btn) btn.disabled = true;
    try {
      const r = await postJson('/api/realtime/now');
      box.textContent = (r && r.text) || (r && r.message) || '（无输出）';
      msg('实时值守检查完成', 'ok');
    } catch (e) {
      box.textContent = '请求失败：' + e.message;
      msg('实时值守请求失败', 'err');
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  // ---------------- 本机项目下载测试：手动跑一次 ----------------
  async function doDevTask() {
    const box = $('devTaskResult');
    const btn = $('btnDevTask');
    const url = ($('devTaskUrl').value || '').trim();
    if (!url) { msg('请先填项目地址', 'warn'); return; }
    box.classList.remove('hidden');
    box.textContent = '下载中…（可能要一段时间；进度见右侧实时日志）';
    if (btn) btn.disabled = true;
    try {
      const r = await postJson('/api/devtask/run', { url, mode: $('devTaskMode').value });
      box.textContent = (r && r.text) || (r && r.message) || '（无输出）';
      msg('下载测试任务结束', 'ok');
    } catch (e) {
      box.textContent = '请求失败：' + e.message;
      msg('下载测试请求失败', 'err');
    } finally {
      if (btn) btn.disabled = false;
    }
  }

  // ---------------- 日报 ----------------
  async function loadReports() {
    const r = await api('/api/reports');
    const list = $('reportList');
    list.innerHTML = '';
    (r.reports || []).forEach(rep => {
      const li = document.createElement('li');
      li.textContent = rep.date;
      li.title = `${rep.time} · ${rep.size} 字节`;
      li.dataset.date = rep.date;
      li.onclick = () => openReport(rep.date);
      list.appendChild(li);
    });
    if (!list.children.length) list.innerHTML = '<li style="cursor:default;color:var(--text-mute)">还没有日报</li>';
  }

  async function openReport(date) {
    const r = await api('/api/report?date=' + encodeURIComponent(date));
    [...$('reportList').children].forEach(li => li.classList.toggle('active', li.dataset.date === date));
    $('reportBody').innerHTML = r.ok ? md(r.content) : '<p style="color:var(--text-mute)">这一天还没有日报。</p>';
  }

  // 极简 Markdown 渲染（够日报用：标题 / 引用 / 列表 / 链接 / 加粗 / 分隔线）
  function md(src) {
    const lines = escapeHtml(src).split(/\r?\n/);
    let out = '', inList = false;
    const inline = (s) => s
      .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
      .replace(/\[(.+?)\]\((https?:\/\/[^)]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
    const closeList = () => { if (inList) { out += '</ul>'; inList = false; } };
    for (const raw of lines) {
      const line = raw.replace(/\s+$/, '');
      if (/^###\s/.test(line)) { closeList(); out += `<h2>${inline(line.slice(4))}</h2>`; }
      else if (/^##\s/.test(line)) { closeList(); out += `<h2>${inline(line.slice(3))}</h2>`; }
      else if (/^#\s/.test(line)) { closeList(); out += `<h1>${inline(line.slice(2))}</h1>`; }
      else if (/^>\s?/.test(line)) { closeList(); out += `<blockquote>${inline(line.replace(/^>\s?/, ''))}</blockquote>`; }
      else if (/^---+$/.test(line)) { closeList(); out += '<hr>'; }
      else if (/^[-*]\s+/.test(line)) {
        if (!inList) { out += '<ul>'; inList = true; }
        out += `<li>${inline(line.replace(/^[-*]\s+/, ''))}</li>`;
      }
      else if (!line.trim()) { closeList(); }
      else { closeList(); out += `<p>${inline(line)}</p>`; }
    }
    closeList();
    return out;
  }

  // ---------------- 启动 ----------------
  async function refresh() {
    state = await api('/api/state');
    if (!state.ok) { msg(state.message || '读不到状态', 'err'); return; }
    if (!cfg) fillForm(state.config);
    renderState();
    loadCapability();
  }

  function init() {
    bindForm();
    document.querySelectorAll('[data-framepreset]').forEach(btn => {
      btn.onclick = () => applyFramePreset(btn.dataset.framepreset);
    });
    $('btnRun').onclick = runNow;
    $('btnStop').onclick = stopRound;
    $('btnQr').onclick = startQr;
    $('btnLogout').onclick = logout;
    $('btnVerify').onclick = verifyComments;
    $('btnOpenOut').onclick = () => openFolder('out');
    $('btnOpenPlugin').onclick = () => openFolder('');
    $('btnReloadReports').onclick = loadReports;
    $('btnTestTranscribe').onclick = testTranscribe;
    $('btnFetchAll').onclick = () => fetchTools('all', $('btnFetchAll'));
    $('btnFetchFfmpeg').onclick = () => fetchTools('ffmpeg', $('btnFetchFfmpeg'));
    $('btnFetchWhisper').onclick = () => fetchTools('whisper', $('btnFetchWhisper'));
    $('btnFetchModel').onclick = () => fetchTools('model', $('btnFetchModel'));
    $('btnInteract').onclick = doInteract;
    $('btnDynamic').onclick = doDynamic;
    $('btnDraft').onclick = doDraft;
    $('btnPrivate').onclick = doPrivate;
    $('btnRealtimeNow').onclick = doRealtimeNow;
    $('btnDevTask').onclick = doDevTask;
    window.addEventListener('pagehide', flushSave);
    document.addEventListener('visibilitychange', () => { if (document.hidden) flushSave(); });
    connectWs();
    refresh();
    loadReports();
    loadToolsPlan();
    setInterval(refresh, 4000);
  }

  document.addEventListener('DOMContentLoaded', init);
})();
