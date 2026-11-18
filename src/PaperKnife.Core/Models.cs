namespace PaperKnife.Core;

/// <summary>An opaque page rendering in 32-bit BGRA (alpha is always 255, so it is also premultiplied).</summary>
public sealed class RenderedBitmap
{
    private readonly byte[] _pixels;

    internal RenderedBitmap(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public ReadOnlySpan<byte> Pixels => _pixels;
}

/// <summary>
/// A piece of text as PaperKnife edits it: one or more consecutive top-level text objects that sit
/// next to each other on one baseline and look the same, usually a line or a table cell. Most
/// PDFs draw such text with one object, but some draw every character or word separately.
/// </summary>
/// <param name="ObjectIndex">Index of the run's first page object; the run spans <paramref name="ObjectCount"/> consecutive objects.</param>
/// <param name="Text">The text, without trailing whitespace.</param>
public sealed record PdfTextRun(
    int PageIndex,
    int ObjectIndex,
    int ObjectCount,
    string Text,
    PdfRect Bounds,
    string FontName,
    bool IsFontEmbedded,
    bool IsFontSubset,
    float FontSize,
    bool IsInvisible);

/// <summary>How many top-level objects of each kind a page contains.</summary>
public readonly record struct PageObjectCounts(int Text, int Path, int Image, int Shading, int Form, int Other)
{
    public override string ToString() =>
        $"{Text} text, {Path} path, {Image} image, {Shading} shading, {Form} form, {Other} other";
}
