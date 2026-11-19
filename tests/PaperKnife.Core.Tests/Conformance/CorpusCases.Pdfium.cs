namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    /// <summary>
    /// Edits through PDFium's page rewrite, which PaperKnife uses where it doesn't edit the content
    /// stream itself, for example in encrypted files (TextEditTests and ContentEditTests).
    /// </summary>
    private static CorpusCase[] PdfiumCases =>
    [
        new(
            "pdfium-inline-image-refused",
            "Through PDFium's page rewrite, editing a line on a page with an inline image is refused.",
            "PDFium's page rewrite drops inline images, so the page would lose its image (refused since v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.InlineImage),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-character-spacing-refused",
            "Through PDFium's page rewrite, editing a line on a page with letter-spaced (Tc) text elsewhere is refused.",
            "PDFium's content generator doesn't write Tc, so the other, spaced-out line would shrink (refused since v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.CharacterSpacing),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-own-style-cmyk",
            "Through PDFium's page rewrite, editing text in a print (CMYK) color is refused.",
            "PDFium's content generator writes only RGB and gray colors, so the edited text would change color (refused since v0.1.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 0 1 1 0 k 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-own-style-tc",
            "Through PDFium's page rewrite, editing text with letter spacing (Tc) is refused.",
            "PDFium's content generator drops letter spacing, so the edited text would lose its own spacing (refused since v0.1.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 3 Tc 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-own-style-tw",
            "Through PDFium's page rewrite, editing text with word spacing (Tw) is refused.",
            "PDFium's content generator drops word spacing, so the edited text would lose its own spacing (refused since v0.1.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 8 Tw 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-shading-refused-up-front",
            "Through PDFium's page rewrite, an edit on a page with a shading is refused up front, with the reason as its message.",
            "PDFium can't write shadings back, so the page would lose its gradient; it must be refused before anything is tried (since v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.ShadedArea),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-spaced-text-under-the-edit-refused",
            "Through PDFium's page rewrite, editing a line with a small letter-spaced word inside its own area is refused.",
            "A check that compared only the rest of the page missed changes under the edited text itself (refused since v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + "BT /F1 6 Tf 4 Tc 80 704 Td (tiny) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife", contentEditing: false)]),

        new(
            "pdfium-type3-text-refused",
            "Through PDFium's page rewrite, editing text in a Type 3 font is refused.",
            "PDFium's content generator drops text in Type 3 fonts, as in PDFs saved from Chrome, so the page would lose it (refused since v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "cab", contentEditing: false)]),

        new(
            "pdfium-respacing-invisible-text-delete",
            "Through PDFium's page rewrite, deleting a line on a page with invisible letter-spaced text is refused.",
            "PDFium's page rewrite drops letter spacing, which the page comparison can't see in invisible text such as an OCR layer, so it changed unnoticed (v0.4.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + InvisibleSpacedText),
            [Replace("Hello World", string.Empty, contentEditing: false)]),

        new(
            "pdfium-respacing-invisible-text-replace",
            "Through PDFium's page rewrite, replacing a line on a page with invisible letter-spaced text is refused.",
            "PDFium's page rewrite drops letter spacing, which the page comparison can't see in invisible text such as an OCR layer, so it changed unnoticed (v0.4.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + InvisibleSpacedText),
            [Replace("Hello World", "Hello There", contentEditing: false)]),

        new(
            "pdfium-delete-own-style-letter-spacing",
            "Through PDFium's page rewrite, deleting letter-spaced text applies, keeping the other lines.",
            "Deleting text whose own style PDFium can't write back was refused, though nothing of it remains to lose that style (v0.4.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + "BT /F1 18 Tf 4 Tc 72 400 Td (DRAFT) Tj ET\n"),
            [Replace("DRAFT", string.Empty, contentEditing: false)]),

        new(
            "pdfium-delete-own-style-cmyk",
            "Through PDFium's page rewrite, deleting text in a print (CMYK) color applies, keeping the other lines.",
            "Deleting text whose own style PDFium can't write back was refused, though nothing of it remains to lose that style (v0.4.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + "BT /F1 18 Tf 0 1 1 0 k 72 400 Td (DRAFT) Tj ET\n"),
            [Replace("DRAFT", string.Empty, contentEditing: false)]),

        new(
            "pdfium-delete-own-style-invisible-letter-spacing",
            "Through PDFium's page rewrite, deleting invisible letter-spaced text (an OCR layer) applies, keeping the other lines.",
            "Deleting text whose own style PDFium can't write back was refused, though nothing of it remains to lose that style (v0.4.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + InvisibleSpacedText),
            [Replace("hidden OCR words", string.Empty, contentEditing: false)]),
    ];

    /// <summary>Invisible (render mode 3) letter-spaced text, like the searchable layer of a scanned page.</summary>
    private const string InvisibleSpacedText = "BT /F1 12 Tf 3 Tr 5 Tc 72 300 Td (hidden OCR words) Tj ET\n";
}
