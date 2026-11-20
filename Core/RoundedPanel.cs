using System.Drawing.Drawing2D;
namespace FMSC_SMS.Core
{
    public class RoundedPanel : Panel
    {
        private int _radius = AppTheme.CardRadius;
        private Color _borderColor = AppTheme.Border;
        private bool _showBorder = true;
        private GraphicsPath? _paintPath;
        private Size _paintPathSize;
        private int _paintPathRadius;
        private Size _regionSize;
        private int _regionRadius = int.MinValue;
        public int CornerRadius
        {
            get => _radius;
            set
            {
                _radius = value;
                UpdateRegion();
                Invalidate();
            }
        }
        public Color BorderColor
        {
            get => _borderColor;
            set
            {
                _borderColor = value;
                Invalidate();
            }
        }
        public bool ShowBorder
        {
            get => _showBorder;
            set
            {
                _showBorder = value;
                Invalidate();
            }
        }
        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = AppTheme.SurfaceElevated;
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }
        private void UpdateRegion()
        {
            if (AppTheme.UseSimpleRendering)
            {
                if (Region is not null) UiHelper.ReplaceRegion(this, null);
                _regionRadius = int.MinValue;
                return;
            }
            if (Width <= 0 || Height <= 0)
                return;
            if (_regionSize == Size && _regionRadius == _radius)
                return;
            using var path =
                CreateRoundedPath(new Rectangle(0, 0, Width, Height), _radius);
            UiHelper.ReplaceRegion(this, new Region(path));
            _regionSize = Size;
            _regionRadius = _radius;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (AppTheme.UseSimpleRendering)
            {
                e.Graphics.SmoothingMode = SmoothingMode.None;
                using var shape = new UiPaintShape(new Rectangle(0, 0, Width - 1, Height - 1), _radius);
                using var fill = new SolidBrush(BackColor);
                shape.Fill(e.Graphics, fill);
                if (_showBorder)
                {
                    using var border = new Pen(_borderColor, 1F) { Alignment = PenAlignment.Inset };
                    shape.Draw(e.Graphics, border);
                }
                return;
            }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect =
                new Rectangle(0, 0, Width - 1, Height - 1);
            if (_paintPath is null || _paintPathSize != Size || _paintPathRadius != _radius)
            {
                _paintPath?.Dispose();
                _paintPath = CreateRoundedPath(rect, _radius);
                _paintPathSize = Size;
                _paintPathRadius = _radius;
            }
            var path = _paintPath;
            using var brush =
                new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, path);
            if (_showBorder)
            {
                using var pen =
                    new Pen(_borderColor, 1F) {
                        Alignment = PenAlignment.Inset
                    };
                e.Graphics.DrawPath(pen, path);
            }
        }
        internal void RefreshRendering()
        {
            if (AppTheme.UseSimpleRendering)
            {
                _paintPath?.Dispose();
                _paintPath = null;
            }
            UpdateRegion();
            Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _paintPath?.Dispose();
                _paintPath = null;
            }
            base.Dispose(disposing);
        }
        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0 || diameter > rect.Width || diameter > rect.Height)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
