using System.Xml.Linq;

namespace FMSC_SMS.Core;

internal enum CertificatePaperMode { PlainPaper, PreprintedLetterhead }

internal static class CertificateLetterhead
{
    internal static bool UsesPreprintedPad(string path) =>
        Path.GetFileName(path) is "LeaveCertificate.rdlc" or "CollegeLeaveCertificate.rdlc";

    internal static CertificatePaperMode DefaultMode(string path) =>
        UsesPreprintedPad(path) ? CertificatePaperMode.PreprintedLetterhead : CertificatePaperMode.PlainPaper;

    internal static string PreferenceKey(string path) =>
        "CertificatePaper." + Path.GetFileNameWithoutExtension(path);

    internal static CertificatePaperMode PreferredMode(string path) =>
        ReportLayoutStore.GetPreference(PreferenceKey(path)) switch
        {
            "PlainPaper" => CertificatePaperMode.PlainPaper,
            "PreprintedLetterhead" => CertificatePaperMode.PreprintedLetterhead,
            _ => DefaultMode(path)
        };

    internal static string Definition(string path, DataRow? profile, DataRow? certificate = null, IReadOnlyList<string>? provisionalSubjects = null, CertificatePaperMode mode = CertificatePaperMode.PlainPaper)
    {
        var document = XDocument.Load(path);
        XNamespace ns = document.Root!.Name.Namespace;
        var page = document.Root.Element(ns + "Page")!;
        // Each certificate has the same physical body position in both paper modes.
        // Reserve matching stationery bands even when the paper already carries them.
        if (UsesPreprintedPad(path))
        {
            var stationery = XDocument.Load(Path.Combine(Path.GetDirectoryName(path)!, "CharacterCertificate.rdlc"));
            var stationeryPage = stationery.Root!.Element(ns + "Page")!;
            page.AddFirst(new XElement(stationeryPage.Element(ns + "PageHeader")!));
            page.Element(ns + "PageHeader")!.AddAfterSelf(new XElement(stationeryPage.Element(ns + "PageFooter")!));

            // The historical pad templates contained literal "(" and ")" text runs,
            // leaving a misleading red mark on original certificates.
            var copyLabel = document.Descendants(ns + "Textbox")
                .Single(element => (string?)element.Attribute("Name") == "txtCopyType");
            var copyRuns = copyLabel.Element(ns + "Paragraphs")!.Element(ns + "Paragraph")!
                .Element(ns + "TextRuns")!;
            foreach (var run in copyRuns.Elements(ns + "TextRun")
                .Where(run => (string?)run.Element(ns + "Value") is "(" or ")").ToArray())
                run.Remove();
        }
        page.SetElementValue(ns + "TopMargin", "0.4in");
        page.SetElementValue(ns + "BottomMargin", "0.4in");
        if (mode == CertificatePaperMode.PreprintedLetterhead)
        {
            // Keep the empty header/footer regions as spacers; do not print
            // the school's artwork or contact details over preprinted stationery.
            page.Element(ns + "PageHeader")!.Element(ns + "ReportItems")?.RemoveNodes();
            page.Element(ns + "PageFooter")!.Element(ns + "ReportItems")?.RemoveNodes();
        }
        else
        {
            string assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
            foreach (var item in new[] { ("CertificateHeaderImage", "header.png") })
            {
                string imagePath = Path.Combine(assetDirectory, item.Item2);
                if (!File.Exists(imagePath))
                    throw new FileNotFoundException($"The certificate image is missing: Assets/{item.Item2}.", imagePath);
                var image = document.Descendants(ns + "Image").Single(element => (string?)element.Attribute("Name") == item.Item1);
                image.Element(ns + "Value")!.Value = new Uri(imagePath).AbsoluteUri;
                using var bitmap = new Bitmap(imagePath);
                // Each template reserves its own header height; keep the image proportional within it.
                double height = double.Parse(image.Element(ns + "Height")!.Value[..^2], System.Globalization.CultureInfo.InvariantCulture);
                double width = Math.Min(7.45, height * bitmap.Width / bitmap.Height);
                image.Element(ns + "Width")!.Value = width.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "in";
                image.SetElementValue(ns + "Left", ((7.45 - width) / 2).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "in");
            }
            string Text(string column) => profile is null ? string.Empty : RecordTextPolicy.DisplayText(column,CertificateUi.GetString(profile, column));
            if(Path.GetFileName(path)=="RegularStudentCertificate.rdlc" && !string.IsNullOrWhiteSpace(Text("SchoolName")))
                document.Descendants(ns+"Textbox").Single(element=>(string?)element.Attribute("Name")=="txtInstitution")
                    .Descendants(ns+"Value").Single().Value=Text("SchoolName");
            string address = string.Join(" ", new[] { Text("AddressLine"), Text("City") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            string contact = Text("ContactNumber");
            string email = Text("Email");
            string details = string.Join("   |   ", new[]
            {
                string.IsNullOrWhiteSpace(address) ? string.Empty : "Address: " + address,
                string.IsNullOrWhiteSpace(contact) ? string.Empty : "Cell: " + contact
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            foreach (var item in new[] { ("CertificateFooterContact", details), ("CertificateFooterEmail", string.IsNullOrWhiteSpace(email) ? string.Empty : "Email: " + email) })
            {
                var textbox = document.Descendants(ns + "Textbox").Single(element => (string?)element.Attribute("Name") == item.Item1);
                textbox.Descendants(ns + "Value").Single().Value = item.Item2;
            }
        }
        if (Path.GetFileName(path) == "ProvisionalCertificate.rdlc" && certificate is not null)
        {
            if (provisionalSubjects is not null) ProvisionalSubjectLayout.Apply(document, provisionalSubjects);
            else ProvisionalSubjectLayout.Apply(document, CertificateUi.GetString(certificate, "SubjectsPassedLine"));
        }
        return document.ToString();
    }
}
