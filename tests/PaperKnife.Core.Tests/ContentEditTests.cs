using System.Text;

namespace PaperKnife.Core.Tests;

/// <summary>Text edits made in the page's content stream and appended as incremental updates.</summary>
public sealed class ContentEditTests
{
    [Theory]
    [InlineData(RawPdf.InlineImage)]
    [InlineData(RawPdf.ShadedArea)]
    [InlineData(RawPdf.CharacterSpacing)]
    [InlineData("BT /F1 6 Tf 4 Tc 80 704 Td (tiny) Tj ET\n")]
    public void ReplaceText_KeepsContentPdfiumCannotRewrite(string content)
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + content));
        var original = session.CurrentSnapshot;
        Assert.Empty(session.GetTextEditBlockers(0));

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        AssertAppended(original, session.CurrentSnapshot);
    }

    [Fact]
    public void ReplaceText_KeepsTheEditedTextsPrintColor()
    {
        using var session = Open(RawPdf.SinglePage("BT /F1 24 Tf 0 1 1 0 k 72 700 Td (Hello World) Tj ET\n"));

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        var pixels = Pixels(session, result.TextRun!.Bounds);
        Assert.Contains(pixels, p => p.R > 200 && p.G < 60 && p.B < 60);
        Assert.DoesNotContain(pixels, p => p.R < 100 && p.G < 100 && p.B < 100);
    }

    [Theory]
    [InlineData("3 Tc", 45f)] // 3 points between each of the 16 letters
    [InlineData("8 Tw", 8f)]  // 8 points more for the one space
    public void ReplaceText_KeepsTheEditedTextsSpacing(string spacing, float extraWidth)
    {
        using var spaced = Open(RawPdf.SinglePage($"BT /F1 24 Tf {spacing} 72 700 Td (Hello World) Tj ET\n"));
        using var plain = Open(RawPdf.SinglePage("BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n"));

        var withSpacing = spaced.ReplaceText(0, Find(spaced, "Hello World").ObjectIndex, "Hello PaperKnife");
        var without = plain.ReplaceText(0, Find(plain, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, withSpacing.Status);
        Assert.Equal(EditStatus.Applied, without.Status);
        Assert.Equal(without.TextRun!.Bounds.Width + extraWidth, withSpacing.TextRun!.Bounds.Width, 0.5f);
    }

    [Fact]
    public void ReplaceText_InType3Font_Applies()
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text));
        var original = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "abc").ObjectIndex, "cab");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("cab", result.TextRun?.Text);
        AssertAppended(original, session.CurrentSnapshot);
    }

    [Fact]
    public void ReplaceText_InType3Font_WithALetterTheFontLacks_IsRejected()
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text));

        var result = session.ReplaceText(0, Find(session, "abc").ObjectIndex, "abd");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Equal(["d"], result.UnsupportedCharacters);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void PdfiumRewrite_OfType3Text_IsRejected()
    {
        // PDFium's content generator drops text in Type 3 fonts.
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text), contentEditing: false);

        var result = session.ReplaceText(0, Find(session, "abc").ObjectIndex, "cab");

        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
    }

    [Fact]
    public void ReplaceText_InFileWithObjectStreams_AppendsACrossReferenceStream()
    {
        using var session = Open(RawPdf.Build([[RawPdf.BaseContent]], useObjectStreams: true));
        var original = session.CurrentSnapshot;

        var first = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");
        var second = session.ReplaceText(0, Find(session, "This line must not move").ObjectIndex, "This line stays");

        Assert.Equal(EditStatus.Applied, first.Status);
        Assert.Equal(EditStatus.Applied, second.Status);
        AssertAppended(original, session.CurrentSnapshot);
        var update = Encoding.Latin1.GetString(session.CurrentSnapshot.Span[original.Length..]);
        Assert.Contains("/Type /XRef", update, StringComparison.Ordinal);
        Assert.DoesNotContain("\nxref\n", update, StringComparison.Ordinal);
        using var reopened = DocumentSession.FromSnapshot(session.CurrentSnapshot);
        Assert.Contains(reopened.GetTextRuns(0), r => r.Text == "Hello PaperKnife");
        Assert.Contains(reopened.GetTextRuns(0), r => r.Text == "This line stays");
    }

    [Fact]
    public void ReplaceText_OnPageDrawnBySeveralStreams_Applies()
    {
        using var session = Open(RawPdf.Build([["BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n", "BT /F1 12 Tf 72 660 Td (This line must not move) Tj ET\n"]]));
        var original = session.CurrentSnapshot;
        var other = Find(session, "This line must not move");

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        AssertAppended(original, session.CurrentSnapshot);
        Assert.Equal(other.Bounds, Find(session, "This line must not move").Bounds);
    }

    [Fact]
    public void ReplaceText_JoinsContentStreamsTheWayPdfiumReadsThem()
    {
        // PDFium joins content streams with a space, so the comment at the end of the first stream
        // also hides the red color at the start of the second, and the text is black.
        using var session = Open(RawPdf.Build([["% stamp", "1 0 0 rg\nBT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n"]]));

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        var pixels = Pixels(session, result.TextRun!.Bounds);
        Assert.Contains(pixels, p => p.R < 60 && p.G < 60 && p.B < 60);
        Assert.DoesNotContain(pixels, p => p.R > 200 && p.G < 60 && p.B < 60);
    }

    [Fact]
    public void ReplaceText_OnContentSharedWithAnotherPage_LeavesTheOtherPageAlone()
    {
        using var session = Open(RawPdf.Build([[RawPdf.BaseContent], ["(unused) Tj"]], shareContent: true));

        session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Contains(session.GetTextRuns(1), r => r.Text == "Hello World");
    }

    [Fact]
    public void ReplaceText_InBlockWithLineOperators_KeepsTheOtherLines()
    {
        // ' and " move to the next line by the leading; TD also sets the leading, which the later
        // block's T* uses.
        const string Content =
            "BT /F1 12 Tf 14 TL 72 600 Td (First line) Tj (Second line) ' 2 0 (Third line) \" 0 -20 TD (Fourth) Tj ET\n" +
            "BT /F1 12 Tf 72 400 Td (After) Tj T* (Next) Tj ET\n";
        using var session = Open(RawPdf.SinglePage(Content));
        var original = session.CurrentSnapshot;
        var others = new[] { "First line", "Third line", "Fourth", "After", "Next" }.Select(t => Find(session, t)).ToList();

        var result = session.ReplaceText(0, Find(session, "Second line").ObjectIndex, "2nd line");

        Assert.Equal(EditStatus.Applied, result.Status);
        AssertAppended(original, session.CurrentSnapshot);
        foreach (var other in others)
        {
            var after = Find(session, other.Text).Bounds;
            Assert.Equal(other.Bounds.Left, after.Left, 0.01f);
            Assert.Equal(other.Bounds.Bottom, after.Bottom, 0.01f);
        }
    }

    [Fact]
    public void ReplaceText_OnTextShownRightAfterOtherText_MovesTheRestOfTheLine()
    {
        using var session = Open(RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (Hello) Tj ( World) Tj ET\n"));
        var original = session.CurrentSnapshot;
        var before = Find(session, "Hello World");

        var result = session.ReplaceText(0, before.ObjectIndex, "Hi World");

        Assert.Equal(EditStatus.Applied, result.Status);
        AssertAppended(original, session.CurrentSnapshot);
        Assert.Equal("Hi World", result.TextRun?.Text);

        // "Hello" is 2278 units wide in Helvetica and "Hi" 944, so " World" moves 16.008 points left.
        Assert.Equal(before.Bounds.Right - 16.008f, result.TextRun!.Bounds.Right, 0.05f);
    }

    [Fact]
    public void UndoAndSave_AfterContentEdit_RestoreAndKeepExactBytes()
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text));
        var original = session.CurrentSnapshot;
        session.ReplaceText(0, Find(session, "abc").ObjectIndex, "cab");
        var edited = session.CurrentSnapshot;

        Assert.True(session.Undo());
        Assert.Same(original, session.CurrentSnapshot);
        Assert.Contains(session.GetTextRuns(0), r => r.Text == "abc");
        Assert.True(session.Redo());

        var path = Path.Combine(Path.GetTempPath(), $"paperknife-{Guid.NewGuid():N}.pdf");
        try
        {
            session.SaveAs(path);
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(edited.Span));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DocumentSession Open(PdfSnapshot snapshot, bool contentEditing = true)
    {
        var session = DocumentSession.FromSnapshot(snapshot);
        session.UseContentEditing = contentEditing;
        return session;
    }

    /// <summary>The edit was appended: every byte of the original file is still there, unchanged.</summary>
    private static void AssertAppended(PdfSnapshot original, PdfSnapshot edited)
    {
        Assert.True(edited.Length > original.Length);
        Assert.True(edited.Span.StartsWith(original.Span));
    }

    private static List<(byte R, byte G, byte B)> Pixels(DocumentSession session, PdfRect bounds)
    {
        var bitmap = session.RenderPage(0, 612, 792);
        var area = session.MapToPixels(0, bounds, 612, 792);
        var pixels = new List<(byte, byte, byte)>();
        for (var y = Math.Max(0, area.Y); y < Math.Min(bitmap.Height, area.Y + area.Height); y++)
        {
            for (var x = Math.Max(0, area.X); x < Math.Min(bitmap.Width, area.X + area.Width); x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                pixels.Add((bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]));
            }
        }

        return pixels;
    }

    private static PdfTextRun Find(DocumentSession session, string text) =>
        session.GetTextRuns(0).Single(r => Editing.TextEquivalence.AreEquivalent(r.Text, text));
}
