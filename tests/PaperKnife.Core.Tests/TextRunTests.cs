using System.Globalization;
using System.Text;
using PaperKnife.Core.Editing;

namespace PaperKnife.Core.Tests;

/// <summary>Text drawn one character per text object, as in PDFs made from web pages by wkhtmltopdf.</summary>
public sealed class TextRunTests
{
    [Fact]
    public void TextRuns_JoinCharactersDrawnOneByOneIntoLinesAndCells()
    {
        using var session = OpenInvoice();

        var runs = session.GetTextRuns(0);
        var hit = session.HitTestText(0, 612, 792, 80, 792 - 704);

        Assert.Equal(new[] { "Invoice 2024", "$1,250.00", "Total due" }, runs.Select(r => r.Text));
        Assert.All(runs, r => Assert.Equal(r.Text.Length, r.ObjectCount)); // spaces are objects too
        Assert.Equal("Invoice 2024", hit?.Text);
    }

    [Fact]
    public void TextRuns_KeepTextOfAnotherColorApart()
    {
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(
            "BT /F1 12 Tf 72 600 Td (A) Tj 8.004 0 Td (B) Tj 8.004 0 Td 1 0 0 rg (C) Tj ET\n"));

        Assert.Equal(new[] { "AB", "C" }, session.GetTextRuns(0).Select(r => r.Text));
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_RewritesOnlyTheChangedCharacters()
    {
        using var session = OpenInvoice();
        var amount = Find(session, "$1,250.00");

        var result = session.ReplaceText(0, Find(session, "Invoice 2024").ObjectIndex, "Invoice 2025");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Invoice 2025", result.TextRun?.Text);
        Assert.Equal(12, result.TextRun?.ObjectCount);
        Assert.Equal(amount.Bounds, Find(session, "$1,250.00").Bounds);
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_MovesTheRestOfTheLineToFit()
    {
        using var session = OpenInvoice();
        var before = Find(session, "Invoice 2024");
        var amount = Find(session, "$1,250.00");

        var result = session.ReplaceText(0, before.ObjectIndex, "Invoice No. 2024");

        Assert.Equal(EditStatus.Applied, result.Status);
        var after = Find(session, "Invoice No. 2024");
        Assert.Equal(before.Bounds.Left, after.Bounds.Left, 0.01f);

        // "No. " in 12 pt Helvetica is (722 + 556 + 278 + 278) / 1000 * 12 = 22.008 points wide.
        Assert.Equal(before.Bounds.Right + 22.008f, after.Bounds.Right, 0.1f);
        Assert.Equal(amount.Bounds, Find(session, "$1,250.00").Bounds);
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_DeletesAWord()
    {
        using var session = OpenInvoice();
        var objects = session.GetTextRuns(0).Sum(r => r.ObjectCount);

        var result = session.ReplaceText(0, Find(session, "Total due").ObjectIndex, "Total");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Total", result.TextRun?.Text);
        Assert.Equal(objects - 4, session.GetTextRuns(0).Sum(r => r.ObjectCount));
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_RefusesToPushTheRestOfTheLineOffThePage()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.CharacterByCharacter(("Total due", 540, 700)));
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Total due").ObjectIndex, "Total amount due");

        Assert.Equal(EditRejection.CollateralChange, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Theory]
    [InlineData(210, "0 g", EditStatus.Applied)]
    [InlineData(108, "0 g", EditStatus.Rejected)] // the clipping area ends just after the line
    [InlineData(108, "1 g", EditStatus.Rejected)] // white text, invisible on a white page
    public void ReplaceText_OnCharacterRun_KeepsTheRestOfTheLineInsideItsClippingArea(int clipRight, string color, EditStatus expected)
    {
        // PDFium drops rectangular clipping paths that contain their object, so this one has five sides.
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(
            $"q 60 680 m {clipRight} 680 l {clipRight} 720 l 84 730 l 60 720 l h W n {color}\n" +
            $"{OneObjectPerCharacter("/F1", "AAAA", 667, 667, 667, 667)}Q\n"));

        var result = session.ReplaceText(0, Find(session, "AAAA").ObjectIndex, "ABAAA");

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_RemovesOnlyTheWidthOfANarrowSpace()
    {
        // The space object leaves a gap of 167 rather than a full space (278) before the "2".
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(OneObjectPerCharacter("/F1", "1 2", 556, 167, 556)));
        var before = Find(session, "1 2");

        var result = session.ReplaceText(0, before.ObjectIndex, "12");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(before.Bounds.Right - 2.004f, result.TextRun!.Bounds.Right, 0.05f); // 167 / 1000 * 12
    }

    [Fact]
    public void ReplaceText_OnCharacterRun_MeasuresTheGlyphThatWasDrawn()
    {
        // The middle object draws "A" with code 66, whose advance is 1000 rather than 667.
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(OneObjectPerCharacter("/F2", "ABD", 667, 1000, 722)));
        var before = Find(session, "AAD");

        var result = session.ReplaceText(0, before.ObjectIndex, "ACD");

        Assert.Equal(EditStatus.Applied, result.Status);

        // "C" is 722 wide, so "D" moves by (722 - 1000) / 1000 * 12 = -3.336 points.
        Assert.Equal(before.Bounds.Right - 3.336f, result.TextRun!.Bounds.Right, 0.05f);
    }

    [Theory]
    [InlineData(true, 10.004)]  // content editing keeps the 2 pt letter spacing, so "B" brings 2 pt more
    [InlineData(false, 8.004)]  // PDFium's rewrite drops letter spacing; "B" is 667 units wide
    public void ReplaceText_OnCharacterRun_ChecksTheLayoutAsSaved(bool contentEditing, float growth)
    {
        // Character spacing doesn't change one-character objects.
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage("2 Tc\n" + OneObjectPerCharacter("/F1", "AAA", 667, 667, 667)));
        session.UseContentEditing = contentEditing;
        var before = Find(session, "AAA");

        var result = session.ReplaceText(0, before.ObjectIndex, "ABAA");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(before.Bounds.Right + growth, result.TextRun!.Bounds.Right, 0.05f);
    }

    [Theory]
    [InlineData("₹| |1|1|6|8|8", "", "₹ 12688", 3, 3, "2")]
    [InlineData("I|n|v|o|i|c|e| |2|0|2|4", "", "Invoice No. 2024", 7, 7, " No. ")] // typing after a space joins the space
    [InlineData("T|o|t|a|l| |d|u|e", "", "Total", 5, 8, "")] // the word and the space before it go
    [InlineData("Single|Pvt|room", " | ", "Single room", 1, 2, "room")] // one object per word, gaps between
    [InlineData("t|r|u|s|t|e|d| ", "", "trusty", 5, 6, "y")] // trailing whitespace stays as it is
    public void Plan_ChangesTheFewestObjects(string pieces, string separators, string newText, int first, int last, string replacement)
    {
        var texts = pieces.Split('|');
        var gaps = separators.Length > 0 ? separators.Split('|') : Enumerable.Repeat(string.Empty, texts.Length - 1).ToArray();

        var edit = TextRunEdit.Plan(texts, gaps, newText);

        Assert.Equal(new TextRunEdit(first, last, replacement), edit);
    }

    private static DocumentSession OpenInvoice() => DocumentSession.FromSnapshot(TestPdfs.CharacterByCharacter(
        ("Invoice 2024", 72, 700),
        ("$1,250.00", 400, 700),
        ("Total due", 72, 650)));

    private static PdfTextRun Find(DocumentSession session, string text) =>
        session.GetTextRuns(0).Single(r => TextEquivalence.AreEquivalent(r.Text, text));

    /// <summary>
    /// Content that draws <paramref name="text"/> in 12 pt from (72, 700), one text object per
    /// character, each advancing by its width in thousandths of an em.
    /// </summary>
    private static string OneObjectPerCharacter(string font, string text, params int[] widths)
    {
        var content = new StringBuilder();
        var x = 72.0;
        for (var i = 0; i < text.Length; i++)
        {
            content.Append(CultureInfo.InvariantCulture, $"BT {font} 12 Tf 1 0 0 1 {x:0.###} 700 Tm ({text[i]}) Tj ET\n");
            x += widths[i] * 12 / 1000.0;
        }

        return content.ToString();
    }
}
