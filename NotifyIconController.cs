using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using ModernNotifyIcon.Theme;

namespace PowerDimmer
{
    public class NotifyIconController
    {
        internal Action? ExitClicked;
        internal Action? ReloadRequested;

        /// <summary>Supplies the last "real" foreground window (the menu itself has focus).</summary>
        internal Func<IntPtr>? GetTargetWindow;

        public NotifyIcon NotifyIcon;

        private readonly ISettings settings;

        // The menu is rebuilt from scratch when the UI language changes, so every
        // checkable item is remembered here and re-synced each time the menu opens.
        private ToolStripMenuItem? dimmingItem;
        private ToolStripMenuItem? dimTaskbarItem;
        private ToolStripMenuItem? undimOnDesktopItem;
        private ToolStripMenuItem? allMonitorsItem;
        private ToolStripMenuItem? activeOnLaunchItem;
        private ToolStripMenuItem? startWithWindowsItem;
        private TrackBarMenuItem? sliderRow;
        private readonly List<(ToolStripMenuItem Item, int Level)> presetItems = new();
        private readonly List<(ToolStripMenuItem Item, int Ms)> delayItems = new();
        private readonly List<(ToolStripMenuItem Item, int Ms)> fadeItems = new();
        private readonly List<(ToolStripMenuItem Item, double Radius)> spotlightRadiusItems = new();
        private ToolStripMenuItem? spotlightItem;
        private readonly List<(ToolStripMenuItem Item, string Color)> colorItems = new();
        private readonly List<(ToolStripMenuItem Item, string Language)> languageItems = new();
        private readonly List<(ToolStripMenuItem Item, string Mode)> filterModeItems = new();

        public NotifyIconController(ISettings settings)
        {
            this.settings = settings;

            NotifyIcon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)!,
                Text = Loc.T("Tray.Tooltip"),
                ContextMenuStrip = CreateStrip(),
                Visible = true
            };

            // The mask covers the taskbar, so the tray icon is both invisible and the
            // only way back in. Double-click is the shortest path to "pause / resume".
            NotifyIcon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    settings.DimmingEnabled = !settings.DimmingEnabled;
                }
            };
        }

        private ThemeReferencedContextMenuStrip CreateStrip()
        {
            var builder = new ContextMenuStripBuilder();
            BuildMenu(builder);
            var strip = builder.Build();
            strip.Opening += (_, _) =>
            {
                SyncMenuState();
                FitSliderRow(strip);
            };
            return strip;
        }

        private void FitSliderRow(ContextMenuStrip strip)
        {
            if (sliderRow == null)
            {
                return;
            }

            // Menu items stretch to the drop-down's content width; a ToolStripControlHost
            // keeps whatever width it was given, which is why the track used to stop short
            // of the right edge. Measure a real item instead of guessing: DisplayRectangle
            // does not include the check-mark / arrow gutters.
            var reference = strip.Items
                .OfType<ToolStripMenuItem>()
                .Where(i => i.Available && i.Bounds.Width > 0)
                .Select(i => i.Bounds.Width)
                .DefaultIfEmpty(0)
                .Max();
            var target = reference > 0 ? reference : strip.DisplayRectangle.Width;
            if (target > 0)
            {
                sliderRow.SetRowWidth(target);
                // Re-run the strip's layout so its hit-testing rectangles match the new
                // row width - otherwise clicks past the old width close the menu.
                strip.PerformLayout();
            }
        }

        internal void RefreshMenu() => RebuildMenu();

        private void RebuildMenu()
        {
            var old = NotifyIcon.ContextMenuStrip;
            NotifyIcon.ContextMenuStrip = CreateStrip();
            NotifyIcon.Text = Loc.T("Tray.Tooltip");

            if (old != null)
            {
                // This runs from inside a menu item click, so the old strip must not be
                // disposed until the click (and the menu closing) has finished.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => old.Dispose()));
            }
        }

        private void BuildMenu(ContextMenuStripBuilder builder)
        {
            dimmingItem = null;
            dimTaskbarItem = null;
            undimOnDesktopItem = null;
            allMonitorsItem = null;
            activeOnLaunchItem = null;
            startWithWindowsItem = null;
            sliderRow = null;
            presetItems.Clear();
            delayItems.Clear();
            fadeItems.Clear();
            spotlightRadiusItems.Clear();
            spotlightItem = null;
            colorItems.Clear();
            languageItems.Clear();
            filterModeItems.Clear();

            builder
                .AddToggle(option => option
                    .SetText(Loc.T("Menu.Dimming"))
                    .SetChecked(settings.DimmingEnabled)
                    .ConfigureItem(item =>
                    {
                        item.ShortcutKeyDisplayString = "CTRL+WIN+ALT+D";
                        dimmingItem = item;
                    })
                    .AddHandler(b => settings.DimmingEnabled = b))
                .AddToggle(option => option
                    .SetText(Loc.T("Menu.DimTaskbar"))
                    .SetChecked(settings.DimTaskbar)
                    .ConfigureItem(item => dimTaskbarItem = item)
                    .AddHandler(b => settings.DimTaskbar = b))
                .AddToggle(option => option
                    .SetText(Loc.T("Menu.UndimOnDesktop"))
                    .SetChecked(settings.UndimOnDesktop)
                    .ConfigureItem(item => undimOnDesktopItem = item)
                    .AddHandler(b => settings.UndimOnDesktop = b))
                .AddToggle(option => option
                    .SetText(Loc.T("Menu.AllMonitors"))
                    .SetChecked(settings.MultiMonitorDimming)
                    .ConfigureItem(item => allMonitorsItem = item)
                    .AddHandler(b => settings.MultiMonitorDimming = b))
                .AddToggle(option => option
                    .SetText(Loc.T("Menu.ActiveOnLaunch"))
                    .SetChecked(settings.ActiveOnLaunch)
                    .ConfigureItem(item => activeOnLaunchItem = item)
                    .AddHandler(b => settings.ActiveOnLaunch = b))
                .AddSeparator()
                .AddItem(sliderRow = new TrackBarMenuItem(settings))
                .AddSeparator();

            builder.AddSubmenu(Loc.T("Menu.Presets"), sub =>
            {
                AddPreset(sub, "Menu.Preset100", 100);
                AddPreset(sub, "Menu.Preset90", 90);
                AddPreset(sub, "Menu.Preset75", 75);
                AddPreset(sub, "Menu.Preset50", 50);
                AddPreset(sub, "Menu.Preset25", 25);
            });

            builder.AddSubmenu(Loc.T("Menu.SwitchDelay"), sub =>
            {
                AddDelay(sub, "Menu.DelayOff", 0);
                AddDelay(sub, "Menu.Delay200", 200);
                AddDelay(sub, "Menu.Delay300", 300);
                AddDelay(sub, "Menu.Delay500", 500);
                AddDelay(sub, "Menu.Delay1000", 1000);
                sub.AddSeparator();
                sub.AddButton(option => option
                    .SetText(CustomCaption(settings.SwitchDelayMs, delayItems.Select(x => x.Ms)))
                    .AddHandler(() => EditDuration(false)));
            });

            builder.AddSubmenu(Loc.T("Menu.FadeOut"), sub =>
            {
                AddFade(sub, "Menu.FadeOff", 0);
                AddFade(sub, "Menu.Fade150", 150);
                AddFade(sub, "Menu.Fade250", 250);
                AddFade(sub, "Menu.Fade400", 400);
                AddFade(sub, "Menu.Fade600", 600);
                sub.AddSeparator();
                sub.AddButton(option => option
                    .SetText(CustomCaption(settings.FadeOutMs, fadeItems.Select(x => x.Ms)))
                    .AddHandler(() => EditDuration(true)));
            });

            builder.AddSubmenu(Loc.T("Menu.Spotlight"), sub =>
            {
                sub.AddToggle(option => option
                    .SetText(Loc.T("Menu.SpotlightEnabled"))
                    .SetChecked(settings.SpotlightEnabled)
                    .ConfigureItem(item => spotlightItem = item)
                    .AddHandler(b => settings.SpotlightEnabled = b));

                sub.AddSubmenu(Loc.T("Menu.SpotlightRadius"), radius =>
                {
                    AddSpotlightRadius(radius, "Menu.SpotlightSmall", 100);
                    AddSpotlightRadius(radius, "Menu.SpotlightMedium", 140);
                    AddSpotlightRadius(radius, "Menu.SpotlightLarge", 200);
                    AddSpotlightRadius(radius, "Menu.SpotlightXl", 280);
                });
            });

            builder.AddSubmenu(Loc.T("Menu.MaskColor"), sub =>
            {
                AddColor(sub, "Menu.ColorBlack", "#000000");
                AddColor(sub, "Menu.ColorCharcoal", "#141414");
                AddColor(sub, "Menu.ColorInk", "#06101E");
            });

            builder.AddSubmenu(Loc.T("Menu.Filter"), sub =>
            {
                AddFilterMode(sub, "Menu.FilterOff", WindowFilter.Off);
                AddFilterMode(sub, "Menu.FilterExclude", WindowFilter.Exclude);
                AddFilterMode(sub, "Menu.FilterOnly", WindowFilter.Only);
                sub.AddSeparator();
                sub.AddButton(option => option
                    .SetText(Loc.T("Menu.FilterAddCurrent"))
                    .AddHandler(AddCurrentWindowToRules));

                var count = settings.FilterKeywords?.Count ?? 0;
                var editText = Loc.T("Menu.FilterEdit");
                if (count > 0)
                {
                    editText += "  (" + count + ")";
                }

                sub.AddButton(option => option
                    .SetText(editText)
                    .AddHandler(EditRules));
            });

            builder.AddSubmenu(Loc.T("Menu.SettingsSection"), sub =>
            {
                sub.AddSubmenu(Loc.T("Menu.Language"), lang =>
                {
                    AddLanguage(lang, "Menu.LanguageAuto", Loc.Auto);
                    AddLanguage(lang, "Menu.LanguageZh", Loc.Chinese);
                    AddLanguage(lang, "Menu.LanguageEn", Loc.English);
                });

                sub.AddToggle(option => option
                    .SetText(Loc.T("Menu.StartWithWindows"))
                    .SetChecked(StartupRegistration.IsEnabled())
                    .ConfigureItem(item => startWithWindowsItem = item)
                    .AddHandler(b =>
                    {
                        StartupRegistration.SetEnabled(b);
                        // Registry permissions can refuse the change - show the real state.
                        SyncMenuState();
                    }));

                sub.AddButton(option => option
                    .SetText(Loc.T("Menu.ReloadConfig"))
                    .AddHandler(() => ReloadRequested?.Invoke()));

                sub.AddButton(option => option
                    .SetText(Loc.T("Menu.OpenConfigFolder"))
                    .AddHandler(() => StartupRegistration.OpenConfigFolder((settings as Settings)?.FilePath)));

                sub.AddSeparator();
                sub.AddButton(option => option
                    .SetText(Loc.T("Menu.About"))
                    .AddHandler(ShowAbout));
            });

            builder
                .AddSeparator()
                .AddButton(option => option
                    .SetText(Loc.T("Menu.Exit"))
                    .AddHandler(() => ExitClicked?.Invoke()));
        }

        private void AddPreset(ContextMenuStripBuilder builder, string textKey, int level)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(settings.DimLevel == level)
                .ConfigureItem(item => presetItems.Add((item, level)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.DimLevel = level;
                    SyncMenuState();
                }));
        }

        private void AddDelay(ContextMenuStripBuilder builder, string textKey, int milliseconds)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(settings.SwitchDelayMs == milliseconds)
                .ConfigureItem(item => delayItems.Add((item, milliseconds)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.SwitchDelayMs = milliseconds;
                    SyncMenuState();
                }));
        }

        private void AddFade(ContextMenuStripBuilder builder, string textKey, int milliseconds)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(settings.FadeOutMs == milliseconds)
                .ConfigureItem(item => fadeItems.Add((item, milliseconds)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.FadeOutMs = milliseconds;
                    SyncMenuState();
                }));
        }

        private void AddSpotlightRadius(ContextMenuStripBuilder builder, string textKey, double radius)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(Math.Abs(settings.SpotlightRadius - radius) < 0.5)
                .ConfigureItem(item => spotlightRadiusItems.Add((item, radius)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.SpotlightRadius = radius;
                    SyncMenuState();
                }));
        }

        private void AddColor(ContextMenuStripBuilder builder, string textKey, string color)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(string.Equals(settings.MaskColor, color, StringComparison.OrdinalIgnoreCase))
                .ConfigureItem(item => colorItems.Add((item, color)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.MaskColor = color;
                    SyncMenuState();
                }));
        }

        private void AddLanguage(ContextMenuStripBuilder builder, string textKey, string language)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(Loc.Current == language)
                .ConfigureItem(item => languageItems.Add((item, language)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.Language = language;
                    Loc.Configure(language);
                    RebuildMenu();
                }));
        }

        private void AddFilterMode(ContextMenuStripBuilder builder, string textKey, string mode)
        {
            builder.AddToggle(option => option
                .SetText(Loc.T(textKey))
                .SetChecked(WindowFilter.NormalizeMode(settings.FilterMode) == mode)
                .ConfigureItem(item => filterModeItems.Add((item, mode)))
                .AddHandler(b =>
                {
                    if (!b)
                    {
                        return;
                    }

                    settings.FilterMode = mode;
                    SyncMenuState();
                }));
        }

        /// <summary>
        /// One-click rule from the window that was in front: stores the process name,
        /// which is the shortest useful form of a rule.
        /// </summary>
        private void AddCurrentWindowToRules()
        {
            var hwnd = GetTargetWindow?.Invoke() ?? IntPtr.Zero;
            var name = WindowFilter.Describe(hwnd);
            if (string.IsNullOrEmpty(name))
            {
                ShowMessage(Loc.T("Msg.RuleAddFailed"));
                return;
            }

            var list = new List<string>(settings.FilterKeywords) { name };
            settings.FilterKeywords = list;

            // Adding a rule while the filter is off would silently do nothing.
            if (WindowFilter.NormalizeMode(settings.FilterMode) == WindowFilter.Off)
            {
                settings.FilterMode = WindowFilter.Exclude;
            }

            ShowMessage(string.Format(Loc.T("Msg.RuleAdded"), name));
            RebuildMenu();
        }

        /// <summary>
        /// "自定义…" caption. When the value in effect is not one of the presets above it,
        /// the caption carries the actual number - otherwise nothing in the submenu would
        /// be ticked and the current setting would be invisible.
        /// </summary>
        private static string CustomCaption(int current, IEnumerable<int> presets)
        {
            var text = Loc.T("Menu.Custom");
            return presets.Contains(current) ? text : Loc.T("Menu.CustomCurrent", text, DurationInput.Describe(current));
        }

        private void EditDuration(bool fade)
        {
            // Deferred for the same reason as EditRules below: a modal window opened
            // straight from a menu click keeps the menu's message loop nested under it.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                var min = fade ? Settings.FadeOutMinMs : Settings.SwitchDelayMinMs;
                var max = fade ? Settings.FadeOutMaxMs : Settings.SwitchDelayMaxMs;
                var current = fade ? settings.FadeOutMs : settings.SwitchDelayMs;

                var dialog = new DurationDialog(
                    Loc.T(fade ? "Dlg.FadeTitle" : "Dlg.DelayTitle"), current, min, max);

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                if (fade)
                {
                    settings.FadeOutMs = dialog.Milliseconds;
                }
                else
                {
                    settings.SwitchDelayMs = dialog.Milliseconds;
                }

                // The caption shows the new value, so the menu has to be rebuilt.
                RebuildMenu();
            }));
        }

        private void EditRules()
        {
            // Deferred: opening a modal window from inside a menu click leaves the
            // menu's own message loop nested under it.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = new TextPromptDialog(
                    Loc.T("Dlg.FilterTitle"),
                    Loc.T("Dlg.FilterHint"),
                    string.Join(Environment.NewLine, settings.FilterKeywords));

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                settings.FilterKeywords = new List<string>(WindowFilter.SplitLines(dialog.Result));
                if (settings.FilterKeywords.Count > 0 &&
                    WindowFilter.NormalizeMode(settings.FilterMode) == WindowFilter.Off)
                {
                    settings.FilterMode = WindowFilter.Exclude;
                }

                RebuildMenu();
            }));
        }

        private void ShowAbout()
        {
            // Same reason as EditRules: a modal window opened straight from a menu click
            // keeps the menu's message loop nested underneath it.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = new AboutDialog((settings as Settings)?.FilePath);
                dialog.ShowDialog();
            }));
        }

        private static void ShowMessage(string text)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() => System.Windows.MessageBox.Show(text, "PowerDimmer")));
        }

        private void SyncMenuState()
        {
            if (dimmingItem != null) dimmingItem.Checked = settings.DimmingEnabled;
            if (dimTaskbarItem != null) dimTaskbarItem.Checked = settings.DimTaskbar;
            if (undimOnDesktopItem != null) undimOnDesktopItem.Checked = settings.UndimOnDesktop;
            if (allMonitorsItem != null) allMonitorsItem.Checked = settings.MultiMonitorDimming;
            if (activeOnLaunchItem != null) activeOnLaunchItem.Checked = settings.ActiveOnLaunch;
            if (startWithWindowsItem != null) startWithWindowsItem.Checked = StartupRegistration.IsEnabled();

            foreach (var (item, level) in presetItems)
            {
                item.Checked = settings.DimLevel == level;
            }

            foreach (var (item, ms) in delayItems)
            {
                item.Checked = settings.SwitchDelayMs == ms;
            }

            foreach (var (item, ms) in fadeItems)
            {
                item.Checked = settings.FadeOutMs == ms;
            }

            if (spotlightItem != null)
            {
                spotlightItem.Checked = settings.SpotlightEnabled;
            }

            foreach (var (item, radius) in spotlightRadiusItems)
            {
                item.Checked = Math.Abs(settings.SpotlightRadius - radius) < 0.5;
            }

            foreach (var (item, color) in colorItems)
            {
                item.Checked = string.Equals(settings.MaskColor, color, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var (item, language) in languageItems)
            {
                item.Checked = Loc.Current == language;
            }

            foreach (var (item, mode) in filterModeItems)
            {
                item.Checked = WindowFilter.NormalizeMode(settings.FilterMode) == mode;
            }
        }
    }

    // https://stackoverflow.com/questions/4339143/adding-a-trackbar-control-to-a-contextmenu
    public class TrackBarMenuItem : ToolStripControlHost
    {
        private const int MinimumWidth = 240;

        private readonly SliderControl slider;

        public TrackBarMenuItem(ISettings settings) : base(new ContainerControl())
        {
            // Everything below is measured by hand. WinForms' autoscaling is what
            // produced the original "Brightne" clipping: the label was sized in 96-DPI
            // units and then rendered with a much larger font at 175%.
            AutoSize = false;

            var host = (ContainerControl)Control;
            host.AutoScaleMode = AutoScaleMode.None;
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            host.BackColor = ThemeDictionary.ChromeMidium;

            // One control draws both the caption and the track, docked to fill: nothing
            // can end up wider than what the ToolStrip actually gives us.
            slider = new SliderControl
            {
                Parent = host,
                Dock = DockStyle.Fill,
                BackColor = host.BackColor,
                Value = Math.Clamp(settings.DimLevel, 0, 100),
                CaptionText = Format(settings.DimLevel)
            };

            // Measure the widest caption we can ever show (0% and 100% in the current
            // language) so the text can never be clipped again - regardless of DPI.
            var widest = Math.Max(
                TextRenderer.MeasureText(string.Format(Loc.T("Menu.DimLevelFormat"), 100), SystemFonts.MenuFont).Width,
                TextRenderer.MeasureText(string.Format(Loc.T("Menu.DimLevelFormat"), 0), SystemFonts.MenuFont).Width);

            var width = Math.Max(MinimumWidth, widest + 32);
            host.Size = new Size(width, SliderControl.TotalHeight);
            Size = host.Size;

            slider.ValueChanged += (_, _) =>
            {
                // 0 = no mask, 100 = solid black. This is the opposite of the old
                // "brightness" slider, which turned out to be backwards for a mask tool.
                settings.DimLevel = slider.Value;
                slider.CaptionText = Format(slider.Value);
            };
        }

        /// <summary>
        /// Stretches the row to the width the menu actually uses. Called after the menu
        /// opens, because only then is the final menu width known (it is driven by the
        /// widest item, which is the hotkey row, not this one).
        /// </summary>
        internal void SetRowWidth(int width)
        {
            if (width <= 0)
            {
                return;
            }

            var host = (ContainerControl)Control;
            var desiredHeight = SliderControl.TotalHeight;

            host.Size = new Size(width, desiredHeight);
            Size = host.Size;

            // The ToolStrip subtracts its margin again when it lays a control host out,
            // so a straight assignment leaves the painted area ~10px short - which is what
            // flattened the bottom of the thumb. Ask for the difference back.
            for (var i = 0; i < 3 && host.Height < desiredHeight; i++)
            {
                host.Size = new Size(width, desiredHeight + (desiredHeight - host.Height));
                Size = host.Size;
            }

            // The slider is docked, so let the container re-run layout instead of sizing
            // it by hand (Dock would overwrite a manual width anyway).
            host.PerformLayout();
            Invalidate();
        }

        private static string Format(int value) => string.Format(Loc.T("Menu.DimLevelFormat"), value);
    }
}
