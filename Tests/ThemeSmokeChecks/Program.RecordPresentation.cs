using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using System.Text;
using System.Xml.Linq;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckRecordPresentation()
    {
        Check((string)FMSC_SMS.DatabaseHelper.P("@AcademicRecord","very good").Value=="Very Good",
            "Leaving-certificate academic text is title case in the database parameter");
        Check((string)FMSC_SMS.DatabaseHelper.P("@AttendanceRecord","regular and punctual").Value=="Regular And Punctual",
            "Leaving-certificate attendance text is title case in the database parameter");
        Check((string)FMSC_SMS.DatabaseHelper.P("@BoardName","bise peshawar").Value=="BISE Peshawar",
            "Known abbreviations retain their proper spelling");
        foreach (string acronym in new[] { "ICS", "FA" })
        {
            Check((string)RecordTextPolicy.NormalizeParameter("@ProgramName",acronym.ToLowerInvariant())! == acronym,
                "Program saves preserve the " + acronym + " acronym");
            Check(RecordTextPolicy.DisplayText("ProgramName",acronym.ToLowerInvariant()) == acronym,
                "Previously saved program names display " + acronym + " in uppercase");
        }
        foreach(string parameter in new[]{"@AdmissionNumber","@RollNumber","@Email","@Remarks"})
            Check((string)FMSC_SMS.DatabaseHelper.P(parameter,"ab12@example.com").Value=="ab12@example.com",
                parameter+" retains its original content");
        using var field=HumanUi.TextBox(false,100);
        using var container=HumanUi.Field("Academic Record",field);
        field.Text="very good";
        typeof(Control).GetMethod("OnLeave",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(field,new object[]{EventArgs.Empty});
        Check(field.Text=="Very Good","Leaving the form field displays the same title case that will be saved");
        foreach(Control page in new Control[]{new FMSC_SMS.Pages.Students.Certificates.LeaveCertificatePage(null,false),
            new FMSC_SMS.Pages.Students.Certificates.CollegeLeaveCertificatePage(false)})
        {
            using(page)
            {
                var reason=(ModernTextBox)page.GetType().GetField("remarks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(page)!;
                reason.Text="on parents request";
                typeof(Control).GetMethod("OnLeave",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(reason,new object[]{EventArgs.Empty});
                Check(reason.Text=="On Parents Request","The leaving-certificate reason is shown in title case");
            }
        }
        using var old=new DataTable();
        foreach(string column in new[]{"StudentName","FatherName","AcademicRecord","ClassName","AdmissionNumber","Remarks"})old.Columns.Add(column);
        old.Rows.Add("ali ahmad","muhammad khan","very good","class viii","8a","a sentence stays unchanged.");
        using var printed=RecordTextPolicy.PrintCopy(old);
        Check(printed.Rows[0].Field<string>("StudentName")=="Ali Ahmad" && printed.Rows[0].Field<string>("FatherName")=="Muhammad Khan"
            && printed.Rows[0].Field<string>("AcademicRecord")=="Very Good" && printed.Rows[0].Field<string>("ClassName")=="Class VIII",
            "Older lowercase data prints in title case, including class Roman numerals");
        Check(old.Rows[0].Field<string>("StudentName")=="ali ahmad" && printed.Rows[0].Field<string>("AdmissionNumber")=="8a"
            && printed.Rows[0].Field<string>("Remarks")=="a sentence stays unchanged.",
            "Printing preserves stored snapshots, identifiers and prose");

        using (var image=new Bitmap(220,60))
        using (var bytes=new MemoryStream())
        {
            image.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);
            foreach(string template in new[]{"BankAccountOpeningCertificate","CharacterCertificate",
                "CollegeLeaveCertificate","DateOfBirthCertificate","LeaveCertificate","NoObjectionCertificate",
                "ProvisionalCertificate","RegularLecturerCertificate","RegularStudentCertificate","SportsCertificate"})
            {
                string path=Path.Combine(AppContext.BaseDirectory,"Reports",template+".rdlc");
                string original=File.ReadAllText(path);
                Check(CertificateSignature.Apply(original,bytes.ToArray(),"image/png",false)==original,
                    template+": disabled principal signature preserves existing layout");
                string signed=CertificateSignature.Apply(original,bytes.ToArray(),"image/png",true);
                var xml=XDocument.Parse(signed);
                XNamespace ns=xml.Root!.Name.Namespace;
                Check(xml.Descendants(ns+"Image").Count(item=>(string?)item.Attribute("Name")==
                    "PrincipalSignaturePrintImage")==1 &&
                    xml.Descendants(ns+"EmbeddedImage").Any(item=>(string?)item.Attribute("Name")==
                    "PrincipalSignaturePrintAsset"),
                    template+": enabled principal signature is embedded over the existing signing line");
            }
        }

        string folder=Path.GetFullPath("artifacts/gender-certificates");Directory.CreateDirectory(folder);
        using var profile=new DataTable();profile.Columns.Add("SchoolName");var school=profile.Rows.Add("fictional school");
        foreach(string name in new[]{"BankAccountOpeningCertificate","CharacterCertificate","CollegeLeaveCertificate",
            "DateOfBirthCertificate","LeaveCertificate","NoObjectionCertificate","ProvisionalCertificate",
            "RegularLecturerCertificate","RegularStudentCertificate","SportsCertificate"})
        {
            string path=Path.Combine(AppContext.BaseDirectory,"Reports",name+".rdlc");
            var document=CertificateLetterhead.UsesPreprintedPad(path)?XDocument.Load(path):XDocument.Parse(CertificateLetterhead.Definition(path,school));
            XNamespace ns=document.Root!.Name.Namespace;
            var dataset=document.Descendants(ns+"DataSet").Single();
            using var source=new DataTable();
            foreach(var item in dataset.Element(ns+"Fields")!.Elements(ns+"Field"))
            {
                string type=item.Elements().FirstOrDefault(e=>e.Name.LocalName=="TypeName")?.Value??"System.String";
                source.Columns.Add(item.Element(ns+"DataField")!.Value,Type.GetType(type,true)!);
            }
            var row=source.NewRow();
            foreach(DataColumn column in source.Columns)
                row[column]=column.DataType==typeof(string)?column.ColumnName=="StudentName"||column.ColumnName=="StaffName"?"ali ahmad":
                    column.ColumnName=="FatherName"?"muhammad khan":"example":column.DataType==typeof(DateTime)?new DateTime(2026,10,24):
                    column.DataType==typeof(bool)?true:Convert.ChangeType(1,column.DataType);
            row["Gender"]="Female";source.Rows.Add(row);
            foreach(var sample in new[]{(Gender:" male ",Title:"Mr.",Relation:"S/O",Pronoun:"he"),
                (Gender:"female",Title:"Ms.",Relation:"D/O",Pronoun:"she"),(Gender:"",Title:"Mr./Ms.",Relation:"S/D/O",Pronoun:"he/she")})
            {
                using var data=CertificateUi.PrintData(path,source,_=>sample.Gender);
                if(CertificateLetterhead.UsesPreprintedPad(path))
                    Check(data.Rows[0].Field<string>("Remarks")=="Example" && source.Rows[0].Field<string>("Remarks")=="example",
                        "Old leaving reasons print in title case without rewriting the original record");
                using var report=new LocalReport();report.EnableExternalImages=true;
                report.LoadReportDefinition(new StringReader(document.ToString()));
                report.DataSources.Add(new ReportDataSource((string)dataset.Attribute("Name")!,data));
                string html=System.Net.WebUtility.HtmlDecode(Encoding.UTF8.GetString(report.Render("HTML4.0")));
                Check(html.Contains("Ali Ahmad",StringComparison.Ordinal)&&html.Contains("Muhammad Khan",StringComparison.Ordinal),
                    name+" renders older lowercase names in title case");
                if(name is "LeaveCertificate" or "CollegeLeaveCertificate")
                    Check(html.Contains("Class in which "+sample.Pronoun+" was studying",StringComparison.Ordinal),name+" uses the recorded gender in its class labels");
                else if(name!="ProvisionalCertificate")
                {
                    Check(html.Contains(sample.Title,StringComparison.Ordinal),name+" uses the appropriate title for "+sample.Gender);
                    Check(html.Contains(sample.Relation,StringComparison.Ordinal),name+" uses the appropriate parent reference for "+sample.Gender);
                    if(sample.Gender.Length>0)
                        Check(!html.Contains("Mr./Ms.",StringComparison.Ordinal)&&!html.Contains("S/D/O",StringComparison.Ordinal)
                            &&!html.Contains("his/her",StringComparison.OrdinalIgnoreCase),name+" does not retain combined references for a specified gender");
                }
                else Check(html.Contains(sample.Relation,StringComparison.Ordinal),name+" uses the appropriate parent reference");
                Check(source.Rows[0].Field<string>("Gender")=="Female","Print wording does not rewrite the saved gender snapshot");
                if(sample.Gender=="female" && name is "LeaveCertificate" or "RegularStudentCertificate")
                    File.WriteAllBytes(Path.Combine(folder,name+"-female.png"),report.Render("IMAGE","<DeviceInfo><OutputFormat>PNG</OutputFormat><DpiX>110</DpiX><DpiY>110</DpiY></DeviceInfo>"));
            }
        }
    }
}
