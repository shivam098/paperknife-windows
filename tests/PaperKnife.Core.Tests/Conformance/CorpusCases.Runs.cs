namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    /// <summary>Lines drawn one character per text object, as in PDFs made from web pages by wkhtmltopdf (TextRunTests).</summary>
    private static CorpusCase[] RunCases =>
    [
        new(
            "runs-letter-by-letter-invoice",
            "An invoice drawn one text object per character, spaces included, reads as three runs: two cells of one line, and another line.",
            "In PDFs made from web pages (wkhtmltopdf), clicking text selected a single character rather than its line or table cell; space objects, which PDFium's text page skips, must join the run (v0.1.1).",
            Invoice,
            []),

        new(
            "runs-other-color-kept-apart",
            "Letters drawn one object each form one run only while they look the same: a red letter after two black ones is a run of its own.",
            "Grouping objects into line runs (v0.1.1) must not join text of another color, which an edit of the run would then restyle.",
            () => RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (A) Tj 8.004 0 Td (B) Tj 8.004 0 Td 1 0 0 rg (C) Tj ET\n"),
            []),

        new(
            "runs-letter-by-letter-line",
            "Shortening one letter-by-letter line, then changing a word of another near the page's right edge.",
            "Clicking such a line selected one character, not the line (PDFs made from web pages by wkhtmltopdf, v0.1.1).",
            () => TestPdfs.CharacterByCharacter(("Total due", 540, 700), ("Room Rent Capping", 72, 660)),
            [Replace("Room Rent Capping", "Room Rent"), Replace("Total due", "Total paid")]),

        new(
            "runs-edit-changed-characters-only",
            "Changing the last digit of a letter-by-letter line rewrites only that character: the line keeps its 12 objects and the next cell stays where it was.",
            "Edits of letter-by-letter lines must change the fewest objects, leaving the rest of the page exactly as it was (v0.1.1).",
            Invoice,
            [Replace("Invoice 2024", "Invoice 2025")]),

        new(
            "runs-move-rest-of-line",
            "Inserting “No. ” into a letter-by-letter line moves the rest of the line along by its width, without touching the next cell.",
            "Edits of letter-by-letter lines must keep the line's start and move only the rest of that line to fit (v0.1.1).",
            Invoice,
            [Replace("Invoice 2024", "Invoice No. 2024")]),

        new(
            "runs-delete-word",
            "Deleting the last word of a letter-by-letter line removes its objects and the space object before it.",
            "Shortening a letter-by-letter line must remove the objects of the deleted characters, spaces included (v0.1.1).",
            Invoice,
            [Replace("Total due", "Total")]),

        new(
            "runs-delete-line",
            "Deleting a whole letter-by-letter line removes all nine objects, the space included; the other lines stay where they were.",
            "Deleting a whole line was refused (“isn't supported yet”) until v0.4.0.",
            Invoice,
            [Replace("Total due", string.Empty)]),

        new(
            "runs-push-off-page-refused",
            "Lengthening a letter-by-letter line near the page's right edge, which would push the rest of the line off the page, is refused.",
            "Moving the rest of a line to fit must never push text off the page; such an edit must be refused as a change elsewhere (v0.1.1).",
            () => TestPdfs.CharacterByCharacter(("Total due", 540, 700)),
            [Replace("Total due", "Total amount due")]),

        new(
            "runs-clipping-area-wide",
            "Inserting a letter into a letter-by-letter line whose clipping area leaves room for it applies.",
            "Moving the rest of a line must not move text out of its clipping area, where it would be cut off (v0.1.1).",
            () => ClippedLine(clipRight: 210, color: "0 g"),
            [Replace("AAAA", "ABAAA")]),

        new(
            "runs-clipping-area-narrow",
            "The edit of runs-clipping-area-wide where the clipping area ends just after the line is refused.",
            "Moving the rest of a line must not move text out of its clipping area, where it would be cut off (v0.1.1).",
            () => ClippedLine(clipRight: 108, color: "0 g"),
            [Replace("AAAA", "ABAAA")]),

        new(
            "runs-clipping-area-narrow-white-text",
            "The edit of runs-clipping-area-narrow in white text, invisible on the white page, is refused too.",
            "Clipping must be checked by what the text covers, not by how the page looks, or text cut off unseen would pass (v0.1.1).",
            () => ClippedLine(clipRight: 108, color: "1 g"),
            [Replace("AAAA", "ABAAA")]),

        new(
            "runs-narrow-space",
            "Removing a space object that advances less than a full space moves the rest of the line back by that advance only.",
            "Edits of letter-by-letter lines must measure the space objects that were drawn, not a full space of the font (v0.1.1).",
            () => RawPdf.SinglePage(TextRunTests.OneObjectPerCharacter("/F1", "1 2", 556, 167, 556)),
            [Replace("1 2", "12")]),

        new(
            "runs-glyph-that-was-drawn",
            "Replacing a letter drawn with a code whose advance (1000) isn't its letter's usual width (667) moves the rest of the line by the drawn advance.",
            "Where old text ends must be measured from the glyph that was drawn, not from the font's width for its Unicode value, which can belong to another glyph (v0.1.1).",
            () => RawPdf.SinglePage(TextRunTests.OneObjectPerCharacter("/F2", "ABD", 667, 1000, 722)),
            [Replace("AAD", "ACD")]),

        new(
            "runs-layout-as-saved-content-stream",
            "Inserting a letter into a letter-spaced letter-by-letter line in the content stream, which keeps the 2 pt letter spacing for the new letter too.",
            "One-character objects don't show their letter spacing, so the layout must be checked as saved: the content stream keeps the spacing (v0.2.0), PDFium's rewrite drops it (v0.1.1).",
            () => RawPdf.SinglePage("2 Tc\n" + TextRunTests.OneObjectPerCharacter("/F1", "AAA", 667, 667, 667)),
            [Replace("AAA", "ABAA")]),

        new(
            "runs-layout-as-saved-pdfium-rewrite",
            "The edit of runs-layout-as-saved-content-stream through PDFium's page rewrite, which drops the letter spacing.",
            "One-character objects don't show their letter spacing, so the layout must be checked as saved, where PDFium's rewrite has dropped it (v0.1.1).",
            () => RawPdf.SinglePage("2 Tc\n" + TextRunTests.OneObjectPerCharacter("/F1", "AAA", 667, 667, 667)),
            [Replace("AAA", "ABAA", contentEditing: false)]),
    ];

    /// <summary>The invoice of TextRunTests: two cells on one line and a second line, one text object per character.</summary>
    private static PdfSnapshot Invoice() => TestPdfs.CharacterByCharacter(
        ("Invoice 2024", 72, 700),
        ("$1,250.00", 400, 700),
        ("Total due", 72, 650));

    /// <summary>
    /// "AAAA", one object per character, in a five-sided clipping area (PDFium drops rectangular
    /// ones that contain their object) that ends at <paramref name="clipRight"/>.
    /// </summary>
    private static PdfSnapshot ClippedLine(int clipRight, string color) => RawPdf.SinglePage(
        $"q 60 680 m {clipRight} 680 l {clipRight} 720 l 84 730 l 60 720 l h W n {color}\n" +
        $"{TextRunTests.OneObjectPerCharacter("/F1", "AAAA", 667, 667, 667, 667)}Q\n");
}
