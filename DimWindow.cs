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
        /// Radius, in DIPs, to clip this mask to; 0 for a plain rectangle. Windows 11
        /// windows are rounded, so the four corner wedges inside their bounding box are
        /// not painted by the window - the full-screen mask shows through there and is
        /// already dimmed. A rectangular overlay laid on top of that spot dims it a second
        /// time, which reads as a small darker square at each corner until it closes.
        /// Clipping the overlay to the same radius keeps every pixel dimmed exactly once.
        /// </summary>
        public double CornerRadiusDip { get; set; }

        /// <summary>
        /// Width, in DIPs, of the band along the straight edges that this mask must leave
        /// alone; 0 for "cover the whole rounded rectangle".
        ///
        /// Windows 11 paints a translucent frame stroke around every window, and those
        /// pixels are a window onto the mask underneath: dimming them again with this
        /// overlay double-darkens them, which is the hairline seen along the edges while
        /// the fade runs. So the straight edges are pulled in by the stroke width.
        ///
        /// The corners are the opposite case: the arc there is the window's own opaque
        /// frame, and leaving it uncovered keeps it at full brightness - a thin white
        /// crescent hugging the corner. That is worse and much more visible, so the arc
        /// stays fully covered: the clip is the window's rounded rectangle intersected
        /// with a rectangle inset by this amount, which pulls in the straight edges only.
        /// </summary>
        public double EdgeInsetDip { get; set; }

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

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ApplyCornerClip();
        }

        /// <summary>
        /// Rounds the painted surface off with a clipping geometry.
        ///
        /// Done in WPF rather than with SetWindowRgn: a window region is binary, so the
        /// arc lands on whole pixels and either bites a pixel out of the window corner or
        /// leaves one covered - a one-pixel rim that lightens by ~16 levels when the fade
        /// hands over. A WPF clip is anti-aliased and measures in DIPs, which is the unit
        /// the radius is quoted in.
        /// </summary>
        private void ApplyCornerClip()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                _surface.Clip = null;
                return;
            }

            var inset = Math.Max(0, EdgeInsetDip);

            // Nothing to shape: no round corners and no edge band to skip.
            if (CornerRadiusDip <= 0 && inset <= 0)
            {
                _surface.Clip = null;
                return;
            }

            var full = new RectangleGeometry(
                new Rect(0, 0, width, height), CornerRadiusDip, CornerRadiusDip);

            if (inset <= 0 || width <= inset * 2 || height <= inset * 2)
            {
                _surface.Clip = full;
                return;
            }

            // Intersecting keeps the arcs at their full radius (so the corners stay
            // covered) while the square inset pulls the four straight edges inwards.
            var inner = new RectangleGeometry(
                new Rect(inset, inset, width - inset * 2, height - inset * 2));

            _surface.Clip = Geometry.Combine(full, inner, GeometryCombineMode.Intersect, null);
        }
    }
}
