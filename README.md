# QLaunch

极简 Windows 启动器：应用启动、Everything 文件搜索、选中文本英译中，三件事共用一个 `Alt+Space`。
用 C# / .NET 10 WinForms 原生绘制界面。

## 功能

| 能力 | 说明 |
| --- | --- |
| 应用启动 | 扫描开始菜单（`.lnk` / `.appref-ms` / `.url`）与 UWP 应用，支持中文名、拼音首字母（`wx` → 微信）、前缀与子序列匹配；空输入时首页只显示**启动过的**应用（按次数排序，最多 18 个），没启动过就不上首页；从文件搜索启动的 exe 会自动取图标加入索引 |
| 文件搜索 | 调用 Everything SDK，结果与应用混排，显示大小与修改时间；`Ctrl+Enter` 在资源管理器中定位文件 |
| 英译中 | 本地 Bergamot 先出快译，云端（百度或微软）结果到达后替换；云端不可用时保留本地译文。原文以中文为主时跳过不译 |
| 划词取词 | 先走 UIA `TextPattern`，失败再模拟 `Ctrl+C` 读剪贴板，并在取词后还原原剪贴板内容 |

## 快捷键与托盘

| 操作 | 效果 |
| --- | --- |
| `Alt+Space` | 居中唤出／收起搜索框（搜索、翻译同一个入口），拖动窗口不记忆位置 |
| `↑` / `↓` | 选择结果；首页宫格左右按 6 个一格平移 |
| `Enter` | 打开应用／文件；译文行上按 `Enter` 复制译文 |
| `Ctrl+Enter` | 在资源管理器中选中该文件 |
| 左键单击译文行 | 在译文与原文之间切换显示（切换后副标题显示另一种语言） |
| `Esc` | 先关右键菜单，再收起窗口 |
| 右键 | 结果项菜单（打开、定位、复制译文／路径等）；译文行另有"显示原文／显示译文"与两种复制；常用应用可"从常用中移除"，只清启动计数，再次启动仍会出现 |
| 托盘图标 | 双击切换窗口；菜单含"显示搜索栏 (Alt+Space)"、"重建应用索引"、"开机自启"、"退出" |

窗口失焦即隐藏，不占任务栏。开机自启写的是 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下名为 `QLaunch` 的值，内容是当前 exe 的绝对路径加 `--minimized`（该参数程序并未处理，靠默认行为本来就是只挂托盘）；关掉开关会删掉这个值。

## 环境要求

- Windows 10/11 x64，**.NET 10 桌面运行时**（`Microsoft.WindowsDesktop.App 10.x`）
- 构建需要 .NET 10 **SDK**。`build.ps1` 会按 `PATH` → `%LOCALAPPDATA%\dotnet` → `C:\Program Files\dotnet` 顺序探测，并挑出真正装了 SDK 的那个（`PATH` 上的 `dotnet` 可能只是运行时，所以会逐个候选确认能否列出 SDK）
- **Everything**：需要装好并正在运行；只影响文件搜索，没装也能启动应用与翻译
- 项目自带 `models/enzh/`（约 50 MB Bergamot 模型）和 `libs/Everything64.dll`，`dotnet publish` 会一并拷进 `dist/`，无需另外下载

## 云端翻译（可选）

不配置也能用，只是只用本地 Bergamot。云端是模块化的，同一时间只启用一个服务商：

| 环境变量 | 必填 | 说明 |
| --- | --- | --- |
| `QLAUNCH_CLOUD_PROVIDER` | 否 | `baidu` 或 `microsoft`；留空时有百度凭据用百度，否则用微软 |
| `QLAUNCH_BAIDU_APPID` | 百度 | 百度翻译开放平台的 APP ID |
| `QLAUNCH_BAIDU_KEY` | 百度 | 百度翻译开放平台的密钥 |
| `QLAUNCH_CLOUD_KEY` | 微软 | 微软 Translator 订阅密钥 |
| `QLAUNCH_CLOUD_REGION` | 否 | 微软多区域资源的区域（如 `eastasia`）；单区域资源留空 |
| `QLAUNCH_CLOUD_ENDPOINT` | 否 | 微软默认 `https://api.cognitive.microsofttranslator.com`，走代理时改这里 |

```powershell
setx QLAUNCH_BAIDU_APPID "你的APPID"
setx QLAUNCH_BAIDU_KEY "你的密钥"
```

```powershell
setx QLAUNCH_CLOUD_PROVIDER "microsoft"
setx QLAUNCH_CLOUD_KEY "你的密钥"
setx QLAUNCH_CLOUD_REGION "eastasia"
```

- 密钥只从环境变量读取，不写 `settings.json`，也不会出现在日志里；`setx` 之后要新开程序（或重新登录）才会读到。
- 百度标准版单次上限 1000 字符、QPS 1：超过 900 字符的原文按句子拆段，段间串行并留 1.1 秒间隔；免费额度以控制台显示为准。
- 微软免费 F0 层每月 200 万字符；计费以源文本字符数算。
- 本地与云端同时发起：本地通常几十毫秒先出译文，云端 0.3~1 秒后替换；云端先回来就直接用云端。云端失败静默处理，界面保留本地译文，不会弹错。
- 门面单次上限 5000 字符（百度内部再拆段），超长原文只走本地（本地上限 20000 字符，超出部分截断并提示）。

## 一键构建

```
双击 build.cmd
```

脚本按顺序做三件事：

1. 关闭**本项目目录下**正在运行的 `QLaunch.exe`（构建会覆盖 exe，运行中会被文件锁挡住）。只结束 `Path` 落在本项目目录内的进程，别处的同名进程只提示不动手。
2. `dotnet publish -c Release -o dist`
3. 启动 `dist\QLaunch.exe`，`Alt+Space` 唤出

等价的手动命令：

```powershell
.\build.ps1                    # 构建并启动
.\build.ps1 -NoRun             # 构建后不启动（仍会先关掉运行中的实例）
.\build.ps1 -NoRun -SelfTest   # 构建后先无头自检，通过才谈得上启动
```

| 开关 | 作用 |
| --- | --- |
| `-NoRun` | 构建完就结束，不启动实例 |
| `-SelfTest` | 构建完调用 `--selftest`，打印并校验 `selftest.log`；翻译引擎没到 `ready`、或 `translate` 一行是空的，就报错退出 |

GitHub Actions（[.github/workflows/build.yml](.github/workflows/build.yml)）在推送到 `master` 和提交 PR 时，在 `windows-latest` 上跑 `.\build.ps1 -NoRun -SelfTest`，并把 `dist/` 作为构建产物 `QLaunch-win-x64` 上传。Runner 上没有 Everything 也没配云端密钥，自检只卡翻译引擎这一项。

产物在 `dist/`（约 75 MB，已 gitignore；`models/enzh` 占 50 MB）。项目把 `RuntimeIdentifier` 钉在 `win-x64` 且 `SelfContained=false`：不钉的话 publish 会把 Bergamot 的 7 份跨平台原生库（约 112 MB）全拷进产物，而自己带运行时又会把 .NET 桌面运行时也塞进去。

## 无头自检

程序带了 `--selftest`，跑完索引、Everything、翻译三条链路后把结果写进 `%APPDATA%\QLaunch\selftest.log` 并退出，不开窗口，适合没有桌面交互时验证一次构建是否可用。

```powershell
.\build.ps1 -NoRun -SelfTest   # 一键做法：构建完自动跑自检并校验结果
```

想手动跑也可以（GUI 程序不会阻塞终端，所以留足预热时间再读日志）：

```powershell
& .\dist\QLaunch.exe --selftest "hello world"
Start-Sleep -Seconds 30      # 翻译模型预热要等一会儿
Get-Content "$env:APPDATA\QLaunch\selftest.log"
```

期望看到 `apps=` 大于 0、`match 'wx' -> 微信` 之类的命中、`everything err=-`、`translator status=ready`，以及有内容的 `translate [Translated]: 你好`。配了云端还会多一行 `cloud=baidu enabled=True` 或 `cloud=microsoft/区域 enabled=True`，随后是 `cloud translate [Translated]: ...`；没配则是 `cloud=disabled enabled=False`。

> `--selftest` 会先抢单实例互斥再判断参数，所以跑它之前必须**先退出**正在运行的 QLaunch，否则它会静默退出、什么都不写。
> 长句可能译出空串（旧构建同样如此），自检只用短句判断链路是否通。

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| `--reindex` | 忽略缓存，强制重扫开始菜单 |
| `--show` | 启动后立刻弹出搜索框（默认只挂托盘） |
| `--selftest <英文句子>` | 无头自检，写 `selftest.log` 后退出 |

## 项目结构

```
QLaunch.csproj      项目文件：WinForms + BergamotTranslatorSharp，补 UIAutomation 引用，钉 win-x64 不自带运行时
Program.cs          入口：单实例互斥、参数分流、索引加载、可选自检
src/AppIndex.cs     开始菜单与 UWP 扫描、图标抽取、apps.json 缓存
src/Matcher.cs      应用打分（完全 > 前缀 > 拼音 > 分词 > 包含 > 子序列）
src/Pinyin.cs       GB2312 锚点法取拼音首字母
src/EverythingSearch.cs  Everything SDK P/Invoke 与降级提示
src/Translator.cs   Bergamot 离线英译中、后台预热
src/CloudTranslator.cs  云端翻译门面：按环境变量选服务商，统一长度与中文原文拦截
src/ICloudTranslationProvider.cs  云端服务商接口（Enabled / Description / MaxInput / TranslateAsync）
src/BaiduCloudTranslator.cs  百度通用文本翻译（MD5 签名，长文拆段串行）
src/MicrosoftCloudTranslator.cs  微软 Translator 云端英译中（失败静默）
src/SelectionCapture.cs / UiaSelection.cs  划词取词
src/LauncherForm.cs 无边框置顶宿主窗口、全局热键、托盘、结果分发
src/Native.cs       user32 / imm32 声明（热键、SendInput、前台窗口、关闭 IME）
src/AppSettings.cs / Startup.cs / Models.cs  设置持久化、开机自启、DTO
libs/Everything64.dll    Everything SDK，构建时拷到产物根目录
models/enzh/             Bergamot 英译中模型（config.txt + 权重 + 词表）
build.ps1 / build.cmd    一键构建：关旧实例 → publish →（可选无头自检）→ 启动
.github/workflows/       build.yml（推送/PR 构建自检）、release.yml（tag 触发发布 zip）
LICENSE / THIRD-PARTY-NOTICES.md   MIT 许可与第三方组件（MPL-2.0、Everything SDK）声明
```

## 数据目录

运行期数据都写在 `%APPDATA%\QLaunch\`，卸载只需删这个目录：

| 文件／目录 | 内容 |
| --- | --- |
| `settings.json` | `maxResults`(20)、`fileResults`(12)、`launchAtStartup`、各应用启动次数 |
| `apps.json` | 应用索引缓存 |
| `portable.json` | 从文件搜索启动过的便携 exe 索引；右键移除时一并删除 |
| `icons/` | 32px 应用图标 PNG |
| `selftest.log` | `--selftest` 输出 |

## 常见问题

- **唤不出来**：`Alt+Space` 可能被别的启动器（ZTools、输入法等）占了，程序会在界面提示，并退回用托盘图标唤出。
- **文件搜索没结果**：需要 Everything 正在运行；未运行时提示"Everything 未运行，请先启动 Everything"，缺 `Everything64.dll` 则提示"未找到 Everything64.dll"。
- **翻译不可用**：提示"翻译引擎不可用：缺少翻译模型配置"说明产物里 `models/enzh/config.txt` 没跟着拷过去；`dist/` 是 `dotnet publish` 的输出，模型是 `Content Include="models\**"` 带过去的。
- **云端译文没出现**：`selftest.log` 里 `cloud=disabled` 表示当前进程读不到凭据（`setx` 后要重开程序）；百度看 `QLAUNCH_BAIDU_APPID` / `QLAUNCH_BAIDU_KEY`，微软看 `QLAUNCH_CLOUD_KEY`。`cloud translate` 报 HTTP 401 一般是微软密钥或区域不匹配，多区域资源的 Key 必须同时给 `QLAUNCH_CLOUD_REGION`；百度报 `error_code` 时优先查 54001（签名错误）与 54003（QPS 超限）。云端失败不会弹窗，界面会一直显示本地译文。
- **重复双击没反应**：单实例互斥 `QLaunch.SingleInstance` 会让第二次启动静默退出，先确认托盘里是否已经有一个。
- **从管理员窗口划词取不到字**：UIPI 会挡住低权限进程向提权窗口发 `Ctrl+C`，属系统限制。
- **开机自启弹警告**：受限组策略下写 `HKCU\...\Run` 可能被拦，可改为把快捷方式放进启动文件夹（`shell:startup`）。

## 发布

推送任意 tag 会触发 [release.yml](.github/workflows/release.yml)：在 `windows-latest` 上构建并自检，通过后把 `dist/` 压成 `QLaunch-<tag>-win-x64.zip` 挂到同名 Release。包是框架依赖版，目标机器未装 .NET 10 桌面运行时时，Windows 会提示去安装。

已存在的 tag 需要补资产时，手动触发即可：

```powershell
gh workflow run release.yml -f tag=0.1
```

## 许可证

本项目代码以 [MIT](LICENSE) 授权。BergamotTranslatorSharp、Mozilla 英译中模型（MPL-2.0）和 Everything SDK 等第三方文件的许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
