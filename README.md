# presents.BiliScroll · B站刷视频

让 AI 桌宠每天替你刷一轮 B站：按关键词搜视频，读字幕、弹幕与评论区（含楼中楼），把知识要点以「她自己」的第一人称写进长期记忆，产出一份 Markdown 日报；特别值得的可以留一条评论，也可以按人设点赞 / 收藏。

评论安全默认「防注入·防污染」——判定与分析走不写进角色记忆的临时通道，评论区文字一律当素材不当指令。针对 DeepSeek 机器人刷屏，内置「忽略 AI 评论（大肥鱼）」开关：按昵称 / 签名 / 等级 / 官方认证 / 大会员 / 头像蓝色占比 + 正文口癖给账号打分，命中就整条忽略（只对卡在临界区的账号才额外下一张 48×48 头像，约 4~5KB；规则移植自 liyupi/dafeiyu-cleaner）。

另有：自主觅食、本地设置 / 日报控制台（扫码登录、峰谷定时）、无字幕视频本地转写（ffmpeg + whisper）、PPT / 图表类视频抽帧识图、私信处理、实时值守。

## 安装

解压后目录名必须与插件 id 一致（`presents.BiliScroll`），放到 Alife 的 `Storage/Plugins/` 下，重启后由客户端自行编译加载。

## 版本

当前版本 **5.5.0**，发布包见 [Releases](https://github.com/presents0721/presents.BiliScroll/releases)。

插件大版本与 Alife 大版本对应：5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x（两条线功能一致，底层接口按版本适配）。

## 源码

仓库结构即插件目录结构：`manifest.json` + `*.cs` + `Resources/`。运行时产生的 `out/`、`state.json` 等不进仓库（见 `.gitignore`）。
