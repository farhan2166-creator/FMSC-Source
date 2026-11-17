using System.Globalization;
using System.Xml.Linq;

namespace FMSC_SMS.Core;

// The family register's compact print layout. A child is a complete physical row;
// families stay together where they fit, and oversized families repeat their heading.
internal static class FamilyAdvanceReport
{
    internal static bool CanBuild(DataTable source)=>source.Columns.Count==4&&
        source.ExtendedProperties[FamilyAdvanceLayout.Key] is string[] families&&families.Length==source.Rows.Count;

    internal static GridReportPreview.Report Build(string institution,string title,string subtitle,DataTable source,string printedBy,string moneyFormat)
    {
        var families=((string[])source.ExtendedProperties[FamilyAdvanceLayout.Key]!).Select(FamilyAdvanceLayout.Read).ToArray();
        using var rows=new DataTable();
        rows.Columns.Add("Father / Parent");rows.Columns.Add("Children / Class");
        rows.Columns.Add("Advance",typeof(decimal));rows.Columns.Add("Family Total",typeof(decimal));
        var children=new List<(int Family,FamilyAdvanceLayout.Child Child)>();
        for(int i=0;i<families.Length;i++)
            foreach(var child in families[i].Children)
            {
                rows.Rows.Add(families[i].Parent,child.Name,child.Advance,families[i].Children.Sum(member=>member.Advance));
                children.Add((i,child));
            }
        string scope=subtitle.Replace("View: Families", "",StringComparison.OrdinalIgnoreCase)
            .Replace("Only students matching the filters are included.","",StringComparison.OrdinalIgnoreCase).Trim(' ','|');
        var report=GridReportPreview.Build(institution,title,scope,rows,printedBy,moneyFormat);
        var data=report.Data;
        data.Columns.Add("FamilyKey");data.Columns.Add("ChildPlacement");data.Columns.Add("AdvanceAmount",typeof(decimal));
        for(int i=0;i<data.Rows.Count;i++)
        {
            data.Rows[i]["FamilyKey"]=children[i].Family.ToString(CultureInfo.InvariantCulture);
            data.Rows[i]["ChildPlacement"]=children[i].Child.Placement;
            data.Rows[i]["AdvanceAmount"]=children[i].Child.Advance;
        }
        var xml=XDocument.Parse(report.Definition);XNamespace ns=xml.Root!.Name.Namespace;
        XElement E(string name,params object[] values)=>new(ns+name,values);
        var fields=xml.Descendants(ns+"DataSet").Single().Element(ns+"Fields")!;
        foreach(string name in new[]{"FamilyKey","ChildPlacement","AdvanceAmount"})
            fields.Add(E("Field",new XAttribute("Name",name),E("DataField",name)));
        XElement Box(string name,string value,bool bold=false,string align="Left",string size="9pt")=>
            E("Textbox",new XAttribute("Name",name),E("CanGrow",true),E("CanShrink",true),
                E("Paragraphs",E("Paragraph",E("TextRuns",E("TextRun",E("Value",value),E("Style",E("FontFamily","Segoe UI"),E("FontSize",size),E("FontWeight",bold?"Bold":"Normal")))),E("Style",E("TextAlign",align)))),
                E("Style",E("VerticalAlign","Top"),E("PaddingLeft","4pt"),E("PaddingRight","4pt"),E("PaddingTop","1pt"),E("PaddingBottom","1pt")));
        XElement Cell(XElement box)=>E("TablixCell",E("CellContents",box));
        var heading=new[]{"Father / Parent","Children / Class","Advance","Family Total"}.Select((caption,i)=>{
            var box=Box("AdvanceHeading"+i,caption,true,"Center","8pt");
            box.Element(ns+"Style")!.Add(E("BottomBorder",E("Style","Solid"),E("Width","0.5pt"),E("Color","#000000")));
            return Cell(box);
        });
        var childBox=Box("AdvanceChild","=Fields!C1.Value");
        childBox.Element(ns+"Paragraphs")!.Add(E("Paragraph",E("TextRuns",E("TextRun",E("Value","=Fields!ChildPlacement.Value"),
            E("Style",E("FontFamily","Segoe UI"),E("FontSize","8pt"),E("Color","#000000")))),E("Style")));
        var groupCells=new[]{Cell(Box("AdvanceParent","=Fields!C0.Value",true)),Cell(Box("AdvanceGroupBlank1","")),
            Cell(Box("AdvanceGroupBlank2","")),Cell(Box("AdvanceGroupBlank3",""))};
        var familyLabel=Box("AdvanceFamilyLabel","Family total",true);
        var familyValue=Box("AdvanceFamilyTotal","=First(Fields!C3.Value, \"AdvanceFamily\")",true,"Center");
        foreach(var box in new[]{familyLabel,familyValue})box.Element(ns+"Style")!.Add(
            E("BottomBorder",E("Style","Solid"),E("Width","0.5pt"),E("Color","#000000")));
        var childAmount=Box("AdvanceChildAmount","=Fields!C2.Value",false,"Center");
        childBox.Add(E("Top","0in"),E("Left","1.8in"),E("Height","0.27in"),E("Width","3.3in"));
        childAmount.Add(E("Top","0in"),E("Left","5.1in"),E("Height","0.27in"),E("Width","0.85in"));
        var childBlock=E("Rectangle",new XAttribute("Name","AdvanceChildBlock"),E("ReportItems",childBox,childAmount),
            E("KeepTogether",true),E("Height","0.27in"),E("Width","7.25in"),
            E("Style",E("BottomBorder",E("Style","Solid"),E("Width","0.25pt"),E("Color","#000000"))));
        var detailCells=new[]{E("TablixCell",E("CellContents",childBlock,E("ColSpan",4))),E("TablixCell"),E("TablixCell"),E("TablixCell")};
        var totalLabel=Box("AdvanceGrandLabel","Grand Total",true);
        var totalValue=Box("AdvanceGrandValue","=\"Rs. \" & Format(Sum(Fields!AdvanceAmount.Value), \""+moneyFormat+"\")",true,"Center");
        foreach(var box in new[]{totalLabel,totalValue})
        {
            var style=box.Element(ns+"Style")!;style.Add(E("TopBorder",E("Style","Solid"),E("Width","0.8pt"),E("Color","#000000")));
            style.SetElementValue(ns+"PaddingTop","3pt");style.SetElementValue(ns+"PaddingBottom","3pt");
        }
        var table=xml.Descendants(ns+"Tablix").Single();
        table.Element(ns+"TablixBody")!.ReplaceWith(E("TablixBody",
            E("TablixColumns",new[]{"1.8in","3.3in","0.85in","1.3in"}.Select(width=>E("TablixColumn",E("Width",width)))),
            E("TablixRows",E("TablixRow",E("Height","0.24in"),E("TablixCells",heading)),
                E("TablixRow",E("Height","0.19in"),E("TablixCells",groupCells)),
                E("TablixRow",E("Height","0.27in"),E("TablixCells",detailCells)),
                E("TablixRow",E("Height","0.20in"),E("TablixCells",
                    E("TablixCell",E("CellContents",familyLabel,E("ColSpan",3))),E("TablixCell"),E("TablixCell"),Cell(familyValue))),
                E("TablixRow",E("Height","0.28in"),E("TablixCells",
                    E("TablixCell",E("CellContents",totalLabel,E("ColSpan",3))),E("TablixCell"),E("TablixCell"),Cell(totalValue))))));
        table.Element(ns+"TablixRowHierarchy")!.ReplaceWith(E("TablixRowHierarchy",E("TablixMembers",
            E("TablixMember",E("KeepWithGroup","After"),E("RepeatOnNewPage",true)),
            E("TablixMember",E("Group",new XAttribute("Name","AdvanceFamily"),E("GroupExpressions",E("GroupExpression","=Fields!FamilyKey.Value"))),
                E("TablixMembers",E("TablixMember",E("KeepWithGroup","After"),E("RepeatOnNewPage",true)),
                    E("TablixMember",E("Group",new XAttribute("Name","AdvanceChildRows")),E("KeepTogether",true)),
                    E("TablixMember",E("KeepWithGroup","Before"))),E("KeepTogether",true)),
            E("TablixMember",E("KeepWithGroup","Before")))));
        table.Element(ns+"Width")!.Value="7.25in";
        var widths=table.Element(ns+"TablixColumnHierarchy")!.Element(ns+"TablixMembers")!;
        widths.RemoveNodes();widths.Add(Enumerable.Range(0,4).Select(_=>E("TablixMember")));
        // One compact summary line replaces the large metric tiles for this register.
        var header=xml.Descendants(ns+"PageHeader").Single();var headerItems=header.Element(ns+"ReportItems")!;
        foreach(var metric in headerItems.Elements().Where(item=>((string?)item.Attribute("Name"))?.StartsWith("Metric",StringComparison.Ordinal)==true).ToArray())metric.Remove();
        double Inch(XElement item,string field)=>double.Parse(item.Element(ns+field)!.Value.Replace("in",""),CultureInfo.InvariantCulture);
        double top=headerItems.Elements().Max(item=>Inch(item,"Top")+Inch(item,"Height"))+.04;
        var summary=Box("AdvanceSummary",$"Families: {families.Length:N0}    Students: {children.Count:N0}    Total: Rs. {children.Sum(child=>child.Child.Advance).ToString(moneyFormat)}",false,"Center","9pt");
        summary.Add(E("Top",top.ToString(CultureInfo.InvariantCulture)+"in"),E("Left","0in"),E("Height","0.24in"),E("Width","7.25in"));
        headerItems.Add(summary);header.Element(ns+"Height")!.Value=(top+.30).ToString(CultureInfo.InvariantCulture)+"in";
        return new(xml.ToString(),data){Title=title};
    }
}
