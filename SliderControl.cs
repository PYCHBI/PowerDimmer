using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PowerDimmer
{
    /// <summary>
    /// Hand-drawn 0-100 slider with its caption, used as the dim-level row of the menu.
    ///
    /// Three problems with the stock TrackBar drove this:
    ///   1. On this machine's 175% display its thumb was drawn outside the control's
    ///      bounds and got clipped flat at 100%.
    ///   2. Mixing a Label and a TrackBar inside a ToolStripControlHost means two
    ///      children that can disagree with the width the ToolStrip finally allocates.
    ///   3. The host is a ToolStrip item, so its final height is not necessarily the
    ///      height we asked for.
    /// Painting both the caption and the track in one control, docked to fill, and
    /// deriving every measurement from the *actual* size at paint time removes all
    /// three: nothing can be wider or taller than the row really is.
    /// </summary>
    public sealed class SliderControl : Control
    {
        private const int MinValue = 0;
        private const int MaxValue = 100;
        private const int CaptionGap = 6;

        private static readonly Color TrackColor = Color.FromArgb(198, 198, 198);
        private static readonly Color FillColor = Color.FromArgb(0, 120, 212);

        private int value;
        private bool dragging;
        private string captionText = string.Empty;

        public event EventHandler? ValueChanged;

        public SliderControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Cursor = Cursors.Hand;
            Font = SystemFonts.MenuFont;
        }

        public static int CaptionHeight => SystemFonts.MenuFont.Height + 8;

        /// <summary>
        /// Height of the track band. Kept comfortably larger than the thumb so the
        /// circle never touches the row edge - a flattened bottom is very visible.
        /// </summary>
        public static int TrackHeight => Math.Max(30, SystemFonts.MenuFont.Height + 12);

        public static int TotalHeight => CaptionHeight + CaptionGap + TrackHeight;

        public string CaptionText
        {
            get => captionText;
            set
            {
                var text = value ?? string.Empty;
                if (captionText == text)
                {
                    return;
                }

                captionText = text;
                Invalidate();
            }
        }

        public int Value
        {
            get => value;
            set
            {
                var clamped = Math.Clamp(value, MinValue, MaxValue);
                if (clamped == this.value)
                {
                    return;
                }

                this.value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private float Scale => DeviceDpi / 96f;

        /// <summary>
        /// Geometry of the track band, all derived from the size the ToolStrip actually
        /// gave us. The thumb radius shrinks when the row is short rather than spilling
        /// over the edge, and the horizontal margin is exactly one radius + the outline,
        /// so the circle is fully inside at 0% and at 100%.
        /// </summary>
        private (float Left, float Right, float MidY, float Radius) Geometry()
        {
            var scale = Scale;
            var captionHeight = Math.Min(CaptionHeight, Height / 2);
            var trackTop = captionHeight + CaptionGap;
            var available = Height - trackTop;
            if (available <= 0)
            {
                return (0f, 0f, 0f, 0f);
            }

            // 2px of air on each side keeps the antialiased outline inside the row.
            var radius = Math.Min(7f * scale, Math.Max(3f, available / 2f - 2f));
            var midY = trackTop + available / 2f;
            var margin = radius + 2f;

            return (margin, Width - margin, midY, radius);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var captionHeight = Math.Min(CaptionHeight, Height);
            if (captionText.Length > 0)
            {
                TextRenderer.DrawText(
                    graphics,
                    captionText,
                    Font,
                    new Rectangle(0, 0, Width, captionHeight),
                    SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            var (left, right, midY, thumbRadius) = Geometry();
            if (right <= left || thumbRadius <= 0f)
            {
                return;
            }

            var scale = Scale;
            var barHeight = Math.Max(2f, 3f * scale);
            var ratio = (Value - MinValue) / (float)(MaxValue - MinValue);
            var thumbX = left + (right - left) * ratio;

            using var trackBrush = new SolidBrush(TrackColor);
            using var fillBrush = new SolidBrush(FillColor);
            using var thumbBrush = new SolidBrush(Color.White);
            using var thumbPen = new Pen(FillColor, Math.Max(1.5f, Math.Min(2f * scale, thumbRadius * 0.3f)));

            graphics.FillRectangle(trackBrush, left, midY - barHeight / 2f, right - left, barHeight);

            var filled = thumbX - left;
            if (filled > 0)
            {
                graphics.FillRectangle(fillBrush, left, midY - barHeight / 2f, filled, barHeight);
            }

            graphics.FillEllipse(thumbBrush, thumbX - thumbRadius, midY - thumbRadius, thumbRadius * 2f, thumbRadius * 2f);
            graphics.DrawEllipse(thumbPen, thumbX - thumbRadius, midY - thumbRadius, thumbRadius * 2f, thumbRadius * 2f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            dragging = true;
            Capture = true;
            SetFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                SetFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        private void SetFromMouse(int x)
        {
            var (left, right, _, _) = Geometry();
            if (right <= left)
            {
                return;
            }

            var ratio = Math.Clamp((x - left) / (right - left), 0f, 1f);
            Value = MinValue + (int)Math.Round(ratio * (MaxValue - MinValue));
        }
    }
}
