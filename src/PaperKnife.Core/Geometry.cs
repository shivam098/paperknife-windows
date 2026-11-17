namespace PaperKnife.Core;

/// <summary>A rectangle in PDF page space: points (1/72 inch), origin at the bottom-left, y pointing up.</summary>
public readonly record struct PdfRect(float Left, float Bottom, float Right, float Top)
{
    public float Width => Right - Left;

    public float Height => Top - Bottom;

    public double Area => Math.Max(0, Width) * (double)Math.Max(0, Height);

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public bool Intersects(PdfRect other) =>
        Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

    public PdfRect Union(PdfRect other) => new(
        Math.Min(Left, other.Left),
        Math.Min(Bottom, other.Bottom),
        Math.Max(Right, other.Right),
        Math.Max(Top, other.Top));

    public PdfRect Inflate(float amount) => new(Left - amount, Bottom - amount, Right + amount, Top + amount);
}

/// <summary>A rectangle in rendered-bitmap pixels: origin at the top-left, y pointing down.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Inflate(int amount) => new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));
}

/// <summary>Displayed page size in points, after the page's rotation is applied.</summary>
public readonly record struct PageSize(float Width, float Height);
