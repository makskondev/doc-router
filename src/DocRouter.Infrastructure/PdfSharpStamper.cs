using DocRouter.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace DocRouter.Infrastructure;

/// <summary>Накладывает PNG поверх страницы PDF (PDFsharp 6.x, MIT). Повёрнутые страницы (/Rotate) пока не учитываются.</summary>
public sealed class PdfSharpStamper : IPdfStamper
{
    public byte[] Apply(byte[] pdf, StampImage stamp)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        var p = stamp.Placement;
        var index = p.Page > 0 ? p.Page - 1 : doc.PageCount - 1;
        if (index < 0 || index >= doc.PageCount)
            throw new InvalidOperationException($"В PDF {doc.PageCount} стр., страницы {p.Page} нет");

        using var pngStream = new MemoryStream(stamp.Png);
        using var image = XImage.FromStream(pngStream);
        using (var gfx = XGraphics.FromPdfPage(doc.Pages[index], XGraphicsPdfPageOptions.Append))
        {
            gfx.DrawImage(image, p.X, p.Y, p.Width, p.Height);
        }

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }
}
