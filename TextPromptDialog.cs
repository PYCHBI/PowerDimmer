using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerDimmer
{
    /// <summary>
    /// Minimal multi-line editor for the exception-rule list. The whole app has exactly
    /// one text dialog, so it is built in code instead of dragging in a XAML window.
    /// </summary>
    public sealed class TextPromptDialog : Window
    {
        private readonly TextBox box;

        public string Result { get; private set; } = string.Empty;

        public TextPromptDialog(string title, string hint, string initial)
        {
            Title = title;
            Width = 560;
            Height = 420;
            MinWidth = 440;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 14;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

            var root = new DockPanel { Margin = new Thickness(16) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var ok = new Button
            {
                Content = Loc.T("Dlg.Ok"),
                Width = 88,
                Height = 32,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            ok.Click += (_, _) =>
            {
                Result = box.Text;
                DialogResult = true;
            };

            var cancel = new Button
            {
                Content = Loc.T("Dlg.Cancel"),
                Width = 88,
                Height = 32,
                IsCancel = true
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var hintText = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(hintText, Dock.Top);
            root.Children.Add(hintText);

            box = new TextBox
            {
                Text = initial,
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
                Padding = new Thickness(6)
            };

            // Last child fills whatever space is left in the DockPanel.
            root.Children.Add(box);

            Content = root;

            Loaded += (_, _) =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            };
        }
    }
}
