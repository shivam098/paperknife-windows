namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    /// <summary>What a session reads from a page (DocumentTests).</summary>
    private static CorpusCase[] DocumentCases =>
    [
        new(
            "document-text-runs",
            "The text runs of a plain page: two lines of Helvetica, with their text, font, size, visibility and position.",
            "Every edit starts from the runs an app lists, so they must be the runs PaperKnife for Windows reads with PDFium, field by field (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            []),
    ];

    /// <summary>Text edits and their refusals (TextEditTests, and the deletion test of ContentEditTests, which covers both edit paths).</summary>
    private static CorpusCase[] EditCases =>
    [
        new(
            "edit-replace-line",
            "Replacing a line of text applies, and the other line stays exactly where it was.",
            "The basic edit must change only the edited text, never move or change the rest of the page (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "edit-basic-undo-redo",
            "Replacing a line of text, then undoing and redoing the edit; a second redo has nothing to redo.",
            "Undo and redo must restore the exact earlier files, byte for byte (v0.1.0), and the edit must be appended to the file, keeping every original byte (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello PaperKnife"), Undo(), Redo(), Redo()]),

        new(
            "edit-character-the-font-lacks",
            "Typing Ω, which the PDF's Helvetica can't draw, is refused, naming the character, and changes nothing.",
            "PDFium silently draws characters a font lacks as its missing-glyph box (or code 0); they must be refused instead (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello Ω")]),

        new(
            "edit-unsupported-layout-devanagari",
            "Typing Devanagari (नमस्ते), which needs text shaping, is refused.",
            "Text is set one character at a time, without shaping, so Hindi, Arabic or Thai would come out wrong; it must be refused (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "नमस्ते")]),

        new(
            "edit-unsupported-layout-line-feed",
            "Typing a line break (U+000A) into a line is refused.",
            "A run is a single line: a line break would be drawn as a missing glyph rather than start a new line, so it must be refused (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello\nWorld")]),

        new(
            "edit-unsupported-layout-line-separator",
            "Typing a Unicode line separator (U+2028) into a line is refused.",
            "A run is a single line: a line separator would be drawn as a missing glyph rather than start a new line, so it must be refused (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello\u2028World")]),

        new(
            "edit-unsupported-layout-emoji",
            "Typing an emoji outside the Basic Multilingual Plane (😀) is refused.",
            "Characters outside the Basic Multilingual Plane aren't supported yet; they must be refused, not written as code 0, as PDFium writes characters it can't encode (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hi 😀")]),

        new(
            "edit-compatibility-variant-superscript",
            "Changing “Area 50 m2” to “Area 50 m²” is a real edit and applies.",
            "Comparing text after compatibility normalization (² as 2) would report a visible change as no change; only canonically equivalent text is the same (v0.1.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 72 700 Td (Area 50 m2) Tj ET\n"),
            [Replace("Area 50 m2", "Area 50 m²")]),

        new(
            "edit-compatibility-variant-ellipsis",
            "Changing “Wait...” to “Wait…” is a real edit and applies.",
            "Comparing text after compatibility normalization (… as ...) would report a visible change as no change; only canonically equivalent text is the same (v0.1.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 72 700 Td (Wait...) Tj ET\n"),
            [Replace("Wait...", "Wait…")]),

        new(
            "edit-keeps-kerning-of-other-text",
            "Editing a line on a page that also has text kerned with a TJ adjustment applies.",
            "An edit must keep the kerning of the other text on the page as it was (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.KernedText),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "edit-kerned-run",
            "Editing the run that is set with a TJ kerning adjustment applies.",
            "Kerning survives PDFium's page rewrite, so text set with TJ adjustments must stay editable rather than be refused like letter spacing (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.KernedText),
            [Replace("Ke rned text", "Kerned text")]),

        new(
            "edit-run-followed-by-text-on-the-same-line",
            "Editing a table cell followed, far along the same line, by an amount applies.",
            "PDFium reports the gap before the amount as a generated space at the end of the cell's run, which must neither end up in the edit nor fail its check (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent
                + "BT /F1 12 Tf 72 400 Td (Design and printing) Tj ET\n"
                + "BT /F1 12 Tf 460 400 Td ($1,250.00) Tj ET\n"),
            [Replace("Design and printing", "Design and print")]),

        new(
            "edit-same-text-no-change",
            "Replacing a line with the same text reports no change.",
            "An edit that changes nothing must neither modify the file nor add an undo step (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", "Hello World")]),

        new(
            "edit-object-that-is-not-text",
            "Replacing the text of page object 2, a filled rectangle, is refused as an invalid target.",
            "Edits must only ever change text objects; any other target must be refused, leaving the file as it was (v0.1.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [ReplaceObject(2, "x")]),

        new(
            "edit-font-character-map-needing-a-dictionary",
            "Replacing a line whose font's character map is Flate data asking for a preset dictionary, which a PDF can't give: the map counts as damaged, the edit applies, and undo restores the file.",
            "zlib refuses such data with an exception PaperKnife didn't treat as damaged data, so reading the page failed and the page couldn't even be shown (found while porting PaperKnife to Android, fixed after v0.5.0).",
            CharacterMapNeedingADictionary,
            [Replace("Hello World", "Hello PaperKnife"), Undo()]),

        new(
            "edit-delete-line-content-stream",
            "Replacing a line with nothing deletes it in the content stream, the other line staying where it was; undo brings it back.",
            "Deleting a whole line was refused (“isn't supported yet”) until v0.4.0; the deletion must be appended to the file and undone exactly.",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", string.Empty), Undo()]),

        new(
            "edit-delete-line-pdfium-rewrite",
            "Replacing a line with nothing deletes it through PDFium's page rewrite, the other line staying where it was; undo brings it back.",
            "Deleting a whole line was refused (“isn't supported yet”) until v0.4.0, also on PDFium's path, which encrypted files take.",
            () => RawPdf.SinglePage(RawPdf.BaseContent),
            [Replace("Hello World", string.Empty, contentEditing: false), Undo()]),
    ];

    /// <summary>Edits in an embedded TrueType font (TextEditTests): DejaVu Sans, where the tests use Arial; neither has U+3000.</summary>
    private static CorpusCase[] TrueTypeCases =>
    [
        new(
            "truetype-edit",
            "Editing text in a fully embedded TrueType font applies; truetype-edit-trailing-ideographic-space must give the same page.",
            "Text in embedded TrueType fonts must stay editable, drawn with the font's own glyphs (v0.1.0).",
            () => TestPdfs.WithEmbeddedFont(TestPdfs.DejaVuSansPath, "Invoice 2024"),
            [Replace("Invoice 2024", "Invoice 2025")]),

        new(
            "truetype-edit-trailing-ideographic-space",
            "The edit of truetype-edit with an ideographic space (U+3000), which the font lacks, typed at the end: it applies and the space is dropped.",
            "PDFium would draw the font's missing-glyph box for trailing whitespace the font can't draw; such whitespace is dropped instead (v0.1.0).",
            () => TestPdfs.WithEmbeddedFont(TestPdfs.DejaVuSansPath, "Invoice 2024"),
            [Replace("Invoice 2024", "Invoice 2025\u3000")]),

        new(
            "truetype-ideographic-space-inside-text",
            "Typing an ideographic space (U+3000), which the embedded TrueType font lacks, between two words is refused.",
            "PDFium silently draws characters a font lacks as its missing-glyph box, and whitespace inside the text can't just be dropped, so it must be refused (v0.1.0).",
            () => TestPdfs.WithEmbeddedFont(TestPdfs.DejaVuSansPath, "Invoice 2024"),
            [Replace("Invoice 2024", "Invoice\u30002025")]),

        new(
            "truetype-ideographic-space-inside-text-then-edit",
            "An ideographic space (U+3000) between two words is refused, then the same edit without it applies.",
            "PDFium silently draws characters a font lacks as its missing-glyph box (v0.1.0); a refused edit must leave the session exactly as it was.",
            () => TestPdfs.WithEmbeddedFont(TestPdfs.DejaVuSansPath, "Invoice 2024"),
            [Replace("Invoice 2024", "Invoice\u30002025"), Replace("Invoice 2024", "Invoice 2025")]),
    ];

    /// <summary>
    /// The base page, with Helvetica's character map stored as Flate data that asks for a preset
    /// dictionary (a zlib header with FDICT set, then a stored block), which a PDF can't give.
    /// </summary>
    private static PdfSnapshot CharacterMapNeedingADictionary() => RawPdf.WriteWithTable(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        RawPdf.Stream(RawPdf.BaseContent),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
        "<< /Length 12 /Filter /FlateDecode >>\nstream\n\u0078\u00BB\u0000\u0000\u0000\u0001\u0001\u0001\u0000\u00FE\u00FF\u0041\nendstream",
    ]);
}
