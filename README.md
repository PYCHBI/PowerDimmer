# PowerDimmer（PYCHBI fork）

录屏 / 演示用的屏幕遮罩：只让当前窗口保持清晰，其余部分全部压暗。

> **注意：这是一个「fork 的 fork」，一共三代，别把前两代的说明当成本仓库的说明。**
>
> | 代 | 仓库 | 做了什么 |
> |---|---|---|
> | 第一代 | [shayne/PowerDimmer](https://github.com/shayne/PowerDimmer) | 原版，.NET 6，基础遮罩 + 区域遮罩 + 快捷键 |
> | 第二代 | 一个未具名的 fork（本仓库的基线） | 修混合 DPI 多显示器、加「点击桌面取消」「只遮主屏」开关、升 .NET 8 |
> | **第三代** | **本仓库 PYCHBI/PowerDimmer** | 修便携版启动崩溃、汉化、程序例外名单、重做滑块、加版本号与关于页 |
>
> 本文档从上往下依次是：**第三代改动 → 下载 / 升级 / 构建 → 第二代原文 → 第一代原文**。

## 第三代：本仓库改了什么

- **修复便携版启动即崩**：基线版本用 Config.Net 保存配置，落盘走 `%TEMP%` 再 `File.Replace`，当临时目录和程序目录不在同一个卷上（例如 TEMP 指向内存盘、程序放在 D 盘）时会抛 Win32 错误 1177 直接退出。现在改为在程序目录写 `settings.json.new` 再原子 `File.Move` 覆盖，只读时回退到 `%APPDATA%\PowerDimmer\`，任何 IO 异常都不会让进程退出。
- **去掉 Config.Net 依赖**，配置读写自己实现，写盘 400ms 防抖，退出时强制 Flush。
- **界面汉化**：托盘菜单、对话框全部中英双语，跟随系统语言，也可在设置里强制 `zh` / `en`。
- **程序例外名单**：按进程名 / exe 全路径 / 窗口标题子串匹配，支持 `off` / `exclude` / `only` 三种模式，名单为空时按 `off` 处理。
- **重做亮度滑块**：菜单里的滑条改成自绘控件，修复 175% 缩放下文案被截断、圆点底部被裁切、轨道不到菜单右边缘的问题。
- **默认不压暗任务栏**（基线默认压暗），需要时在菜单里勾选。
- **版本号与详情页**：版本号唯一来源是 `PowerDimmer.csproj` 里的 `<Version>`，会同时出现在三处 —— 托盘菜单 `设置 → 关于 PowerDimmer`（显示版本、运行环境、程序位置、配置文件位置、项目地址，可一键复制版本信息）、exe 的「属性 → 详细信息」页、以及发布包的名称里。
- 异常写 `crash.log`，方便定位。

## 下载

到 [Releases](https://github.com/PYCHBI/PowerDimmer/releases) 下载：

- `PowerDimmer-portable.zip` — 自包含，不需要装 .NET，解压即用（推荐）
- `PowerDimmer-requires-dotnet8.zip` — 体积小，需要装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

每次 push 到 `main` 会自动构建并更新 release；版本号取自 csproj，改版本只改这一处。

## 升级（便携版怎么换新版本）

直接解压新包、覆盖到旧目录即可，**配置不会丢**。原因有三条：

1. 发布包由 CI 从干净检出构建，`dotnet publish` 的输出只有程序文件（exe / dll / deps.json / runtimeconfig.json / pdb），**包里从来没有 `settings.json`**。构建流程里还有一步校验，一旦发现 `settings.json` 或 `crash.log` 混进包就直接让构建失败。
2. `settings.json` 是**运行时才生成**的：程序第一次启动、且真的要写入用户设置时才会在 exe 同目录创建它。装完没运行过就不会有这个文件。
3. 它是"同目录追加"，不在发布包的覆盖名单里 —— 覆盖只会改到程序文件，碰不到它。如果你的 exe 所在目录不可写、配置自动回退到了 `%APPDATA%\PowerDimmer\`，那就更不可能被覆盖。

保险起见也可以覆盖前把 `settings.json` 复制一份出去，出问题再拷回来。

## 便携版构建

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```

输出目录 `dist/` 即为便携版，配置写在 exe 同级的 `settings.json`。需要完全免装 .NET 运行时就用 `--self-contained true`。

---

## 第二代 fork 的改动（本仓库的基线，非本仓库所为）

> 以下是本仓库 fork 时继承的上游说明，原文保留以便追溯。
> **这一段里的 "this fork" 指的是第二代 fork，不是 PYCHBI fork。**

This is a fork of [shayne/PowerDimmer](https://github.com/shayne/PowerDimmer). The original had a bug where dimming didn't work on external monitors in mixed-DPI setups ([issue #4](https://github.com/shayne/PowerDimmer/issues/4)), and I wanted a couple of extra features. No commits have been made to the original repo in 2+ years, so I fixed and extended it. Just did this for personal use. Not tested on any PCs except my own and haven't checked if the changes affect resource usage compared to the original, so YMMV.

### Changes from the original

- Fixed multi-monitor dimming on mixed-DPI setups (uses Win32 pixel coordinates instead of WPF DIPs)
- Added "Undim on desktop click" option — click the desktop wallpaper to temporarily remove all dimming
- Added "Dim all monitors?" toggle — optionally limit dimming to the primary monitor only
- Upgraded from .NET 6 to .NET 8 (LTS)

---

## 原版 README（第一代，shayne/PowerDimmer）

### Features

* Dims all but currently focused window
* Toggle dimming for a specific window via `Win + Shift + D`
* Adjust brightness level from the system tray context menu
* Toggle shade for a specific window via `Win + Alt + S`, usefull for that one bright screen without darkmode
* Shade an area of a window via `Win + Alt + A` then select the area to shade. The shade will move with the window

| [<img src="https://user-images.githubusercontent.com/79330/147771591-853256ae-f4f1-42d3-8c68-ea467febeb58.png" width="800" />](https://user-images.githubusercontent.com/79330/147771591-853256ae-f4f1-42d3-8c68-ea467febeb58.png) |
| :--: |
| *Dim everything but focused window* |

| [<img src="https://user-images.githubusercontent.com/79330/147770555-5efe9efc-88e1-438e-a559-47b5f495976b.png" width="800" />](https://user-images.githubusercontent.com/79330/147770555-5efe9efc-88e1-438e-a559-47b5f495976b.png) |
| :--: |
| *Multiple focused windows via dimming toggle hotkey* |

## Building

Just install the [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) and then `dotnet run`
