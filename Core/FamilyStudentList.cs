using System.Runtime.InteropServices;
using System.Windows.Forms.VisualStyles;

namespace FMSC_SMS.Core;

/// <summary>Two-line family choices retaining CheckedListBox keyboard and check behavior.</summary>
internal sealed class FamilyStudentList : CheckedListBox
{
    private Func<int,(decimal Total,decimal Pay)>? paymentAmounts;
    private Func<int,string>? statusText;
    internal void SetStatusRows(Func<int,string>? status)
    {
        statusText=status;FitRows();Invalidate();
    }
    private int StatusHeight=>statusText is null?0:AppTheme.SmallFont.Height+AppScale.Scale(3);
    internal void SetPaymentRows(Func<int,(decimal Total,decimal Pay)> amounts)
    {
        paymentAmounts=amounts;FitRows();Invalidate();
    }
    internal Rectangle PaymentBounds(int index)
    {
        var row=GetItemRectangle(index);int inset=AppScale.Scale(CompactRows?6:8);
        int width=Math.Min(AppScale.Scale(90),Math.Max(AppScale.Scale(65),row.Width/3));
        return new Rectangle(row.Right-inset-width,row.Top+inset+2*(LineHeight+LineGap)+StatusHeight,width,AppTheme.SmallFont.Height+AppScale.Scale(4));
    }
    private int LineHeight=>Font.Height+(CompactRows?2:4);
    private int LineGap=>CompactRows?0:2;
    /// <summary>Row clicks select for review; only the drawn checkbox changes inclusion.</summary>
    internal bool CheckBoxOnly { get; set; }
    internal bool CompactRows { get; set; }
    private bool _mouseOutsideCheckBox;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr index, IntPtr height);
    protected override void WndProc(ref Message m)
    {
        // CheckedListBox normally toggles a selected item on a text click too,
        // even with CheckOnClick disabled. Limit mouse toggles to our drawn glyph.
        if (CheckBoxOnly && m.Msg is 0x0201 or 0x0202 or 0x0203)
        {
            var point = new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16)));
            int index = IndexFromPoint(point);
            bool previous = _mouseOutsideCheckBox;
            _mouseOutsideCheckBox = index < 0 || !CheckBoxBounds(index).Contains(point);
            try { base.WndProc(ref m); }
            finally { _mouseOutsideCheckBox = previous; }
            return;
        }
        base.WndProc(ref m);
    }
    protected override void OnItemCheck(ItemCheckEventArgs e)
    {
        if (_mouseOutsideCheckBox)
        {
            e.NewValue = e.CurrentValue;
            return;
        }
        base.OnItemCheck(e);
    }
    internal Rectangle CheckBoxBounds(int index)
    {
        using var graphics = CreateGraphics();
        return CheckBoxBounds(index, graphics);
    }
    private Rectangle CheckBoxBounds(int index, Graphics graphics)
    {
        Rectangle row = GetItemRectangle(index);
        int inset = AppScale.Scale(CompactRows?6:8);
        var size = CheckBoxRenderer.GetGlyphSize(graphics, CheckBoxState.UncheckedNormal);
        return new Rectangle(row.Left + inset, row.Top + inset, size.Width, size.Height);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); FitRows(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitRows(); }
    private void FitRows()
    {
        if (IsHandleCreated) SendMessage(Handle, 0x01A0, IntPtr.Zero, (IntPtr)(paymentAmounts is null
            ?Font.Height*2+StatusHeight+AppScale.Scale(CompactRows?18:24)
            :2*(LineHeight+LineGap)+StatusHeight+AppTheme.SmallFont.Height+AppScale.Scale(CompactRows?16:24)));
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? AppTheme.SelectionBackground : AppTheme.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        var state = GetItemChecked(e.Index) ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal;
        var size = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        int inset = AppScale.Scale(CompactRows?6:8), gap = AppScale.Scale(8);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, CheckBoxBounds(e.Index, e.Graphics).Location, state);
        string[] lines = (GetItemText(Items[e.Index]) ?? "").Split('\n', 2);
        int left = e.Bounds.Left + inset + size.Width + gap;
        var first = new Rectangle(left, e.Bounds.Top + inset, Math.Max(1, e.Bounds.Right - left - inset), LineHeight);
        TextRenderer.DrawText(e.Graphics, lines[0], AppTheme.BodyBoldFont, first, selected?AppTheme.SelectionText:AppTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (lines.Length > 1)
        {
            var second = new Rectangle(first.Left, first.Bottom + LineGap, first.Width, LineHeight);
            TextRenderer.DrawText(e.Graphics, lines[1], Font, second, selected?AppTheme.SelectionText:AppTheme.MutedText,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if(statusText is not null)
        {
            string status=statusText(e.Index);
            var statusBounds=new Rectangle(first.Left,first.Top+2*(LineHeight+LineGap),first.Width,StatusHeight);
            var color=selected?AppTheme.SelectionText:status=="Awaiting Return"?AppTheme.Warning:AppTheme.MutedText;
            TextRenderer.DrawText(e.Graphics,status,AppTheme.SmallFont,statusBounds,color,
                TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        }
        if(paymentAmounts is not null)
        {
            var info=paymentAmounts(e.Index);var pay=PaymentBounds(e.Index);
            var color=selected?AppTheme.SelectionText:AppTheme.Text;
            var owed=new Rectangle(left,pay.Top,Math.Max(1,pay.Left-left-AppScale.Scale(32)),pay.Height);
            TextRenderer.DrawText(e.Graphics,$"Total: {info.Total:N0}",AppTheme.SmallFont,owed,color,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,"Pay",AppTheme.SmallFont,new Rectangle(pay.Left-AppScale.Scale(30),pay.Top,AppScale.Scale(30),pay.Height),color,TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics,info.Pay.ToString("N0"),AppTheme.SmallFont,Rectangle.Inflate(pay,-3,0),color,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        }
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}
