namespace PaperKnife.Core.Editing;

/// <summary>Pixel comparison used to prove that an edit didn't change anything else on the page.</summary>
internal static class EditVerifier
{
    /// <summary>Per-channel difference treated as anti-aliasing noise rather than a real change.</summary>
    public const int ChannelTolerance = 24;

    private const double TargetPixelsPerPoint = 2.0;
    private const double MaxCheckPixels = 8_000_000;

    /// <summary>Rendering size for verification: 144 dpi, scaled down for very large pages.</summary>
    public static (int Width, int Height) CheckSize(float widthPoints, float heightPoints)
    {
        var scale = TargetPixelsPerPoint;
        var area = widthPoints * (double)heightPoints;
        if (area * scale * scale > MaxCheckPixels)
        {
            scale = Math.Sqrt(MaxCheckPixels / area);
        }

        return (Math.Max(1, (int)Math.Round(widthPoints * scale)), Math.Max(1, (int)Math.Round(heightPoints * scale)));
    }

    public static int CountChangedPixels(RenderedBitmap before, RenderedBitmap after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return before.Width * before.Height;
        }

        var a = before.Pixels;
        var b = after.Pixels;
        var changed = 0;
        for (var y = 0; y < before.Height; y++)
        {
            var row = y * before.Stride;
            for (var x = 0; x < before.Width; x++)
            {
                var i = row + (x * 4);
                if (Math.Abs(a[i] - b[i]) > ChannelTolerance
                    || Math.Abs(a[i + 1] - b[i + 1]) > ChannelTolerance
                    || Math.Abs(a[i + 2] - b[i + 2]) > ChannelTolerance)
                {
                    changed++;
                }
            }
        }

        return changed;
    }
}
