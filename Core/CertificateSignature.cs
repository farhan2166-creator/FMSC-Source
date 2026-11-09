using System.Globalization;
using System.Xml.Linq;

namespace FMSC_SMS.Core;

/// <summary>Places the School Profile signature over the existing Principal line, only when enabled.</summary>
internal static class CertificateSignature
{
    internal static string Apply(string definition, byte[]? image, string? contentType, bool enabled)
    {
        if (!enabled || image is null || image.Length == 0) return definition;
        if (contentType is not ("image/png" or "image/jpeg"))
            throw new InvalidDataException("The saved principal signature format is not supported.");

        var document = XDocument.Parse(definition);
        XNamespace ns = document.Root!.Name.Namespace;
        XElement line = document.Descendants(ns + "Line")
            .Single(item => (string?)item.Attribute("Name") == "lnPrincipal");
        string Inches(double value) => value.ToString("0.####", CultureInfo.InvariantCulture) + "in";
        double lineTop = double.Parse(line.Element(ns + "Top")!.Value.Replace("in", ""),
            CultureInfo.InvariantCulture);
        double lineLeft = double.Parse(line.Element(ns + "Left")!.Value.Replace("in", ""),
            CultureInfo.InvariantCulture);
        double lineWidth = double.Parse(line.Element(ns + "Width")!.Value.Replace("in", ""),
            CultureInfo.InvariantCulture);

        const double height = 0.38;
        // Leave the existing signature line and label intact for a natural print.
        XElement signature = new(ns + "Image",
            new XAttribute("Name", "PrincipalSignaturePrintImage"),
            new XElement(ns + "Source", "Embedded"),
            new XElement(ns + "Value", "PrincipalSignaturePrintAsset"),
            new XElement(ns + "Sizing", "FitProportional"),
            new XElement(ns + "Top", Inches(Math.Max(0, lineTop - height - 0.02))),
            new XElement(ns + "Left", Inches(lineLeft + 0.12)),
            new XElement(ns + "Height", Inches(height)),
            new XElement(ns + "Width", Inches(Math.Max(0.5, lineWidth - 0.24))),
            new XElement(ns + "Style"));
        line.AddAfterSelf(signature);

        XElement embeddedImages = document.Root.Element(ns + "EmbeddedImages")
            ?? new XElement(ns + "EmbeddedImages");
        embeddedImages.Add(new XElement(ns + "EmbeddedImage",
            new XAttribute("Name", "PrincipalSignaturePrintAsset"),
            new XElement(ns + "MIMEType", contentType),
            new XElement(ns + "ImageData", Convert.ToBase64String(image))));
        if (embeddedImages.Parent is null)
        {
            XElement? dataSets = document.Root.Element(ns + "DataSets");
            if (dataSets is null) document.Root.Add(embeddedImages);
            else dataSets.AddAfterSelf(embeddedImages);
        }
        return document.ToString();
    }
}
