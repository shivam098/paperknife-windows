using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Syntax;

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

    [Theory]
    [InlineData("abd")]
    [InlineData("ab d")] // the space is drawn as a gap, so only the letter is missing
    public void ReplaceText_InType3Font_WithALetterTheFontLacks_IsRejected(string text)
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text));

        var result = session.ReplaceText(0, Find(session, "abc").ObjectIndex, text);

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Equal(["d"], result.UnsupportedCharacters);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void ReplaceText_WithASpaceTheFontCannotDraw_LeavesAGap()
    {
        // The font has no space glyph, like TeX's fonts; TeX leaves a gap between words instead,
        // which PDFium reads as a space. With no gaps on the page to copy, it is a third of an em.
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.Type3Text));

        var result = session.ReplaceText(0, Find(session, "abc").ObjectIndex, "ab ca");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("ab ca", result.TextRun?.Text);
        Assert.Matches(@"\[<6162> -333\.3+ <6361>\] TJ", PageContent(session));
    }

    [Fact]
    public void ReplaceText_KeepsTheKerningAndGapsOfUnchangedText()
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + RawPdf.GappedText));

        var result = session.ReplaceText(0, Find(session, "ab ca").ObjectIndex, "ab cab c");

        // The kerning after "a" and the gap after "b" are copied, and the new space gets the
        // page's usual gap in this font.
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("ab cab c", result.TextRun?.Text);
        Assert.Contains("[<61> 30 <62> -400 <636162> -400 <63>] TJ", PageContent(session), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("2nd line")]
    [InlineData("")] // deleted
    public void ReplaceText_InBlockWithLineOperators_KeepsTheOtherLines(string newText)
    {
        // ' and " move to the next line by the leading; TD also sets the leading, which the later
        // block's T* uses.
        const string Content =
            "BT /F1 12 Tf 14 TL 72 600 Td (First line) Tj (Second line) ' 2 0 (Third line) \" 0 -20 TD (Fourth) Tj ET\n" +
            "BT /F1 12 Tf 72 400 Td (After) Tj T* (Next) Tj ET\n";
        using var session = Open(RawPdf.SinglePage(Content));
        var original = session.CurrentSnapshot;
        var others = new[] { "First line", "Third line", "Fourth", "After", "Next" }.Select(t => Find(session, t)).ToList();

        var result = session.ReplaceText(0, Find(session, "Second line").ObjectIndex, newText);

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
    public void ReplaceText_InBlockOfRelativeMoves_LeavesTheOtherLinesExactlyWhereTheyWere()
    {
        // PDFium adds up Td moves in single precision. The rewritten block places each line with
        // an explicit Tm, which must land on the same positions to the bit: a glyph a fraction of
        // a pixel off may round to another pixel, and the edit is then refused.
        const string Content =
            "BT /F1 10 Tf 392.432 685.111 Td (First) Tj 56.276 -11.956 Td (Second) Tj -303.981 -11.956 Td (Third) Tj 0 -11.955 Td (Fourth) Tj ET\n";
        using var session = Open(RawPdf.SinglePage(Content));
        var others = new[] { "Second", "Third", "Fourth" }.Select(t => Find(session, t)).ToList();

        var result = session.ReplaceText(0, Find(session, "First").ObjectIndex, "1st");

        Assert.Equal(EditStatus.Applied, result.Status);
        foreach (var other in others)
        {
            Assert.Equal(other.Bounds, Find(session, other.Text).Bounds);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // PDFium rewrites the page
    public void ReplaceText_WithNothing_DeletesTheLine(bool contentEditing)
    {
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent), contentEditing);
        var original = session.CurrentSnapshot;
        var other = Find(session, "This line must not move");

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.StartsWith("Text deleted.", result.Message, StringComparison.Ordinal);
        Assert.Null(result.TextRun);
        Assert.Equal(["This line must not move"], session.GetTextRuns(0).Select(r => r.Text));
        Assert.Equal(other.Bounds, Find(session, "This line must not move").Bounds);
        Assert.Equal(contentEditing, session.CurrentSnapshot.Span.StartsWith(original.Span));

        Assert.True(session.Undo());
        Assert.Contains(session.GetTextRuns(0), r => r.Text == "Hello World");
    }

    [Fact]
    public void ReplaceText_WithNothing_KeepsInvisibleTextAfterIt()
    {
        // The invisible text was placed right after the deleted text; the page looks the same
        // wherever it ends up, so its position is checked on its own.
        using var session = Open(RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (Visible) Tj 3 Tr (hidden words) Tj ET\n"));
        var hidden = Find(session, "hidden words");
        Assert.True(hidden.IsInvisible);

        var result = session.ReplaceText(0, Find(session, "Visible").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(hidden.Bounds, Find(session, "hidden words").Bounds);
    }

    [Theory]
    [InlineData("")] // deleted
    [InlineData("Hello There")]
    public void PdfiumRewrite_WhenItWouldRespaceInvisibleText_IsRejected(string newText)
    {
        // PDFium's page rewrite drops letter spacing, which the page comparison can't see in invisible text.
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + "BT /F1 12 Tf 3 Tr 5 Tc 72 300 Td (hidden OCR words) Tj ET\n"), contentEditing: false);

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, newText);

        Assert.Equal(EditStatus.Rejected, result.Status);
        Assert.False(session.IsModified);
    }

    [Theory]
    [InlineData("BT /F1 18 Tf 4 Tc 72 400 Td (DRAFT) Tj ET\n")] // letter spacing
    [InlineData("BT /F1 18 Tf 0 1 1 0 k 72 400 Td (DRAFT) Tj ET\n")] // a print (CMYK) color
    [InlineData("BT /F1 12 Tf 3 Tr 5 Tc 72 300 Td (hidden OCR words) Tj ET\n")] // invisible and letter-spaced
    public void PdfiumRewrite_DeletesTextWhoseOwnStyleItCannotWriteBack(string content)
    {
        // Nothing of the deleted text remains, so only the rest of the page has to survive the rewrite.
        using var session = Open(RawPdf.SinglePage(RawPdf.BaseContent + content), contentEditing: false);
        var run = session.GetTextRuns(0).Single(r => r.Text is not ("Hello World" or "This line must not move"));

        var result = session.ReplaceText(0, run.ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(["Hello World", "This line must not move"], session.GetTextRuns(0).Select(r => r.Text));
    }

    [Fact]
    public void ReplaceText_WithNothing_AfterAHyphenatedLine_Applies()
    {
        // PDFium reads the hyphen that ends the first line as U+0002 only while the next line follows it.
        using var session = Open(RawPdf.SinglePage("BT /F1 12 Tf 72 600 Td (A long hyphen-) Tj 0 -14 Td (ated word.) Tj ET\n"));

        var result = session.ReplaceText(0, Find(session, "ated word.").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(["A long hyphen-"], session.GetTextRuns(0).Select(r => r.Text));
    }

    [Fact]
    public void ReplaceText_WithNothing_AfterALineEndingInASoftHyphen_Applies()
    {
        // Some word processors map their hyphen to U+00AD SOFT HYPHEN, which PDFium also reads as U+0002 there.
        using var session = Open(RawPdf.WriteWithTable(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            RawPdf.Stream("BT /F1 12 Tf 72 600 Td (A long hyphen-) Tj 0 -14 Td (ated word.) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
            RawPdf.Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Soft def /CMapType 2 def\n" +
                "1 begincodespacerange <00> <FF> endcodespacerange\n1 beginbfchar <2D> <00AD> endbfchar\n" +
                "endcmap CMapName currentdict /CMap defineresource pop end end"),
        ]));

        var result = session.ReplaceText(0, Find(session, "ated word.").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
    }

    [Fact]
    public void ReplaceText_WithNothing_OnFakeBoldText_DeletesOneCopyAtATime()
    {
        // Text drawn twice, slightly apart, looks bold. PDFium reads only the first copy; once it is
        // deleted, the second one reads as text and can be deleted too.
        using var session = Open(RawPdf.SinglePage(
            "BT /F1 18 Tf 72 600 Td (Bold Heading) Tj ET\nBT /F1 18 Tf 72.4 600 Td (Bold Heading) Tj ET\nBT /F1 12 Tf 72 500 Td (Body) Tj ET\n"));

        var first = session.ReplaceText(0, Find(session, "Bold Heading").ObjectIndex, string.Empty);
        var second = session.ReplaceText(0, Find(session, "Bold Heading").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, first.Status);
        Assert.Equal(EditStatus.Applied, second.Status);
        Assert.Equal(["Body"], session.GetTextRuns(0).Select(r => r.Text));
    }

    [Fact]
    public void ReplaceText_WithNothing_InAFontPaperKnifeCannotEncode_KeepsEveryOtherByte()
    {
        // Chinese text in a font with one of the CMaps PDFium has built in; deleting it needs no encoding.
        var pdf = RawPdf.WriteWithTable(
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
        using var session = Open(pdf);
        var original = session.CurrentSnapshot;
        var others = session.GetTextRuns(0).Where(r => r.Text != "中文").ToList();

        var result = session.ReplaceText(0, Find(session, "中文").ObjectIndex, string.Empty);

        Assert.Equal(EditStatus.Applied, result.Status);
        AssertAppended(original, session.CurrentSnapshot);
        Assert.Equal(others.Select(r => (r.Text, r.Bounds)), session.GetTextRuns(0).Select(r => (r.Text, r.Bounds)));
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

    /// <summary>The first page's content, decoded.</summary>
    private static string PageContent(DocumentSession session)
    {
        var file = PdfFile.TryOpen(session.CurrentSnapshot.Memory, out _)!;
        return Encoding.Latin1.GetString(ContentPage.TryLoad(file, file.GetPages()!, 0)!.Content);
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
