namespace FMSC_SMS.Core;

internal sealed class StudentPickerHeaderStrip : Control
{
    internal IReadOnlyList<StudentPicker.ColumnSpec> Columns { get; set; } =
        Array.Empty<StudentPicker.ColumnSpec>();

    internal StudentPickerHeaderStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var brush = new SolidBrush(AppTheme.Surface))
            e.Graphics.FillRectangle(brush, ClientRectangle);
        using (var pen = new Pen(AppTheme.InputBorder))
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        int x = 6;
        foreach (StudentPicker.ColumnSpec column in Columns)
        {
            var cell = new Rectangle(x, 0, column.Width, Height - 1);
            TextRenderer.DrawText(e.Graphics, column.Header, AppTheme.FontSmallBold, cell,
                AppTheme.MutedText,
                (column.RightAligned ? TextFormatFlags.Right : TextFormatFlags.Left) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            x += column.Width + 14;
        }
    }
}

internal sealed class StudentPickerTextBox : TextBox
{
    internal Func<Keys, bool>? OwnsKey { get; set; }

    protected override bool IsInputKey(Keys keyData) =>
        OwnsKey?.Invoke(keyData) == true || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && OwnsKey?.Invoke(keyData) != true && FormFlow.TryAdvance(this))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
