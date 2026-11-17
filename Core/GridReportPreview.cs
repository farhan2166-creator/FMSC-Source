using System.Globalization;
using System.Xml.Linq;

namespace FMSC_SMS.Core;

internal static class GridReportPreview
{
    internal sealed record Report(string Definition, DataTable Data)
    {
        internal string Title { get; init; } = "Report";
    }
    internal static Report Build(string institution,string title,string subtitle,DataTable source,string printedBy,string moneyFormat="#,##0.##",IReadOnlyList<ReportPrintMetric>? summaryMetrics=null,string? campusContext=null)
    {
        if(source.Columns.Count==0)throw new ArgumentException("Choose at least one report column.",nameof(source));
        if(FamilyAdvanceReport.CanBuild(source))return FamilyAdvanceReport.Build(institution,title,subtitle,source,printedBy,moneyFormat);
        using var printSource=ReportContextProjection.ForPdf(source,campusContext);
        source=printSource;
        XNamespace ns="http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition";
        XElement E(string name,params object[] content)=>new(ns+name,content);
        string Inch(double value)=>value.ToString("0.####",CultureInfo.InvariantCulture)+"in";
        var columns=GridReportColumns.Create(source,moneyFormat);
        bool compactReceipts=CollectionReceiptGrouping.Layout(source) is not null;
        bool compactFinancial=ManagementReportPrintLayout.Applies(title);
        bool compactTable=compactReceipts||compactFinancial;
        if(compactReceipts)columns.Insert(0,new GridReportColumns.Column("#",Array.Empty<int>(),false,.3,.2));
        bool landscape=columns.Count>6||columns.Sum(column=>column.Minimum)>7.25;
        double width=landscape?10.67:7.25;
        if(compactFinancial)ManagementReportPrintLayout.Apply(columns,width,title);
        var bands=GridReportColumns.Bands(columns,width);
        var data=new DataTable("ReportData");
        for(int i=0;i<columns.Count;i++)data.Columns.Add("C"+i);
        data.Columns.Add("IsReportTotal",typeof(bool));
        var receiptLayout = CollectionReceiptGrouping.Layout(source);
        if (receiptLayout is not null)
        {
            data.Columns.Add("GroupKey"); data.Columns.Add("GroupLabel");
            for (int i = 0; i < columns.Count; i++) if (columns[i].Numeric) data.Columns.Add("N" + i, typeof(decimal));
        }
        for (int rowIndex = 0; rowIndex < source.Rows.Count; rowIndex++)
        {
            if (receiptLayout is not null && receiptLayout[rowIndex].Kind != "Detail") continue;
            DataRow row = source.Rows[rowIndex], printed = data.NewRow();
            printed["IsReportTotal"]=ReportPrintSummary.IsTotalRow(row);
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                printed["C" + i] = compactReceipts&&column.Caption=="#" ? (data.Rows.Count+1).ToString(CultureInfo.CurrentCulture)
                    : GridReportColumns.DisplayValue(row,column,moneyFormat);
                if (receiptLayout is not null && column.Numeric)
                    printed["N" + i] = row.IsNull(column.Fields[0]) ? 0m
                        : ReportMoneyPresentation.Amount(source.Columns[column.Fields[0]],Convert.ToDecimal(row[column.Fields[0]]));
            }
            if (receiptLayout is not null)
            {
                printed["GroupKey"] = receiptLayout[rowIndex].GroupKey;
                printed["GroupLabel"] = receiptLayout[rowIndex].Label;
            }
            data.Rows.Add(printed);
        }

        XElement Text(string name,string value,bool bold=false,string align="Left",string size="9pt")=>
            E("Textbox",new XAttribute("Name",name),E("CanGrow",true),E("CanShrink",true),
                E("Paragraphs",E("Paragraph",E("TextRuns",E("TextRun",E("Value",value),
                    E("Style",E("FontFamily","Segoe UI"),E("FontSize",size),E("FontWeight",bold?"Bold":"Normal"),E("Color","#000000")))),E("Style",E("TextAlign",align)))),
                E("Style",E("VerticalAlign","Middle"),E("PaddingLeft","4pt"),E("PaddingRight","4pt"),E("PaddingTop",compactReceipts?"1.5pt":compactFinancial?"2pt":"3pt"),E("PaddingBottom",compactReceipts?"1.5pt":compactFinancial?"2pt":"3pt")));
        XElement Position(XElement text,double left,double top,double textWidth,double height)
        {text.Add(E("Top",Inch(top)),E("Left",Inch(left)),E("Height",Inch(height)),E("Width",Inch(textWidth)));return text;}
        var items=new List<XElement>();
        for(int bandIndex=0;bandIndex<bands.Count;bandIndex++)
        {
            int[] indices=bands[bandIndex];double minimum=indices.Sum(i=>columns[i].Minimum),weight=indices.Sum(i=>columns[i].Weight);
            double[] widths=indices.Select(i=>minimum<=width?columns[i].Minimum+(width-minimum)*columns[i].Weight/weight:width*columns[i].Minimum/minimum).ToArray();
            var headings=new List<XElement>();var details=new List<XElement>();
            foreach(int index in indices)
            {
                var column=columns[index];
                string alignment=GridReportColumns.Alignment(source,column);
                var heading=Text($"Heading{bandIndex}_{index}",column.Caption,true,"Center","8.5pt");
                heading.Element(ns+"Style")!.Add(E("BottomBorder",E("Style","Solid"),E("Width","0.8pt"),E("Color","#000000")));
                headings.Add(E("TablixCell",E("CellContents",heading)));
                var detail=Text($"Value{bandIndex}_{index}",$"=Fields!C{index}.Value",false,alignment);
                detail.Descendants(ns+"FontWeight").First().Value="=IIf(Fields!IsReportTotal.Value, \"Bold\", \"Normal\")";
                detail.Element(ns+"Style")!.Add(E("BottomBorder",E("Style","Solid"),E("Width","0.25pt"),E("Color","#000000")));
                detail.Element(ns+"Style")!.Add(E("TopBorder",E("Style","=IIf(Fields!IsReportTotal.Value, \"Solid\", \"None\")"),E("Width","0.8pt"),E("Color","#000000")));
                details.Add(E("TablixCell",E("CellContents",detail)));
            }
            if(compactReceipts)
            {
                double left=0;var receiptItems=new List<XElement>();
                for(int position=0;position<details.Count;position++)
                {
                    var box=details[position].Descendants(ns+"Textbox").Single();
                    box.Remove();
                    var style=box.Element(ns+"Style")!;
                    style.Element(ns+"BottomBorder")?.Remove();style.Element(ns+"TopBorder")?.Remove();
                    style.SetElementValue(ns+"VerticalAlign","Top");
                    receiptItems.Add(Position(box,left,0,widths[position],.22));left+=widths[position];
                }
                var block=E("Rectangle",new XAttribute("Name","ReceiptBlock"+bandIndex),E("ReportItems",receiptItems),
                    E("KeepTogether",true),E("Height","0.22in"),E("Width",Inch(width)),
                    E("Style",E("BottomBorder",E("Style","Solid"),E("Width","0.25pt"),E("Color","#000000"))));
                details.Clear();details.Add(E("TablixCell",E("CellContents",block,E("ColSpan",indices.Length))));
                details.AddRange(Enumerable.Range(1,indices.Length-1).Select(_=>E("TablixCell")));
            }
            var table=E("Tablix",new XAttribute("Name","ReportRows"+bandIndex),
                E("TablixBody",E("TablixColumns",widths.Select(w=>E("TablixColumn",E("Width",Inch(w))))),
                    E("TablixRows",E("TablixRow",E("Height",compactTable?"0.32in":"0.42in"),E("TablixCells",headings)),E("TablixRow",E("Height",compactTable?"0.22in":"0.26in"),E("TablixCells",details)))),
                E("TablixColumnHierarchy",E("TablixMembers",indices.Select(_=>E("TablixMember")))),
                E("TablixRowHierarchy",E("TablixMembers",E("TablixMember",E("KeepWithGroup","After"),E("RepeatOnNewPage",true)),
                    E("TablixMember",E("Group",new XAttribute("Name","DetailRows"+bandIndex)),E("KeepTogether",true)))),
                E("DataSetName","ReportData"),E("NoRowsMessage","No records match the selected filters."),E("Top",bands.Count>1?"0.25in":"0in"),E("Left","0in"),E("Height","0.68in"),E("Width",Inch(width)),
                E("Style",E("FontFamily","Segoe UI"),E("FontSize","9pt"),E("Color","#000000"),E("TextAlign","Center")));
            if (receiptLayout is not null)
            {
                table.Add(E("KeepTogether",true));
                CollectionReceiptRdlc.Apply(table, indices, columns.Select(column => column.Numeric).ToArray(), bandIndex);
            }
            if(bands.Count==1)items.Add(table);
            else
            {
                var note=Position(Text("Section"+bandIndex,$"Details {bandIndex+1} of {bands.Count} - student/reference columns repeat for matching records",false,"Center","8pt"),0,0,width,.23);
                var rectangle=E("Rectangle",new XAttribute("Name","SectionBlock"+bandIndex),E("ReportItems",note,table),
                    E("KeepTogether",false),E("Top",Inch(bandIndex*.95)),E("Left","0in"),E("Height","0.93in"),E("Width",Inch(width)),E("Style"));
                if(bandIndex>0)rectangle.Add(E("PageBreak",E("BreakLocation","Start")));
                items.Add(rectangle);
            }
        }
        var presentation=ReportPrintSummary.Split(subtitle,summaryMetrics);
        var metrics=presentation.Metrics;
        string scope=presentation.Scope;
        if(source.Columns.Cast<DataColumn>().Any(ReportPrintSummary.IsMoneyColumn)) scope+=(scope.Length>0?"   |   ":"")+"Amounts in Rs.";
        using var bitmap=new Bitmap(1,1);using var graphics=Graphics.FromImage(bitmap);
        double TextHeight(string text,float points,double? availableWidth=null)
        {
            if(text.Length==0)return 0;
            using var font=new Font("Segoe UI",points);
            float contentWidth=(float)Math.Max(1,((availableWidth??width)-8d/72)*graphics.DpiX);
            var measured=graphics.MeasureString(text,font,new SizeF(contentWidth,10000),StringFormat.GenericTypographic);
            return Math.Max(points/72*1.3,measured.Height/graphics.DpiY)+6d/72+.02;
        }
        double top=0;
        var headerItems=new List<XElement>();
        double schoolHeight=TextHeight(institution,14);
        headerItems.Add(Position(Text("Institution",institution,true,"Center","14pt"),0,top,width,schoolHeight));top+=schoolHeight+.02;
        double titleHeight=TextHeight(title,12);
        headerItems.Add(Position(Text("ReportTitle",title,true,"Center","12pt"),0,top,width,titleHeight));top+=titleHeight+.03;
        if(scope.Length>0){double scopeHeight=TextHeight(scope,8.5f);headerItems.Add(Position(Text("Filters",scope,false,"Center","8.5pt"),0,top,width,scopeHeight));top+=scopeHeight+.08;}
        if(metrics.Count>0)
        {
            int count=Math.Min(metrics.Count,landscape?6:3);double metricWidth=width/count;
            for(int start=0;start<metrics.Count;start+=count)
            {
                int end=Math.Min(start+count,metrics.Count);
                double labelHeight=Enumerable.Range(start,end-start).Max(i=>TextHeight(metrics[i].Label,8,metricWidth));
                double valueHeight=Enumerable.Range(start,end-start).Max(i=>TextHeight(metrics[i].Value,11,metricWidth));
                for(int index=start;index<end;index++)
                {
                    double x=(index-start)*metricWidth;
                    headerItems.Add(Position(Text("MetricLabel"+index,metrics[index].Label,false,"Center","8pt"),x,top,metricWidth,labelHeight));
                    headerItems.Add(Position(Text("MetricValue"+index,metrics[index].Value,true,"Center","11pt"),x,top+labelHeight,metricWidth,valueHeight));
                }
                top+=labelHeight+valueHeight+.03;
            }
            top+=.05;
        }
        string safeBy=printedBy.Replace("\"","\"\"").Replace("\r"," ").Replace("\n"," ");
        var footerItems=new[]{
            Position(Text("Stamp","=\"Printed \" & Format(Now(), \"dd-MM-yyyy HH:mm\") & \"  |  Printed by: "+safeBy+"\"",false,"Left","7.5pt"),0,0,width*.75,.27),
            Position(Text("PageNumber","=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages",false,"Right","7.5pt"),width*.75,0,width*.25,.27)};
        var document=new XDocument(E("Report",
            E("DataSources",E("DataSource",new XAttribute("Name","Source"),E("ConnectionProperties",E("DataProvider","System.Data.DataSet"),E("ConnectString","")))),
            E("DataSets",E("DataSet",new XAttribute("Name","ReportData"),E("Query",E("DataSourceName","Source"),E("CommandText","")),
                E("Fields",data.Columns.Cast<DataColumn>().Select(column=>E("Field",new XAttribute("Name",column.ColumnName),E("DataField",column.ColumnName)))))),
            E("Body",E("ReportItems",items),E("Height",Inch(bands.Count==1?.68:bands.Count*.95)),E("Style")),E("Width",Inch(width)),
            E("Page",E("PageHeader",E("Height",Inch(top)),E("PrintOnFirstPage",true),E("PrintOnLastPage",true),E("ReportItems",headerItems),E("Style")),
                E("PageFooter",E("Height","0.30in"),E("PrintOnFirstPage",true),E("PrintOnLastPage",true),E("ReportItems",footerItems),E("Style")),
                E("PageHeight",landscape?"8.27in":"11.69in"),E("PageWidth",landscape?"11.69in":"8.27in"),
                E("LeftMargin","0.5in"),E("RightMargin","0.5in"),E("TopMargin","0.35in"),E("BottomMargin","0.35in"),E("Style")),E("ConsumeContainerWhitespace",true)));
        return new(document.ToString(),data) { Title=title };
    }
    internal static void ExportPdf(string path,Report report)
    {
        using var local=new Microsoft.Reporting.WinForms.LocalReport();
        local.DisplayName=report.Title;
        local.LoadReportDefinition(new StringReader(report.Definition));
        local.DataSources.Add(new Microsoft.Reporting.WinForms.ReportDataSource("ReportData",report.Data));
        byte[] bytes=local.Render("PDF");
        AtomicExportFile.Publish(path,temp=>File.WriteAllBytes(temp,bytes),PdfTableExporter.ValidatePdf);
    }

    internal static void Show(IWin32Window? owner, string institution, string title, string subtitle, DataTable source, string printedBy, string moneyFormat = "#,##0.##",IReadOnlyList<ReportPrintMetric>? summaryMetrics=null,string? campusContext=null)
    {
        var report = Build(institution, title, subtitle, source, printedBy, moneyFormat,summaryMetrics,campusContext);
        using var printedData=report.Data;
        RdlcReportPreview.Show(owner, title, "", "ReportData", report.Data, reportDefinition: report.Definition);
    }
}
