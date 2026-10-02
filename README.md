# WhisperClock

Windows 桌面闹钟（C# / WinForms，.NET 6）。常驻系统托盘，用 Windows 原生 Toast 通知提醒；
支持贪睡、未操作自动贪睡、随机触发偏移、打开目标、开机自启、登录时触发，以及从文件夹随机选曲。

- **当前版本**：`0.2.4-beta`（原项目名 `AlarmClock`，0.2.4beta 起更名 WhisperClock，主界面标题 `Whisper`）
- **许可证**：[GPL-3.0](LICENSE)

> **内部文档不在本仓库内**：完整状态机说明、历史 bug 清单（防回归）、Toast 激活机制、调试方法，
> 以及 0.1.4 时期的旧版 README，都放在本地 `docs/` 目录（已写入 `.gitignore`，不随仓库分发）。
> 参与开发时请向维护者索取这些文件——尤其是旧版 README 里"手动注册 `AlarmClock.App` AUMID"
> 的做法**已被废弃**（会造成系统"通知"设置出现多余条目），请勿照做。

## 功能

| 功能 | 说明 |
| --- | --- |
| 系统通知 | `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 发送 Windows 原生 Toast，主/副标题可自定义 |
| 两段式状态机 | 触发 → **确认期 30 秒**（Toast 按钮可交互）→ 未操作则**自动贪睡** → 贪睡等待（默认 5 分钟，可调 1–60）→ 再次触发，**无限循环直到用户操作** |
| Toast 按钮 | `打开`（可选）/ `延迟 N 分钟` / `结束`，按钮文字均可在"默认模板…"中自定义 |
| 闹钟模式 | 普通（完整状态机）/ 仅通知（无按钮 Toast + 直接播/打开）/ 纯提醒（beta，连 Toast 都不弹） |
| 随机偏移 | 触发点 = 设定时间 ±N 秒随机，且限制在目标日当天（不跨日） |
| 打开目标 | 网址 / 文件 / 文件夹，触发时可由 Toast 按钮或主界面按钮用系统默认程序打开 |
| 铃声 | `.wav` 文件，或**文件夹**（递归含嵌套子目录，并解析 `.lnk` 快捷方式指向的目录/文件）随机选曲 |
| 单次闹钟 | 触发一次后从列表删除（纯提醒/仅通知模式下播完才删） |
| 登录时触发 | 开机/启动时触发一次，不按设定时间；含"隐形登录 → 锁屏"判定，进桌面后才响 |
| 开机自启 | 注册表 `HKCU\...\CurrentVersion\Run`，值名 `WhisperClock`，带 `--minimized` 直接隐藏到托盘 |
| 导出 / 导入 | 单个或全部闹钟导出为 `.json`，导入时重新生成 Id |
| 暗色模式 | 跟随系统深色/浅色主题（含暗色标题栏），切换时自动刷新 |
| 托盘驻留 | 点关闭是隐藏到托盘，不退出；托盘菜单可退出 |

## 目录结构

```
whisperClock/
├─ src/WhisperClock/          源码（.cs / .csproj / Assets\app.ico）
├─ tools/                     clean.bat（只清注册项）、uninstall.bat（卸载）
├─ samples/                   闹钟列表 JSON 样例与 AI 测试清单
├─ docs/                      本地内部文档（不推送，见上）
├─ dist/<版本>/               发布产物（dotnet publish 输出，不入库）
├─ archive/                   旧版手动版本归档（不入库，见下）
├─ LICENSE                    GPL-3.0
└─ README.md
```

`tools/` 下的两个脚本在**发布目录**（`dist/<版本>/`）里也各有一份，因为
`uninstall.bat` 依赖 `%~dp0` 定位并删除自己所在的程序目录；`tools/` 里的是权威源。

## 构建与运行

需要 [.NET SDK](https://dotnet.microsoft.com/download)（本机验证 10.0.303；目标框架仍是 `net6.0-windows10.0.17763.0`，net6.0 已 EOL，构建会有 `NETSDK1138` 警告）。

```powershell
# 构建（验证）
dotnet build src/WhisperClock/WhisperClock.csproj -c Release -v q --nologo

# 调试运行
dotnet run --project src/WhisperClock/WhisperClock.csproj
```

发布（框架依赖版，不内置运行时）：

```powershell
dotnet publish src/WhisperClock/WhisperClock.csproj -c Release -r win-x64 --self-contained false --nologo -o dist/0.2.4beta
```

发布后把 `tools/clean.bat`、`tools/uninstall.bat` 复制进发布目录即可分发。

## 数据文件

程序数据固定在 `%LocalAppData%\AlarmClock\`（目录名沿用旧项目名，**改名时特意保持不变以免丢用户数据**）：

| 文件 | 内容 |
| --- | --- |
| `alarms.json` | 闹钟列表 |
| `settings.json` | 全局设置（贪睡时长、音频延迟、默认模板文案、按钮文字） |
| `debug.log` | 调试日志（UTF-8 带 BOM） |

## 卸载 / 清理

```powershell
# 仅清理注册项（自启动 + Toast 激活 AUMID/CLSID 残留），保留数据与程序
dist\0.2.4beta\clean.bat

# 完整卸载：结束进程 → 清注册项 → 删用户数据 → 自删程序目录
dist\0.2.4beta\uninstall.bat
```

## 版本管理

本项目早期**没有版本控制**，靠手工复制目录管理版本：每个版本在 `source\` 和 `App\` 各放一份，
旧版本手工移到 `Versions\源码\` 和 `Versions\程序\`。这一做法现已废弃，改用 Git 提交 + tag 管理：

- 当前版本位于 `src/WhisperClock/`，发布产物在 `dist/<版本>/`（构建生成，不入库）。
- 发版流程：改 `src/WhisperClock/WhisperClock.csproj` 的 `<Version>` / `<FileVersion>` → `dotnet publish -o dist/<版本>` → `git commit` → `git tag v<版本>`。
- `0.1.0` ~ `0.2.4alpha` 的历史快照保留在本地 `archive/`（不入库）；从当前版本起用 tag 记录演进。

### 旧版归档（`archive/`，入库已忽略）

`archive/source/<版本>/`（21 个）与 `archive/binary/<版本>/`（25 个 + `AlarmClock.zip`）保留了
改名前的全部手工版本快照，**仅在本地保留，不推送到 GitHub**。原因：

1. **体积**：合计约 1.14 GB。每个版本都带一份 23.8 MB 的 `Microsoft.Windows.SDK.NET.dll`，
   `archive/source/*/.vs/` 还含 Visual Studio 的 `suo`/`dtbcache` 缓存。远超 GitHub 单仓库
   的建议体积（单文件硬上限 100 MB），而其中绝大多数内容是可以由源码重新构建出来的。
2. **重复**：手工版本是**整目录快照**而非增量，相邻版本的文件几乎逐字相同；直接入库等于把
   同一份源码提交几十遍，对 Git 而言是纯冗余。
3. **二进制不适合入库**：`archive/binary/` 全是 exe/dll/pdb 产物，且属于已废弃的手工流程。
4. **可回溯性已经保住**：归档目录本身完整保留在本地，随时可解开回滚；需要查某个历史版本时
   直接在 `archive/` 里翻，不必走网络。

> 因此**旧版本的源码不再推送到 GitHub**：`archive/`、`dist/`、`docs/`、`bin/`、`obj/` 均已写入
> [.gitignore](.gitignore)。仓库里只保留当前源码、样例、工具脚本与本 README。

## 已知问题

- `net6.0` 已 EOL，构建有 `NETSDK1138` 警告，待评估升级到 `net8.0-windows`。
- 每升一次版本，旧 exe 路径的 Toast AUMID 注册表项会累积，用 `tools/clean.bat` 清理。
- 随机偏移计划点、贪睡状态、待确认状态都是运行时状态，不写盘：重启程序后当日贪睡会丢失。
- 另有若干待修缺陷（铃声文件夹 `.lnk` 递归导致崩溃、纯提醒单次闹钟音频被截断、编辑时无法清除
  铃声等），明细记录在本地 `docs/待修问题清单.md`，修好后再随版本更新本文件。
