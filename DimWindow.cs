using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PowerDimmer
{
    public partial class DimWindow : Window
    {
        public IntPtr Handle;
        private readonly System.Drawing.Rectangle _screenBounds;

        // The mask is painted by this rectangle instead of Window.Background so an
        // OpacityMask can punch the cursor spotlight hole into it.
        private readonly Rectangle _surface;

        // Built once and then only mutated while the mouse moves: rebuilding brushes
        // every frame would throw away WPF's cached composition resources.
        private RadialGradientBrush? _hole;

        public DimWindow(ISettings settings, System.Drawing.Rectangle screenBounds, Brush? maskBrush = null)
        {
            _screenBounds = screenBounds;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;

            // A mask that steals the foreground would break the whole mechanism: the
            // "focused window" would be the mask itself, so nothing would ever undim.
            ShowActivated = false;
            Focusable = false;

            _surface = new Rectangle { Fill = maskBrush ?? Brushes.Black };

            // Grid stretches its child, so the rectangle always covers the whole window.
            Content = new Grid { Children = { _surface } };

            Width = 1; Height = 1;
        }

        /// <summary>Screen rectangle this mask covers, in physical pixels.</summary>
        public System.Drawing.Rectangle ScreenBounds => _screenBounds;

        /// <summary>
        /// Puts a soft hole in the mask centred on the given screen position (physical
        /// pixels). The radius is in DIPs so it keeps a constant visual size on every
        /// monitor, whatever the scaling is.
        /// </summary>
        public void SetSpotlight(double screenX, double screenY, double radiusDip)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0 || _screenBounds.Width <= 0 || _screenBounds.Height <= 0)
            {
                return;
            }

            // The window is positioned in physical pixels but laid out in DIPs, so convert.
            var dipPerPixelX = width / _screenBounds.Width;
            var dipPerPixelY = height / _screenBounds.Height;

            var center = new Point(
                (screenX - _screenBounds.Left) * dipPerPixelX,
                (screenY - _screenBounds.Top) * dipPerPixelY);

            _hole ??= CreateHoleBrush();
            _hole.Center = center;
            _hole.GradientOrigin = center;
            _hole.RadiusX = Math.Max(1, radiusDip);
            _hole.RadiusY = Math.Max(1, radiusDip);

            if (!ReferenceEquals(_surface.OpacityMask, _hole))
            {
                _surface.OpacityMask = _hole;
            }
        }

        /// <summary>
        /// Opacity of the painted surface (0..1), independent of Window.Opacity.
        /// The fade-out overlay starts here at 0 and animates up to the dim level.
        /// </summary>
        public void SetSurfaceOpacity(double value)
        {
            _surface.BeginAnimation(UIElement.OpacityProperty, null);
            _surface.Opacity = Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// Animates the surface to <paramref name="to"/>. Animated on the rectangle
        /// rather than on Window.Opacity: the window is a layered HWND and WPF pushes
        /// per-frame alpha into it, which is both slower and coarser than a pure
        /// composition animation.
        /// </summary>
        public void FadeSurface(double to, TimeSpan duration, Action? completed)
        {
            var animation = new DoubleAnimation
            {
                To = Math.Clamp(to, 0.0, 1.0),
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };

            if (completed != null)
            {
                animation.Completed += (_, _) => completed();
            }

            _surface.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        /// <summary>Removes the spotlight hole - the mask becomes solid again.</summary>
        public void ClearSpotlight()
        {
            if (_surface.OpacityMask != null)
            {
                _surface.OpacityMask = null;
            }
        }

        private static RadialGradientBrush CreateHoleBrush()
        {
            // Absolute mapping: Center/Radius are DIPs inside this window, not fractions
            // of its bounding box. As an OpacityMask, alpha 0 means "see through", which
            // is exactly the hole; the last stop fades back to fully opaque.
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute
            };

            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x00, 0x00, 0x00), 1.0));
            return brush;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            Handle = new WindowInteropHelper(this).EnsureHandle();

            var style = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, style | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE);

            // Position using Win32 pixel coordinates directly to avoid
            // DPI mismatch between WinForms Screen.Bounds and WPF Left/Top.
            Win32.SetWindowPos(Handle, IntPtr.Zero,
                _screenBounds.Left, _screenBounds.Top,
                _screenBounds.Width, _screenBounds.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

            base.OnSourceInitialized(e);
        }
    }
}
