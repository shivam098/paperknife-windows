using PaperKnife.Core.Pdfium;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core.Tests;

/// <summary>Builds PDFs with PDFium at test time, so the repository never ships documents it doesn't own.</summary>
internal static unsafe class TestPdfs
{
    /// <summary>DejaVu Sans, a TrueType font that may be redistributed (see Fonts/README.md); it has no U+3000.</summary>
    public static string DejaVuSansPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    /// <summary>Letter-size pages whose only text is "Page N".</summary>
    public static PdfSnapshot NumberedPages(int count)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var document = PdfDocument.CreateNew();
            for (var i = 0; i < count; i++)
            {
                var page = FPDFPage_New(document.Handle, i, 612, 792);
                AddStandardText(document.Handle, page, $"Page {i + 1}", 72, 700, 24);
                FinishPage(page);
            }

            return Save(document);
        }
    }

    /// <summary>One page whose first text run uses the TrueType font at <paramref name="fontPath"/>, fully embedded.</summary>
    public static PdfSnapshot WithEmbeddedFont(string fontPath, string text)
    {
        var fontData = File.ReadAllBytes(fontPath);
        lock (PdfiumLibrary.Sync)
        {
            using var document = PdfDocument.CreateNew();
            var page = FPDFPage_New(document.Handle, 0, 612, 792);
            nint font;
            fixed (byte* data = fontData)
            {
                font = FPDFText_LoadFont(document.Handle, data, (uint)fontData.Length, FPDF_FONT_TRUETYPE, cid: 1);
            }

            Assert.NotEqual(nint.Zero, font);
            var textObject = FPDFPageObj_CreateTextObj(document.Handle, font, 20);
            SetText(textObject, text);
            FPDFPageObj_Transform(textObject, 1, 0, 0, 1, 72, 700);
            FPDFPage_InsertObject(page, textObject);
            AddStandardText(document.Handle, page, "Unrelated line", 72, 640, 12);
            FinishPage(page);

            var snapshot = Save(document);
            FPDFFont_Close(font);
            return snapshot;
        }
    }

    /// <summary>
    /// One page whose lines draw every character, spaces included, as a text object of its own,
    /// the way PDFs made from web pages by wkhtmltopdf (Qt) do. 12 pt Helvetica.
    /// </summary>
    public static PdfSnapshot CharacterByCharacter(params (string Text, double X, double Y)[] lines)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var document = PdfDocument.CreateNew();
            var page = FPDFPage_New(document.Handle, 0, 612, 792);
            foreach (var (text, start, y) in lines)
            {
                var x = start;
                foreach (var c in text)
                {
                    var textObject = AddStandardText(document.Handle, page, c.ToString(), x, y, 12);
                    float width = 0;
                    Assert.NotEqual(0, FPDFFont_GetGlyphWidth(FPDFTextObj_GetFont(textObject), c, 12, &width));
                    x += width;
                }
            }

            FinishPage(page);
            return Save(document);
        }
    }

    /// <summary>
    /// The document as PDFium saves it, with the creation date and the file identifier it makes up
    /// set to constants of the same length, so the same test document has the same bytes every
    /// time (the conformance corpus stores them).
    /// </summary>
    private static PdfSnapshot Save(PdfDocument document)
    {
        var bytes = document.Save().Span.ToArray();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var id = text.LastIndexOf("/ID[<", StringComparison.Ordinal);
        for (var i = id + 5; id >= 0 && i < bytes.Length && bytes[i] != (byte)']'; i++)
        {
            if (Uri.IsHexDigit((char)bytes[i]))
            {
                bytes[i] = (byte)'0';
            }
        }

        var date = text.IndexOf("/CreationDate(D:", StringComparison.Ordinal);
        if (date >= 0 && text.Length >= date + 30 && text.AsSpan(date + 16, 14).IndexOfAnyExcept("0123456789") < 0)
        {
            "20240101000000"u8.CopyTo(bytes.AsSpan(date + 16));
        }

        return PdfSnapshot.FromBytes(bytes);
    }

    private static nint AddStandardText(nint document, nint page, string text, double x, double y, float size)
    {
        nint textObject;
        fixed (byte* fontName = "Helvetica\0"u8)
        {
            textObject = FPDFPageObj_NewTextObj(document, fontName, size);
        }

        SetText(textObject, text);
        FPDFPageObj_Transform(textObject, 1, 0, 0, 1, x, y);
        FPDFPage_InsertObject(page, textObject);
        return textObject;
    }

    private static void SetText(nint textObject, string text)
    {
        fixed (char* chars = text)
        {
            Assert.NotEqual(0, FPDFText_SetText(textObject, (ushort*)chars));
        }
    }

    private static void FinishPage(nint page)
    {
        Assert.NotEqual(0, FPDFPage_GenerateContent(page));
        FPDF_ClosePage(page);
    }
}
