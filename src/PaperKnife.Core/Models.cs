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

/// <summary>Text drawn by one top-level PDF text object (one text-showing operator in the page content).</summary>
public sealed record PdfTextRun(
    int PageIndex,
    int ObjectIndex,
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
