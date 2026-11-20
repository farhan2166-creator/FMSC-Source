namespace FMSC_SMS.Core
{
    public class ModernCheckBox : CheckBox
    {
        private bool optionStyle;
        public bool OptionStyle
        {
            get => optionStyle;
            set
            {
                optionStyle = value;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, value);
                MinimumSize = value ? new Size(0, AppMetrics.InputControlHeight) : Size.Empty;
                Invalidate();
            }
        }
        public ModernCheckBox()
        {
            Font = AppTheme.NormalFont;
            ForeColor = AppTheme.Text;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if (!optionStyle) return base.GetPreferredSize(proposedSize);
            return new Size(TextRenderer.MeasureText(Text, Font).Width + AppScale.Scale(42), AppMetrics.InputControlHeight);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!optionStyle) { base.OnPaint(e); return; }
            e.Graphics.Clear(Checked && Enabled ? AppTheme.PrimaryLight : AppTheme.SurfaceElevated);
            int size = AppScale.Scale(19);
            var box = new Rectangle(AppScale.Scale(8), (Height - size) / 2, size, size);
            Color accent = Enabled ? AppTheme.Primary : AppTheme.InputBorder;
            using var fill = new SolidBrush(Checked ? accent : AppTheme.Surface);
            using var border = new Pen(Checked ? accent : AppTheme.InputBorder, 1.5f);
            e.Graphics.FillRectangle(fill, box);
            e.Graphics.DrawRectangle(border, box);
            if (Checked)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.White, Math.Max(2f, DeviceDpi / 48f));
                e.Graphics.DrawLines(pen, new[] {
                    new Point(box.Left + size / 5, box.Top + size / 2),
                    new Point(box.Left + size * 2 / 5, box.Top + size * 3 / 4),
                    new Point(box.Left + size * 4 / 5, box.Top + size / 4) });
            }
            var textRect = new Rectangle(box.Right + AppScale.Scale(8), 0,
                Math.Max(0, Width - box.Right - AppScale.Scale(12)), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect,
                Enabled ? AppTheme.Text : AppTheme.MutedText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }
}
