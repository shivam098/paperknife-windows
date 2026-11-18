using PaperKnife.Core.Editing;

namespace PaperKnife.Core.Tests;

public sealed class TextEditTests
{
    [Fact]
    public void ReplaceText_AppliesAndLeavesTheRestOfThePageAlone()
    {
        using var session = OpenRaw(RawPdf.BaseContent);
        var other = Find(session, "This line must not move");

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello PaperKnife", result.TextRun?.Text);
        Assert.True(session.IsModified);
        Assert.True(session.CanUndo);

        using var reopened = DocumentSession.FromSnapshot(session.CurrentSnapshot);
        Assert.Contains(reopened.GetTextRuns(0), r => r.Text == "Hello PaperKnife");
        var otherAfter = Find(reopened, "This line must not move");
        Assert.Equal(other.Bounds.Left, otherAfter.Bounds.Left, 0.01f);
        Assert.Equal(other.Bounds.Bottom, otherAfter.Bounds.Bottom, 0.01f);
    }

    [Fact]
    public void ReplaceText_WithCharacterMissingFromFont_IsRejectedWithoutChangingAnything()
    {
        using var session = OpenRaw(RawPdf.BaseContent);
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello Ω");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Contains("Ω", result.UnsupportedCharacters);
        Assert.Same(before, session.CurrentSnapshot);
        Assert.False(session.CanUndo);
        Assert.False(session.IsModified);
    }

    [Theory]
    [InlineData("नमस्ते")]
    [InlineData("Hello\nWorld")]
    [InlineData("Hello\u2028World")]
    [InlineData("Hi 😀")]
    public void ReplaceText_NeedingUnsupportedLayout_IsRejected(string text)
    {
        using var session = OpenRaw(RawPdf.BaseContent);

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, text);

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void PdfiumRewrite_WhenItWouldDropAnInlineImage_IsRejected()
    {
        using var session = OpenRaw(RawPdf.BaseContent + RawPdf.InlineImage, contentEditing: false);
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Fact]
    public void PdfiumRewrite_WhenItWouldLoseCharacterSpacing_IsRejected()
    {
        // PDFium's content generator doesn't write Tc, so the spaced-out line would shrink.
        using var session = OpenRaw(RawPdf.BaseContent + RawPdf.CharacterSpacing, contentEditing: false);

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
        Assert.False(session.IsModified);
    }

    [Theory]
    [InlineData("0 1 1 0 k")] // CMYK fill color: PDFium writes only RGB and gray colors
    [InlineData("3 Tc")]      // letter spacing
    [InlineData("8 Tw")]      // word spacing
    public void PdfiumRewrite_WhenItWouldChangeTheEditedTextsOwnStyle_IsRejected(string style)
    {
        using var session = OpenRaw($"BT /F1 24 Tf {style} 72 700 Td (Hello World) Tj ET\n", contentEditing: false);
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Fact]
    public void ReplaceText_OnKernedRun_Applies()
    {
        // Kerning survives PDFium's rewrite, so text set with TJ adjustments stays editable.
        using var session = OpenRaw(RawPdf.BaseContent + RawPdf.KernedText);
        var kerned = session.GetTextRuns(0).Single(r => r.Text.Contains("rned", StringComparison.Ordinal));

        var result = session.ReplaceText(0, kerned.ObjectIndex, "Kerned text");

        Assert.Equal(EditStatus.Applied, result.Status);
    }

    [Fact]
    public void PdfiumRewrite_OnPageWithShading_IsRefusedUpFront()
    {
        using var session = OpenRaw(RawPdf.BaseContent + RawPdf.ShadedArea, contentEditing: false);
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.NotEmpty(session.GetTextEditBlockers(0));
        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Fact]
    public void PdfiumRewrite_WhenItWouldChangeContentUnderTheEditedText_IsRejected()
    {
        // The small spaced-out word sits inside "Hello World"'s own area, where a check that only
        // compared the rest of the page would miss it.
        using var session = OpenRaw(RawPdf.BaseContent + "BT /F1 6 Tf 4 Tc 80 704 Td (tiny) Tj ET\n", contentEditing: false);
        var before = session.CurrentSnapshot;

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Theory]
    [InlineData("Area 50 m2", "Area 50 m²")]
    [InlineData("Wait...", "Wait…")]
    public void ReplaceText_WithCompatibilityVariant_IsARealEdit(string original, string edited)
    {
        using var session = OpenRaw($"BT /F1 24 Tf 72 700 Td ({original}) Tj ET\n");

        var result = session.ReplaceText(0, Find(session, original).ObjectIndex, edited);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(edited, result.TextRun?.Text);
    }

    [Fact]
    public void ReplaceText_DropsTrailingWhitespaceTheFontCannotDraw()
    {
        // Arial has no U+3000; PDFium would draw the font's missing-glyph box for it.
        var fontPath = ArialPath();
        using var withIdeographicSpace = DocumentSession.FromSnapshot(TestPdfs.WithEmbeddedFont(fontPath, "Invoice 2024"));
        using var plain = DocumentSession.FromSnapshot(TestPdfs.WithEmbeddedFont(fontPath, "Invoice 2024"));

        var spaced = withIdeographicSpace.ReplaceText(0, Find(withIdeographicSpace, "Invoice 2024").ObjectIndex, "Invoice 2025\u3000");
        var unspaced = plain.ReplaceText(0, Find(plain, "Invoice 2024").ObjectIndex, "Invoice 2025");

        Assert.Equal(EditStatus.Applied, spaced.Status);
        Assert.Equal(EditStatus.Applied, unspaced.Status);
        Assert.True(withIdeographicSpace.RenderPage(0, 612, 792).Pixels.SequenceEqual(plain.RenderPage(0, 612, 792).Pixels));
    }

    [Fact]
    public void ReplaceText_WithWhitespaceTheFontCannotDraw_IsRejected()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.WithEmbeddedFont(ArialPath(), "Invoice 2024"));

        var result = session.ReplaceText(0, Find(session, "Invoice 2024").ObjectIndex, "Invoice\u30002025");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void ReplaceText_KeepsKerningOfOtherText()
    {
        using var session = OpenRaw(RawPdf.BaseContent + RawPdf.KernedText);

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
    }

    [Fact]
    public void ReplaceText_WithEmbeddedTrueTypeFont_Applies()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.WithEmbeddedFont(ArialPath(), "Invoice 2024"));
        var run = Find(session, "Invoice 2024");

        var result = session.ReplaceText(0, run.ObjectIndex, "Invoice 2025");

        Assert.True(run.IsFontEmbedded);
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.True(TextEquivalence.AreEquivalent("Invoice 2025", result.TextRun!.Text));
    }

    [Fact]
    public void ReplaceText_OnRunFollowedByTextOnTheSameLine_Applies()
    {
        // PDFium reports the gap before "$1,250.00" as a generated space at the end of the first run.
        using var session = OpenRaw(RawPdf.BaseContent
            + "BT /F1 12 Tf 72 400 Td (Design and printing) Tj ET\n"
            + "BT /F1 12 Tf 460 400 Td ($1,250.00) Tj ET\n");

        var result = session.ReplaceText(0, Find(session, "Design and printing").ObjectIndex, "Design and print");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Design and print", result.TextRun?.Text);
    }

    [Fact]
    public void UndoAndRedo_RestoreExactSnapshots()
    {
        using var session = OpenRaw(RawPdf.BaseContent);
        var original = session.CurrentSnapshot;
        session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello PaperKnife");
        var edited = session.CurrentSnapshot;

        Assert.True(session.Undo());
        Assert.Same(original, session.CurrentSnapshot);
        Assert.False(session.IsModified);
        Assert.Contains(session.GetTextRuns(0), r => r.Text == "Hello World");

        Assert.True(session.Redo());
        Assert.Same(edited, session.CurrentSnapshot);
        Assert.Contains(session.GetTextRuns(0), r => r.Text == "Hello PaperKnife");
        Assert.False(session.Redo());
    }

    [Fact]
    public void ReplaceText_WithSameText_ReportsNoChange()
    {
        using var session = OpenRaw(RawPdf.BaseContent);

        var result = session.ReplaceText(0, Find(session, "Hello World").ObjectIndex, "Hello World");

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ReplaceText_OnObjectThatIsNotText_IsRejected()
    {
        using var session = OpenRaw(RawPdf.BaseContent);
        var before = session.CurrentSnapshot;

        // Object 2 is the red rectangle.
        var result = session.ReplaceText(0, 2, "x");

        Assert.Equal(EditRejection.InvalidTarget, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    private static string ArialPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(path))
        {
            Assert.Skip("Arial isn't installed on this machine.");
        }

        return path;
    }

    private static DocumentSession OpenRaw(string content, bool contentEditing = true)
    {
        var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(content));
        session.UseContentEditing = contentEditing;
        return session;
    }

    private static PdfTextRun Find(DocumentSession session, string text) =>
        session.GetTextRuns(0).Single(r => TextEquivalence.AreEquivalent(r.Text, text));
}
