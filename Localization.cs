using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerDimmer
{
    /// <summary>
    /// Tiny hand-rolled localization layer. The app only has a tray menu and a few
    /// messages, so a .resx pipeline (and the satellite assemblies it drags along)
    /// would be overkill for a portable single-folder tool.
    ///
    /// Language resolution order:
    ///   1. settings.json "language": "zh" / "en"  -> force that language
    ///   2. settings.json "language": "auto"       -> follow the Windows UI language
    /// </summary>
    public static class Loc
    {
        public const string Auto = "auto";
        public const string Chinese = "zh";
        public const string English = "en";

        private static readonly Dictionary<string, (string Zh, string En)> Table = new()
        {
            // ---- tray menu ----
            ["Menu.Dimming"] = ("启用遮罩", "Dimming active?"),
            ["Menu.DimTaskbar"] = ("同时压暗任务栏", "Dim taskbar?"),
            ["Menu.UndimOnDesktop"] = ("点击桌面时暂时取消", "Undim on desktop click?"),
            ["Menu.AllMonitors"] = ("遮罩所有显示器", "Dim all monitors?"),
            ["Menu.ActiveOnLaunch"] = ("启动后立即生效", "Active on launch?"),

            ["Menu.DimLevelFormat"] = ("遮罩浓度  {0}%", "Dim level  {0}%"),
            ["Menu.Presets"] = ("浓度预设", "Presets"),
            ["Menu.Preset100"] = ("100%  纯黑", "100%  Solid black"),
            ["Menu.Preset90"] = ("90%", "90%"),
            ["Menu.Preset75"] = ("75%", "75%"),
            ["Menu.Preset50"] = ("50%", "50%"),
            ["Menu.Preset25"] = ("25%  轻压暗", "25%  Light"),

            ["Menu.MaskColor"] = ("遮罩颜色", "Mask color"),
            ["Menu.ColorBlack"] = ("纯黑", "Black"),
            ["Menu.ColorCharcoal"] = ("深灰", "Charcoal"),
            ["Menu.ColorInk"] = ("墨蓝", "Ink blue"),

            ["Menu.SettingsSection"] = ("设置", "Settings"),
            ["Menu.Language"] = ("界面语言", "Language"),
            ["Menu.LanguageAuto"] = ("跟随系统", "Follow system"),
            ["Menu.LanguageZh"] = ("简体中文", "简体中文"),
            ["Menu.LanguageEn"] = ("English", "English"),
            ["Menu.StartWithWindows"] = ("开机自动启动", "Start with Windows"),
            ["Menu.OpenConfigFolder"] = ("打开配置文件位置", "Open config folder"),
            ["Menu.ReloadConfig"] = ("重新加载配置文件", "Reload config file"),

            ["Menu.Filter"] = ("程序例外", "App exceptions"),
            ["Menu.FilterOff"] = ("不启用", "Disabled"),
            ["Menu.FilterExclude"] = ("不遮匹配的窗口", "Skip matching windows"),
            ["Menu.FilterOnly"] = ("只遮匹配的窗口", "Only matching windows"),
            ["Menu.FilterAddCurrent"] = ("把当前窗口加进规则", "Add current window"),
            ["Menu.FilterEdit"] = ("编辑规则列表…", "Edit rules..."),
            ["Menu.FilterNone"] = ("（还没有规则）", "(no rules yet)"),

            ["Dlg.Ok"] = ("确定", "OK"),
            ["Dlg.Cancel"] = ("取消", "Cancel"),
            ["Dlg.FilterTitle"] = ("程序例外规则", "App exception rules"),
            ["Dlg.FilterHint"] = ("每行一条。可以填进程名（如 notepad）、exe 路径的一部分（如 WeChat），或窗口标题里的关键字（如 微信）。不区分大小写；三种会同时比对。",
                                  "One rule per line: a process name (e.g. notepad), part of the exe path (e.g. WeChat), or a window-title keyword. Case-insensitive; all three are checked."),

            ["Msg.Reloaded"] = ("配置已重新加载。", "Configuration reloaded."),
            ["Msg.ReloadFailed"] = ("读取配置失败：文件不存在或不是合法 JSON，已保持当前设置。",
                                   "Could not reload: the file is missing or not valid JSON. Current settings kept."),
            ["Msg.RuleAdded"] = ("已加入规则：{0}", "Rule added: {0}"),
            ["Msg.RuleAddFailed"] = ("没能识别当前窗口，请手动编辑规则。",
                                     "Could not identify the current window - edit the rules manually."),

            ["Menu.Exit"] = ("退出", "E&xit"),
            ["Menu.About"] = ("关于 PowerDimmer…", "About PowerDimmer..."),

            // ---- about / details window ----
            ["About.Title"] = ("关于 PowerDimmer", "About PowerDimmer"),
            ["About.Subtitle"] = ("录屏 / 演示用的屏幕遮罩：只留下当前窗口，其余压暗",
                                  "A screen mask for recording and demos: keep one window, dim the rest"),
            ["About.Version"] = ("版本", "Version"),
            ["About.Exe"] = ("程序位置", "Program"),
            ["About.Runtime"] = ("运行环境", "Runtime"),
            ["About.Config"] = ("配置文件", "Config file"),
            ["About.Project"] = ("项目主页", "Project page"),
            ["About.Upstream"] = ("原项目", "Upstream"),
            ["About.License"] = ("许可", "License"),
            ["About.Copy"] = ("复制版本信息", "Copy version info"),
            ["About.Copied"] = ("已复制", "Copied"),
            ["About.OpenProject"] = ("打开项目主页", "Open project page"),
            ["About.Close"] = ("关闭", "Close"),

            // ---- tray tooltip ----
            ["Tray.Tooltip"] = ("PowerDimmer - 双击暂停/恢复", "PowerDimmer - double click to pause"),

            // ---- misc ----
            ["Hint.NoConfig"] = ("还没有生成配置文件", "No config file yet"),
            ["Hint.Hotkeys"] = ("Ctrl+Win+Alt+D 开关遮罩 · Win+Shift+D 钉住窗口 · Win+Alt+S 遮住窗口 · Win+Alt+A 自选区域",
                                "Ctrl+Win+Alt+D toggle · Win+Shift+D pin · Win+Alt+S shade window · Win+Alt+A custom area"),
        };

        public static string Current { get; private set; } = Auto;

        public static bool IsChinese { get; private set; }

        public static void Configure(string? settingValue)
        {
            Current = Normalize(settingValue);
            IsChinese = Current switch
            {
                Chinese => true,
                English => false,
                _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                        .Equals("zh", StringComparison.OrdinalIgnoreCase)
            };
        }

        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Auto;
            }

            var v = value.Trim().ToLowerInvariant();
            return v switch
            {
                "zh" or "zh-cn" or "zh-hans" or "chinese" or "cn" => Chinese,
                "en" or "en-us" or "english" => English,
                _ => Auto
            };
        }

        public static string T(string key)
        {
            if (!Table.TryGetValue(key, out var entry))
            {
                return key;
            }

            return IsChinese ? entry.Zh : entry.En;
        }

        public static string T(string key, params object[] args)
        {
            try
            {
                return string.Format(T(key), args);
            }
            catch (FormatException)
            {
                return T(key);
            }
        }
    }
}
