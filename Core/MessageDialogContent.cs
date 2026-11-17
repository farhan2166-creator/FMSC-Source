namespace FMSC_SMS.Core;

/// <summary>Optional presentation only. Financial data and dialog results remain unchanged.</summary>
public enum MessageDialogStyle { Standard, FamilyCollection }

/// <summary>Explicit display details; values are rendered as text, never interpreted as markup.</summary>
public sealed record MessageDialogContent(
    string Summary,
    string? Explanation = null,
    IReadOnlyList<(string Label, string Value)>? Facts = null,
    DataTable? Records = null,
    string? Notice = null,
    MessageDialogStyle Style = MessageDialogStyle.Standard)
{
    internal static MessageDialogContent FromText(string? message)
    {
        string text = (message ?? string.Empty).Replace("\r\n", "\n");
        int split = text.IndexOf('\n');
        return split < 0 ? new(text) : new(text[..split], text[(split + 1)..].TrimStart('\n'));
    }
}
