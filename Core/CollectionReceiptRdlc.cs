using System.Xml.Linq;

namespace FMSC_SMS.Core;

// Applied only to receipt tables carrying explicit grouping metadata.
internal static class CollectionReceiptRdlc
{
    internal static void Apply(XElement table, int[] indices, IReadOnlyList<bool> numeric, int band)
    {
        XNamespace ns = table.Name.Namespace;
        XElement E(string name, params object[] content) => new(ns + name, content);
        var rows = table.Element(ns + "TablixBody")!.Element(ns + "TablixRows")!;
        XElement heading = rows.Elements().First(), detail = rows.Elements().Last();
        string group = "ReceiptGroup" + band;
        XElement Text(string name, string value, bool bold, string alignment = "Left")
        {
            var box = new XElement(heading.Descendants(ns + "Textbox").First());
            box.SetAttributeValue("Name", name);
            box.Descendants(ns + "Value").First().Value = value;
            box.Descendants(ns + "FontWeight").First().Value = bold ? "Bold" : "Normal";
            box.Descendants(ns + "TextAlign").First().Value = alignment;
            return box;
        }
        var groupHeading = E("TablixRow", E("Height", "0.23in"), E("TablixCells",
            E("TablixCell", E("CellContents", Text("ReceiptHeading" + band,
                $"=Fields!GroupLabel.Value & \" (\" & CountRows(\"{group}\") & IIf(CountRows(\"{group}\") = 1, \" entry)\", \" entries)\")", true), E("ColSpan", indices.Length))),
            Enumerable.Range(1, indices.Length - 1).Select(_ => E("TablixCell"))));
        XElement Total(bool grand)
        {
            int labelSpan=Math.Max(1,indices.TakeWhile(index=>!numeric[index]).Count());
            var cells = indices.Select((index, position) => {
                if(position>0&&position<labelSpan)return E("TablixCell");
                string value = position == 0 ? (grand
                    ? "=\"Grand Total (\" & CountRows(\"ReportData\") & IIf(CountRows(\"ReportData\") = 1, \" entry)\", \" entries)\")"
                    : $"=\"Subtotal — \" & First(Fields!GroupLabel.Value, \"{group}\")")
                    : numeric[index] ? $"=Format(Sum(Fields!N{index}.Value{(grand ? "" : ", \"" + group + "\"")}), \"#,##0.##\")" : "";
                var contents=E("CellContents",Text($"ReceiptTotal{band}_{index}_{grand}", value, true,
                    numeric[index] ? "Center" : "Left"));
                if(position==0&&labelSpan>1)contents.Add(E("ColSpan",labelSpan));
                return E("TablixCell",contents);
            });
            return E("TablixRow", E("Height", "0.23in"), E("TablixCells", cells));
        }
        rows.RemoveNodes();
        rows.Add(heading, groupHeading, detail, Total(false), Total(true));
        var members = table.Element(ns + "TablixRowHierarchy")!.Element(ns + "TablixMembers")!;
        var columnsHeader = new XElement(members.Elements().First());
        var dynamicDetails = new XElement(members.Elements().Last());
        // A detail group can split its growing textboxes despite KeepTogether.
        // Give each receipt a real group keyed by its unique printed row number.
        dynamicDetails.Element(ns+"Group")!.Add(E("GroupExpressions",E("GroupExpression","=Fields!C0.Value")));
        XElement EmptyGroupVisibility() => E("Visibility", E("Hidden", "=Len(Fields!GroupLabel.Value) = 0"));
        members.RemoveNodes();
        members.Add(columnsHeader,
            E("TablixMember", E("Group", new XAttribute("Name", group), E("GroupExpressions", E("GroupExpression", "=Fields!GroupKey.Value"))),
                E("TablixMembers",
                    E("TablixMember", EmptyGroupVisibility(), E("KeepWithGroup", "After"), E("RepeatOnNewPage", true)),
                    dynamicDetails,
                    E("TablixMember", EmptyGroupVisibility(), E("KeepWithGroup", "Before"))),
                E("KeepTogether", false)),
            E("TablixMember", E("KeepWithGroup", "Before")));
    }
}
