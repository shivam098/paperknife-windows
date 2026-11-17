namespace PaperKnife.Core.Tests;

public sealed class DocumentTests
{
    [Fact]
    public void Load_RejectsDataThatIsNotPdf()
    {
        var snapshot = PdfSnapshot.FromBytes("definitely not a pdf"u8);

        var error = Assert.Throws<PdfException>(() => PdfDocument.Load(snapshot));

        Assert.Equal(PdfErrorCode.InvalidFormat, error.ErrorCode);
    }

    [Fact]
    public void TextRuns_DescribeTextFontAndPosition()
    {
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(RawPdf.BaseContent));

        var runs = session.GetTextRuns(0);

        Assert.Equal(new[] { "Hello World", "This line must not move" }, runs.Select(r => r.Text));
        var hello = runs[0];
        Assert.Equal("Helvetica", hello.FontName);
        Assert.False(hello.IsFontEmbedded);
        Assert.False(hello.IsInvisible);
        Assert.Equal(24f, hello.FontSize, 0.01f);
        Assert.InRange(hello.Bounds.Left, 71f, 74f);
        Assert.InRange(hello.Bounds.Bottom, 690f, 701f);
    }

    [Fact]
    public void RenderPage_DrawsContentOnWhite()
    {
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(RawPdf.BaseContent));

        var bitmap = session.RenderPage(0, 612, 792);

        var (r, g, b) = Pixel(bitmap, 172, 792 - 550);
        Assert.InRange(r, 210, 225);
        Assert.InRange(g, 35, 47);
        Assert.InRange(b, 35, 47);
        Assert.Equal((255, 255, 255), Pixel(bitmap, 500, 100));
    }

    [Fact]
    public void HitTestText_FindsTheRunUnderAPixel()
    {
        using var session = DocumentSession.FromSnapshot(RawPdf.SinglePage(RawPdf.BaseContent));

        var hit = session.HitTestText(0, 612, 792, 100, 792 - 708);

        Assert.Equal("Hello World", hit?.Text);
        Assert.True(session.MapToPixels(0, hit!.Bounds, 612, 792).Contains(100, 792 - 708));
        Assert.Null(session.HitTestText(0, 612, 792, 500, 50));
    }

    private static (int R, int G, int B) Pixel(RenderedBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Stride) + (x * 4);
        return (bitmap.Pixels[i + 2], bitmap.Pixels[i + 1], bitmap.Pixels[i]);
    }
}
