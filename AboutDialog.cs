using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerDimmer
{
    /// <summary>
    /// The "详情 / About" window: version, build time, runtime, config location and links.
    /// Built in code like TextPromptDialog - the app deliberately has no extra XAML window.
    /// </summary>
    public sealed class AboutDialog : Window
    {
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));
        private static readonly Brush ValueBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x63, 0xC2));
        private static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");
        private static readonly FontFamily MonoFont = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI");

        public AboutDialog(string? configPath)
        {
            Title = Loc.T("About.Title");
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = Brushes.White;
            FontFamily = UiFont;
            FontSize = 13.5;

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

            // ---- header ----
            root.Children.Add(new TextBlock
            {
                Text = AppInfo.Name,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = ValueBrush
            });

            root.Children.Add(new TextBlock
            {
                Text = Loc.T("About.Subtitle"),
                Foreground = LabelBrush,
                Margin = new Thickness(1, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            root.Children.Add(new TextBlock
            {
                Text = $"v{AppInfo.Version}   ·   {Loc.T("About.Built")} {AppInfo.BuildTime}",
                Foreground = AccentBrush,
                Margin = new Thickness(1, 8, 0, 0),
                FontSize = 14
            });

            // ---- divider ----
            root.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3)),
                Margin = new Thickness(0, 16, 0, 14)
            });

            // ---- detail rows ----
            var rows = new Grid();
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var index = 0;
            AddRow(rows, ref index, Loc.T("About.Version"), $"v{AppInfo.Version}");
            AddRow(rows, ref index, Loc.T("About.Runtime"), AppInfo.Runtime);
            AddRow(rows, ref index, Loc.T("About.Config"),
                string.IsNullOrWhiteSpace(configPath) ? Loc.T("Hint.NoConfig") : configPath!, mono: true);
            AddRow(rows, ref index, Loc.T("About.Project"), AppInfo.ProjectUrl);
            AddRow(rows, ref index, Loc.T("About.Upstream"), AppInfo.UpstreamUrl);
            AddRow(rows, ref index, Loc.T("About.License"), AppInfo.License);

            root.Children.Add(rows);

            // ---- buttons ----
            var buttons = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copy = MakeButton(Loc.T("About.Copy"), 136);
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(AppInfo.Describe());
                    copy.Content = Loc.T("About.Copied");
                }
                catch
                {
                    // Clipboard can be locked by another process - not worth a dialog.
                }
            };
            buttons.Children.Add(copy);

            var homepage = MakeButton(Loc.T("About.OpenProject"), 132);
            homepage.Margin = new Thickness(0, 0, 8, 0);
            homepage.Click += (_, _) => AppInfo.OpenUrl(AppInfo.ProjectUrl);
            Grid.SetColumn(homepage, 2);
            buttons.Children.Add(homepage);

            var close = MakeButton(Loc.T("About.Close"), 88);
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (_, _) => Close();
            Grid.SetColumn(close, 3);
            buttons.Children.Add(close);

            root.Children.Add(buttons);

            Content = root;
        }

        private static void AddRow(Grid grid, ref int index, string label, string value, bool mono = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = label,
                Foreground = LabelBrush,
                Margin = new Thickness(0, 0, 18, 8),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(caption, index);
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);

            var text = new TextBlock
            {
                Text = value,
                Foreground = ValueBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                FontFamily = mono ? MonoFont : UiFont,
                FontSize = mono ? 12.5 : 13.5
            };
            Grid.SetRow(text, index);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            index++;
        }

        private static Button MakeButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 32,
                FontFamily = UiFont,
                FontSize = 13.5
            };
        }
    }
}
