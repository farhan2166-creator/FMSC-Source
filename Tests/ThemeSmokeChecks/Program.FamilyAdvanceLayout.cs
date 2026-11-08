using System.Data;
using System.Reflection;
using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports.Finance;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckFamilyAdvanceLayout()
    {
        using var source=new DataTable();
        foreach(string name in new[]{"Family / Student","Members","Student Advances"})source.Columns.Add(name);
        source.Columns.Add("Available Credit",typeof(decimal));source.Columns.Add(FamilyAdvanceLayout.Column);
        var families=new List<FamilyAdvanceLayout.Family>();
        void Add(int number,int count)
        {
            var children=Enumerable.Range(0,count).Select(i=>new FamilyAdvanceLayout.Child($"Pupil{number:D3}C{i:D3}",$"Placement{number:D3}C{i:D3}",100m+number+i)).ToArray();
            var family=new FamilyAdvanceLayout.Family("Parent"+number.ToString("D3"),children);families.Add(family);
            source.Rows.Add(family.Parent,string.Join("\n\n",children.Select(child=>child.Name+"\n"+child.Placement)),
                string.Join("\n\n\n",children.Select(child=>child.Advance.ToString("N0"))),children.Sum(child=>child.Advance),JsonSerializer.Serialize(family));
        }
        Add(0,2);Add(1,3);
        using var grid=new DataGridView();
        foreach(var (key,header) in new[]{("Family / Student","Father / Parent"),("Members","Children / Class"),("Student Advances","Advance"),("Available Credit","Family Total")})
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name=key,DataPropertyName=key,HeaderText=header});
        string folder=Path.GetFullPath("artifacts/family-advance-layout");Directory.CreateDirectory(folder);
        GridReportPreview.Report Build()
        {
            using var projected=ReportExportBuilder.Build(source,grid.Columns.Cast<DataGridViewColumn>(),Array.Empty<string>());
            Check(FamilyAdvanceReport.CanBuild(projected) && !projected.Columns.Contains(FamilyAdvanceLayout.Column),
                "The actual export contains usable grouped-family data without the internal JSON payload");
            return GridReportPreview.Build("Fictional Meadow School & College","Student Advance Balance Register — Families","Campus: All   Class: All   Section: All",projected,"Fixture operator");
        }
        var compact=Build();
        using(compact.Data)
        {
            using var report=new LocalReport();report.LoadReportDefinition(new StringReader(compact.Definition));report.DataSources.Add(new ReportDataSource("ReportData",compact.Data));
            File.WriteAllBytes(Path.Combine(folder,"compact.pdf"),report.Render("PDF"));
            File.WriteAllBytes(Path.Combine(folder,"compact.png"),report.Render("IMAGE","<DeviceInfo><OutputFormat>PNG</OutputFormat><DpiX>110</DpiX><DpiY>110</DpiY></DeviceInfo>"));
        }
        // Verify the real form's four default columns and aligned per-child amount painting.
        using(var page=new CreditBalanceReportPage{BindingContext=new BindingContext()})
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            ((ModernComboBox)typeof(CanonicalFinanceReportPage).GetField("View",flags)!.GetValue(page)!).SelectedItem="Families";
            var pageGrid=(DataGridView)typeof(V2Page).GetField("Grid",flags)!.GetValue(page)!;
            pageGrid.DataSource=source;
            typeof(CreditBalanceReportPage).GetMethod("OnReportPageBound",flags)!.Invoke(page,null);
            var visibleKeys=pageGrid.Columns.Cast<DataGridViewColumn>()
                .Where(column=>column.Visible).Select(column=>column.DataPropertyName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(new[]{"Family / Student","Members","Student Advances","Available Credit"}
                    .All(visibleKeys.Contains) && !visibleKeys.Contains(FamilyAdvanceLayout.Column),
                "The family report displays identities, members, advances and totals without exposing internal JSON");
            pageGrid.Parent!.Controls.Remove(pageGrid);
            using var form=new Form{ClientSize=new Size(1100,440),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)};
            pageGrid.Dock=DockStyle.Fill;form.Controls.Add(pageGrid);form.Show();Application.DoEvents();
            using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(folder,"form.png"));form.Close();
            Check(!page.IsHandleCreated,"The form fixture performs no deferred database reads");
        }
        for(int i=2;i<30;i++)Add(i,3);
        Add(30,70); // Intentionally larger than an A4 page.
        var multipage=Build();
        using(multipage.Data)
        {
            string path=Path.Combine(folder,"multiple-families.pdf");GridReportPreview.ExportPdf(path,multipage);
            byte[] pdf=File.ReadAllBytes(path);using var parsed=UglyToad.PdfPig.PdfDocument.Open(pdf);
            var index=ReportPageSearchIndex.FromPdf(pdf);
            int familyTotals=parsed.GetPages().Sum(page=>System.Text.RegularExpressions.Regex.Matches(page.Text,"Family total").Count);
            Check(familyTotals==families.Count,"Each family total is printed once, including families continuing on another page");
            Check(parsed.NumberOfPages>2&&multipage.Data.Rows.Count==families.Sum(family=>family.Children.Length),"All children from many families and an oversized family are retained across multiple pages");
            foreach(var family in families)
            {
                var pages=new HashSet<int>();
                foreach(var child in family.Children)
                {
                    int[] namePages=index.FindPages(child.Name),placePages=index.FindPages(child.Placement);
                    Check(namePages.Length==1&&namePages.SequenceEqual(placePages),"A complete child row stays on one page: "+child.Name);
                    pages.Add(namePages.Single());
                }
                if(family.Children.Length<10)Check(pages.Count==1,"A family that fits on a page is not split: "+family.Parent);
                else Check(pages.Count>1&&pages.All(page=>index.FindPages(family.Parent).Contains(page)),"Oversized families continue between complete student rows with the parent heading repeated");
            }
            Check(index.FindPages("Grand Total").SequenceEqual(new[]{parsed.NumberOfPages}),"Grand Total appears once on the final page");
            Check(multipage.Data.AsEnumerable().Sum(row=>row.Field<decimal>("AdvanceAmount"))==families.Sum(family=>family.Children.Sum(child=>child.Advance)),"Grand total counts every student's advance exactly once");
        }
    }
}
