namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    /// <summary>Edits in the page's content stream, appended as incremental updates (ContentEditTests).</summary>
    private static CorpusCase[] ContentCases =>
    [
        new(
            "content-keeps-inline-image",
            "Editing a line on a page with an inline image applies, appended to the file.",
            "PDFium's page rewrite drops inline images, so such pages were refused; edits in the content stream keep every other byte (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.InlineImage),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-keeps-shading",
            "Editing a line on a page with a shading (a gradient) applies, appended to the file.",
            "PDFium's page rewrite can't write shadings back, so such pages were refused; edits in the content stream keep every other byte (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.ShadedArea),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-keeps-character-spacing",
            "Editing a line on a page with letter-spaced (Tc) text elsewhere applies, appended to the file.",
            "PDFium's page rewrite drops letter spacing, so such pages were refused; edits in the content stream keep every other byte (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.CharacterSpacing),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-keeps-spaced-text-under-the-edit",
            "Editing a line with a small letter-spaced word inside its own area applies, appended to the file.",
            "PDFium's page rewrite would drop the small word's letter spacing, so the edit was refused; edits in the content stream keep every other byte (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + "BT /F1 6 Tf 4 Tc 80 704 Td (tiny) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-keeps-cmyk-color",
            "Editing text in a print (CMYK) color applies, and the new text keeps that color.",
            "PDFium's page rewrite writes only RGB and gray colors, so CMYK text was refused; edits in the content stream keep the text's own color (v0.2.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 0 1 1 0 k 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-edited-text-spacing-tc",
            "Editing text with 3 pt letter spacing (Tc) keeps the spacing in the new text, so it is wider than in content-edited-text-spacing-plain.",
            "PDFium's page rewrite drops letter spacing, so such text was refused; edits in the content stream keep the edited text's own spacing (v0.2.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 3 Tc 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-edited-text-spacing-tw",
            "Editing text with 8 pt word spacing (Tw) keeps the spacing in the new text, so it is wider than in content-edited-text-spacing-plain.",
            "PDFium's page rewrite drops word spacing, so such text was refused; edits in the content stream keep the edited text's own spacing (v0.2.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 8 Tw 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-edited-text-spacing-plain",
            "The edit of content-edited-text-spacing-tc and -tw in text without letter or word spacing, for comparing widths.",
            "Edits in the content stream must keep the edited text's own spacing, no more and no less (v0.2.0).",
            () => RawPdf.SinglePage("BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n"),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-type3-edit",
            "Editing text in a Type 3 font, using letters the font has, applies, appended to the file.",
            "PDFium's page rewrite drops Type 3 text, so PDFs saved from Chrome (Skia) couldn't be edited at all; edits in the content stream keep it (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "cab")]),

        new(
            "content-type3-letter-the-font-lacks",
            "Typing a letter the Type 3 font has no glyph for is refused, naming it.",
            "PDFium can't outline Type 3 glyphs, so letters are checked against the font's glyph procedures; a missing one must be refused, not drawn as nothing (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "abd")]),

        new(
            "content-type3-letter-the-font-lacks-after-a-space",
            "The edit of content-type3-letter-the-font-lacks with a space before the letter: only the letter is named, as the space is drawn as a gap.",
            "Letters a Type 3 font lacks must be refused (v0.2.0); a space it lacks isn't one of them, since it is drawn as a gap (v0.3.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "ab d")]),

        new(
            "content-type3-space-as-gap",
            "Typing a space in a Type 3 font without a space glyph, on a page with no gaps to copy, leaves a gap of a third of an em.",
            "Fonts without a space glyph, such as TeX's, refused every edit with a space; a space is now drawn as a TJ gap, as TeX does (v0.3.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "ab ca")]),

        new(
            "content-type3-keeps-kerning-and-gaps",
            "Extending text set the way TeX sets it keeps the kerning and gaps of the unchanged letters, and gives the new space the page's usual gap.",
            "Rewriting a line lost the kerning and word gaps of the text the edit didn't change (v0.3.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.GappedText),
            [Replace("ab ca", "ab cab c")]),

        new(
            "content-object-streams",
            "Two edits in a file whose objects and cross-reference data are in compressed streams (PDF 1.5) are both appended, with a cross-reference stream.",
            "Files with object streams and cross-reference streams must be read and updated in the same form, keeping every original byte (v0.2.0).",
            () => RawPdf.Build([[RawPdf.BaseContent]], useObjectStreams: true),
            [Replace("Hello World", "Hello PaperKnife"), Replace("This line must not move", "This line stays")]),

        new(
            "content-several-streams",
            "Editing a line on a page drawn by two content streams applies, and the line drawn by the other stream doesn't move.",
            "Pages drawn by several content streams must be edited where the text is, keeping the other streams as they are (v0.2.0).",
            () => RawPdf.Build([["BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n", "BT /F1 12 Tf 72 660 Td (This line must not move) Tj ET\n"]]),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-streams-joined-with-a-space",
            "Editing a line whose red color is hidden by a comment at the end of the previous content stream: the text stays black.",
            "PDFium joins a page's content streams with a space, so a comment at the end of one stream also hides the start of the next; edits must read them the same way (v0.2.0).",
            () => RawPdf.Build([["% stamp", "1 0 0 rg\nBT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n"]]),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-shared-with-another-page",
            "Editing a line whose content stream the second page draws too: the second page keeps the original text.",
            "Content shared with another page must not be changed in place, or the edit would appear on both pages (v0.2.0).",
            () => RawPdf.Build([[RawPdf.BaseContent], ["(unused) Tj"]], shareContent: true),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "content-line-operators-replace",
            "Replacing a line shown with the ' operator, in a block that also uses \", TD and TL, leaves the other lines exactly where they were.",
            "The line operators ', \", TD and TL move to the next line by the leading, so rewriting one line must keep the leading and every other line's position (v0.2.0).",
            () => RawPdf.SinglePage(LineOperators),
            [Replace("Second line", "2nd line")]),

        new(
            "content-line-operators-delete",
            "Deleting the line of content-line-operators-replace leaves the other lines exactly where they were.",
            "The line operators ', \", TD and TL move to the next line by the leading, so deleting one line must keep every other line's position (v0.2.0, deletion v0.4.0).",
            () => RawPdf.SinglePage(LineOperators),
            [Replace("Second line", string.Empty)]),

        new(
            "content-relative-moves-exact-positions",
            "Editing the first line of a block placed with relative Td moves leaves the other lines at exactly the same positions.",
            "PDFium adds up Td moves in single precision; lines placed again with Tm landed a fraction of a point off, so edits were refused as changing pixels elsewhere (v0.5.0).",
            () => RawPdf.SinglePage("BT /F1 10 Tf 392.432 685.111 Td (First) Tj 56.276 -11.956 Td (Second) Tj -303.981 -11.956 Td (Third) Tj 0 -11.955 Td (Fourth) Tj ET\n"),
            [Replace("First", "1st")]),

        new(
            "content-delete-keeps-invisible-text-after",
            "Deleting a line keeps the invisible text shown right after it exactly where it was.",
            "Deleting text must keep invisible text, such as a scanned page's searchable layer, exactly where it was (v0.4.0).",
            () => RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (Visible) Tj 3 Tr (hidden words) Tj ET\n"),
            [Replace("Visible", string.Empty)]),

        new(
            "content-delete-after-hyphenated-line",
            "Deleting the line that follows a line ending in a hyphen applies.",
            "PDFium reads a hyphen at the end of a line as U+0002 only while the next line follows it, so the check of the remaining text refused the deletion (v0.4.0).",
            () => RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (A long hyphen-) Tj 0 -14 Td (ated word.) Tj ET\n"),
            [Replace("ated word.", string.Empty)]),

        new(
            "content-delete-after-soft-hyphen-line",
            "Deleting the line that follows a line ending in a hyphen that the font maps to U+00AD SOFT HYPHEN applies.",
            "Some word processors map their hyphen to a soft hyphen, which PDFium also reads as U+0002 at the end of a line, so the deletion was refused (v0.4.0).",
            SoftHyphenLine,
            [Replace("ated word.", string.Empty)]),

        new(
            "content-delete-fake-bold",
            "Deleting a heading drawn twice, slightly apart, to look bold: each deletion removes one copy, so two remove it all.",
            "PDFium reads only the first of two stacked copies; deleting fake-bold text must remove one copy at a time and keep the rest of the page (v0.4.0).",
            () => RawPdf.SinglePage("BT /F1 18 Tf 72 600 Td (Bold Heading) Tj ET\nBT /F1 18 Tf 72.4 600 Td (Bold Heading) Tj ET\nBT /F1 12 Tf 72 500 Td (Body) Tj ET\n"),
            [Replace("Bold Heading", string.Empty), Replace("Bold Heading", string.Empty)]),

        new(
            "content-delete-cjk-text",
            "Deleting Chinese text in a font with one of PDFium's predefined CMaps, which PaperKnife can't encode, applies, appended to the file.",
            "Text in a font PaperKnife can't encode couldn't be changed at all; deleting it needs no encoding, so it must work (v0.4.0).",
            ChineseText,
            [Replace("中文", string.Empty)]),

        new(
            "content-text-right-after-other-text",
            "Editing a line drawn as two text shows, the second placed right after the first, moves the second along to fit.",
            "Text placed right after other text, with no position of its own, must move with the edited text rather than stay put and overlap it (v0.2.0).",
            () => RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (Hello) Tj ( World) Tj ET\n"),
            [Replace("Hello World", "Hi World")]),

        new(
            "content-type3-undo-redo",
            "An edit in a Type 3 font, undone and redone: undo restores the original file and redo the edited one, byte for byte, which is what is saved.",
            "Undo and redo must restore the exact files also after edits appended to the file (v0.2.0).",
            () => RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text),
            [Replace("abc", "cab"), Undo(), Redo()]),
    ];

    /// <summary>A block whose lines move with ', " and TD, and a later one whose T* uses the leading TD set.</summary>
    private const string LineOperators =
        "BT /F1 12 Tf 14 TL 72 600 Td (First line) Tj (Second line) ' 2 0 (Third line) \" 0 -20 TD (Fourth) Tj ET\n" +
        "BT /F1 12 Tf 72 400 Td (After) Tj T* (Next) Tj ET\n";

    /// <summary>A line ending in a hyphen, in a Helvetica whose character map says the hyphen is U+00AD SOFT HYPHEN.</summary>
    private static PdfSnapshot SoftHyphenLine() => RawPdf.WriteWithTable(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        RawPdf.Stream("BT /F1 12 Tf 72 600 Td (A long hyphen-) Tj 0 -14 Td (ated word.) Tj ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
        RawPdf.Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Soft def /CMapType 2 def\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n1 beginbfchar <2D> <00AD> endbfchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end"),
    ]);

    /// <summary>The base content and Chinese text in STSong-Light, with the UniGB-UCS2-H CMap PDFium has built in.</summary>
    private static PdfSnapshot ChineseText() => RawPdf.WriteWithTable(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F4 6 0 R >> >> >>",
        RawPdf.Stream(RawPdf.BaseContent + "BT /F4 24 Tf 72 500 Td <4E2D6587> Tj ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /UniGB-UCS2-H /DescendantFonts [7 0 R] >>",
        "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> " +
            "/FontDescriptor 8 0 R /DW 1000 >>",
        "<< /Type /FontDescriptor /FontName /STSong-Light /Flags 6 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 " +
            "/CapHeight 880 /StemV 93 >>",
    ]);
}
