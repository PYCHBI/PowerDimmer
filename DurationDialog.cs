using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerDimmer
{
    /// <summary>
    /// Single-value editor for the two timings in the tray menu. Same style as
    /// TextPromptDialog / AboutDialog: built in code, no XAML.
    ///
    /// The value is validated on every keystroke, so the OK button is simply disabled
    /// while the text cannot be understood - no modal error box to dismiss.
    /// </summary>
    public sealed class DurationDialog : Window
    {
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        private static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");

        private readonly TextBox box;
        private readonly TextBlock status;
        private readonly Button ok;
        private readonly int minMs;
        private readonly int maxMs;

        /// <summary>Parsed value; only meaningful when ShowDialog() returned true.</summary>
        public int Milliseconds { get; private set; }

        public DurationDialog(string title, int currentMs, int minMs, int maxMs)
        {
            this.minMs = minMs;
            this.maxMs = maxMs;

            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = Brushes.White;
            FontFamily = UiFont;
            FontSize = 13.5;

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };

            root.Children.Add(new TextBlock
            {
                Text = Loc.T("Dlg.DurationHint"),
                Foreground = LabelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            box = new TextBox
            {
                // Pre-filled with the value that is in effect right now, in seconds, so
                // "0.35" is easy to tweak without retyping a unit.
                Text = (currentMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 15
            };
            box.TextChanged += (_, _) => Validate();
            root.Children.Add(box);

            status = new TextBlock
            {
                Margin = new Thickness(1, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(status);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            ok = new Button
            {
                Content = Loc.T("Dlg.Ok"),
                Width = 88,
                Height = 32,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0),
                FontFamily = UiFont,
                FontSize = 13.5
            };
            ok.Click += (_, _) =>
            {
                if (!DurationInput.TryParse(box.Text, this.minMs, this.maxMs, out var ms))
                {
                    return;
                }

                Milliseconds = ms;
                DialogResult = true;
            };

            var cancel = new Button
            {
                Content = Loc.T("Dlg.Cancel"),
                Width = 88,
                Height = 32,
                IsCancel = true,
                FontFamily = UiFont,
                FontSize = 13.5
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
                Validate();
            };
        }

        private void Validate()
        {
            if (DurationInput.TryParse(box.Text, minMs, maxMs, out var ms))
            {
                status.Foreground = LabelBrush;
                status.Text = Loc.T("Dlg.DurationPreview", ms);
                ok.IsEnabled = true;
                return;
            }

            status.Foreground = ErrorBrush;
            status.Text = Loc.T("Dlg.DurationInvalid", minMs, maxMs);
            ok.IsEnabled = false;
        }
    }
}
