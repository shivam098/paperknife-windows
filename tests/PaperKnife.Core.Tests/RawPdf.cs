using System.Text;

namespace PaperKnife.Core.Tests;

/// <summary>
/// Hand-written single-page PDFs (uncompressed, Latin-1) for content that PDFium can't create
/// itself, such as inline images, shadings and character spacing.
/// </summary>
internal static class RawPdf
{
    public const string BaseContent =
        "BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n" +
        "BT /F1 12 Tf 72 660 Td (This line must not move) Tj ET\n" +
        "0.86 0.16 0.16 rg 72 500 200 100 re f\n";

    public const string ShadedArea = "q 300 500 200 100 re W n /Sh1 sh Q\n";

    public const string InlineImage =
        "q 60 0 0 60 400 300 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID " +
        "\u00FF\u0000\u0000\u0000\u00FF\u0000\u0000\u0000\u00FF\u00FF\u00FF\u0000\nEI Q\n";

    public const string CharacterSpacing = "BT /F1 14 Tf 4 Tc 72 300 Td (Spaced out letters) Tj ET\n";

    public const string KernedText = "BT /F1 18 Tf 72 250 Td [(Ke) -300 (rned text)] TJ ET\n";

    public static PdfSnapshot SinglePage(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /Shading << /Sh1 6 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [300 0 500 0] /Extend [true true] " +
                "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>",
        ];

        var pdf = new StringBuilder("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfSnapshot.FromBytes(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
