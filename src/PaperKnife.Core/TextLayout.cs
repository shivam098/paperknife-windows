using System.Text;
using PaperKnife.Core.Pdfium;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core;

/// <summary>What PDFium's text extraction put between two characters that follow each other.</summary>
internal enum TextGap
{
    None,
    Space,
    LineBreak,
}

/// <summary>One top-level text object, as <see cref="TextLayout"/> sees it.</summary>
internal sealed class TextPiece(int objectIndex, nint handle)
{
    public int ObjectIndex { get; } = objectIndex;

    public nint Handle { get; } = handle;

    /// <summary>
    /// The object's own characters in reading order, plus the spaces PDFium found between them
    /// (for example for a large kerning adjustment), but not the gap after the last one.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// False when a character has no Unicode value, or the object's characters aren't contiguous in
    /// reading order. Such objects still form a run of their own, but never join other objects.
    /// </summary>
    public bool IsWellFormed { get; set; } = true;

    public int FirstCharacter { get; set; } = -1;

    public int LastCharacter { get; set; } = -1;

    public uint LastUnicode { get; set; }

    /// <summary>Origins of the first and last character, in page space.</summary>
    public (double X, double Y) FirstOrigin { get; set; }

    public (double X, double Y) LastOrigin { get; set; }

    /// <summary>Advance width of the last character, in text space units.</summary>
    public float LastAdvance { get; set; }

    /// <summary>
    /// False when <see cref="LastAdvance"/> may be wrong: the font's width for the last
    /// character's Unicode value disagrees with the boxes PDFium measured for the glyph that was
    /// drawn. Then nothing after this object can be moved to fit an edit that ends here.
    /// </summary>
    public bool IsEndKnown { get; set; } = true;

    /// <summary>PDFium's tight box (the glyph's ink) and loose box of the last character, in page space.</summary>
    public PdfRect? LastInkBox { get; set; }

    public PdfRect? LastLooseBox { get; set; }

    /// <summary>Where the last character's advance ends, in page space.</summary>
    public (double X, double Y) End => (LastOrigin.X + (LastAdvance * Matrix.A), LastOrigin.Y + (LastAdvance * Matrix.B));

    /// <summary>The object whose characters come right after this one's in reading order, and what lies between them.</summary>
    public TextPiece? Next { get; set; }

    public TextGap GapAfter { get; set; }

    /// <summary>Maps text space (without the font size) to page space; the translation is the object's origin.</summary>
    public FsMatrix Matrix { get; set; }

    public bool HasMatrix { get; set; }

    public float FontSize { get; set; }

    public nint Font { get; set; }

    /// <summary>The font's name as PDFium reports it: /BaseFont, without the subset tag for embedded fonts.</summary>
    public string FontName { get; set; } = string.Empty;

    public bool IsEmbedded { get; set; }

    public int RenderMode { get; set; }

    public PdfRect? Bounds { get; set; }

    public float[]? ClipSignature { get; set; }

    public bool HasCharacters => FirstCharacter >= 0;
}

/// <summary>A run and the text objects it is made of, valid until the page changes.</summary>
internal sealed class TextRunLayout(PdfTextRun run, IReadOnlyList<TextPiece> pieces, IReadOnlyList<string> separators, string fullText)
{
    public PdfTextRun Run { get; } = run;

    public IReadOnlyList<TextPiece> Pieces { get; } = pieces;

    /// <summary><c>Separators[i]</c> is the text between <c>Pieces[i]</c> and <c>Pieces[i + 1]</c>: a space PDFium found in the gap, or nothing.</summary>
    public IReadOnlyList<string> Separators { get; } = separators;

    /// <summary>The run's text including trailing whitespace, which <see cref="PdfTextRun.Text"/> leaves out.</summary>
    public string FullText { get; } = fullText;

    public bool Contains(int objectIndex) =>
        objectIndex >= Run.ObjectIndex && objectIndex < Run.ObjectIndex + Run.ObjectCount;

    /// <summary>Distance along the run's baseline from the first object's origin to a page-space point, in text space units.</summary>
    public double DistanceAlongBaseline(double x, double y)
    {
        var first = Pieces[0];
        return TextLayout.ToTextSpace(first.Matrix, x - first.Matrix.E, y - first.Matrix.F).U;
    }

    /// <summary>Where the last character of a piece ends, along the run's baseline.</summary>
    public double EndAlongBaseline(TextPiece piece) =>
        DistanceAlongBaseline(piece.LastOrigin.X, piece.LastOrigin.Y) + piece.LastAdvance;
}

/// <summary>
/// Groups a page's top-level text objects into the runs PaperKnife edits. Many PDFs draw a line
/// with one text object, but some draw every character as its own object (for example PDFs made
/// from web pages with wkhtmltopdf), or every word. Consecutive objects join a run when they
/// follow each other in reading order on the same baseline, close together, and look the same:
/// same font, size, orientation, colors, render mode, clipping and marked content. Use only while
/// holding <see cref="PdfiumLibrary.Sync"/>; the result is valid until the page changes.
/// </summary>
internal static unsafe class TextLayout
{
    // Limits for joining two objects, in ems of the font size. Gaps between table cells are
    // usually wider than MaxGap; gaps between words are narrower.
    private const double MaxBaselineShift = 0.05;
    private const double MaxGap = 1.0;
    private const double MaxOverlap = 0.5;
    private const double RelativeTolerance = 1e-4;

    public static (IReadOnlyList<TextPiece> Pieces, IReadOnlyList<TextRunLayout> Runs) Build(nint page, int pageIndex)
    {
        var pieces = new List<TextPiece>();
        var byHandle = new Dictionary<nint, TextPiece>();
        var count = FPDFPage_CountObjects(page);
        for (var i = 0; i < count; i++)
        {
            var obj = FPDFPage_GetObject(page, i);
            if (obj != 0 && FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT)
            {
                var piece = new TextPiece(i, obj);
                pieces.Add(piece);
                byHandle[obj] = piece;
            }
        }

        if (pieces.Count == 0)
        {
            return (pieces, []);
        }

        var textPage = FPDFText_LoadPage(page);
        if (textPage != 0)
        {
            try
            {
                ReadCharacters(textPage, byHandle);
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }

        foreach (var piece in pieces)
        {
            ReadGeometry(piece);
        }

        var runs = new List<TextRunLayout>();
        var next = 0;
        while (next < pieces.Count)
        {
            var current = new List<TextPiece> { pieces[next++] };
            var separators = new List<string>();
            while (next < pieces.Count)
            {
                if (TryJoin(current[^1], pieces[next], out var separator))
                {
                    separators.Add(separator);
                    current.Add(pieces[next++]);
                    continue;
                }

                // Spaces drawn as objects of their own sit between the words they separate.
                var end = next;
                while (end < pieces.Count && IsSpacer(pieces[end]) && pieces[end].ObjectIndex == pieces[end - 1].ObjectIndex + 1)
                {
                    end++;
                }

                if (end == next || end == pieces.Count || !TryJoinAcrossSpacers(current[^1], pieces, next, end))
                {
                    break;
                }

                for (; next <= end; next++)
                {
                    separators.Add(string.Empty);
                    current.Add(pieces[next]);
                }
            }

            runs.Add(CreateRun(pageIndex, current, separators));
        }

        return (pieces, runs);
    }

    /// <summary>Maps a page-space displacement into a text space: along the baseline (U) and across it (V).</summary>
    public static (double U, double V) ToTextSpace(FsMatrix m, double dx, double dy)
    {
        var determinant = (m.A * (double)m.D) - (m.B * (double)m.C);
        if (Math.Abs(determinant) < 1e-12)
        {
            return (double.NaN, double.NaN);
        }

        return (((m.D * dx) - (m.C * dy)) / determinant, ((m.A * dy) - (m.B * dx)) / determinant);
    }

    /// <summary>The advance width of text set with <paramref name="font"/>, in text space units, as PDFium lays it out.</summary>
    public static double Measure(nint font, float fontSize, string text)
    {
        double total = 0;
        foreach (var c in text)
        {
            float width = 0;
            if (FPDFFont_GetGlyphWidth(font, c, fontSize, &width) != 0)
            {
                total += width;
            }
        }

        return total;
    }

    private static void ReadCharacters(nint textPage, Dictionary<nint, TextPiece> pieces)
    {
        var texts = new Dictionary<TextPiece, StringBuilder>();
        var count = FPDFText_CountChars(textPage);
        TextPiece? previous = null;
        var gap = TextGap.None;
        for (var i = 0; i < count; i++)
        {
            var unicode = FPDFText_GetUnicode(textPage, i);
            if (FPDFText_IsGenerated(textPage, i) == 1)
            {
                gap = unicode is '\r' or '\n' || gap == TextGap.LineBreak ? TextGap.LineBreak : TextGap.Space;
                continue;
            }

            var handle = FPDFText_GetTextObject(textPage, i);
            if (handle == 0 || !pieces.TryGetValue(handle, out var piece))
            {
                // Text inside a form XObject isn't editable; it separates the text around it.
                previous = null;
                gap = TextGap.None;
                continue;
            }

            if (!texts.TryGetValue(piece, out var text))
            {
                text = new StringBuilder();
                texts.Add(piece, text);
                piece.FirstCharacter = i;
                if (previous is not null)
                {
                    previous.Next = piece;
                    previous.GapAfter = gap;
                }
            }
            else if (piece != previous || gap == TextGap.LineBreak)
            {
                // The object's characters resume after another object's, or on another line.
                piece.IsWellFormed = false;
            }

            if (piece == previous && gap != TextGap.None)
            {
                text.Append(' ');
            }

            if (unicode == 0 || unicode > 0x10FFFF)
            {
                piece.IsWellFormed = false;
            }
            else if (unicode <= char.MaxValue)
            {
                // On Windows PDFium reports characters outside the BMP as two UTF-16 surrogates.
                text.Append((char)unicode);
            }
            else
            {
                text.Append(char.ConvertFromUtf32((int)unicode));
            }

            piece.LastCharacter = i;
            piece.LastUnicode = unicode;
            previous = piece;
            gap = TextGap.None;
        }

        foreach (var (piece, text) in texts)
        {
            piece.Text = text.ToString();
            double x, y;
            if (FPDFText_GetCharOrigin(textPage, piece.FirstCharacter, &x, &y) != 0)
            {
                piece.FirstOrigin = (x, y);
            }

            if (FPDFText_GetCharOrigin(textPage, piece.LastCharacter, &x, &y) != 0)
            {
                piece.LastOrigin = (x, y);
            }

            double left, right, bottom, top;
            if (FPDFText_GetCharBox(textPage, piece.LastCharacter, &left, &right, &bottom, &top) != 0)
            {
                piece.LastInkBox = new PdfRect((float)left, (float)bottom, (float)right, (float)top);
            }

            FsRectF loose;
            if (FPDFText_GetLooseCharBox(textPage, piece.LastCharacter, &loose) != 0)
            {
                piece.LastLooseBox = new PdfRect(loose.Left, loose.Bottom, loose.Right, loose.Top);
            }
        }
    }

    private static void ReadGeometry(TextPiece piece)
    {
        var handle = piece.Handle;
        FsMatrix matrix;
        piece.HasMatrix = FPDFPageObj_GetMatrix(handle, &matrix) != 0;
        piece.Matrix = piece.HasMatrix ? matrix : default;

        float size = 0;
        FPDFTextObj_GetFontSize(handle, &size);
        piece.FontSize = size;
        piece.Font = FPDFTextObj_GetFont(handle);
        piece.FontName = piece.Font == 0 ? string.Empty : RawFontName(piece.Font);
        piece.IsEmbedded = piece.Font != 0 && FPDFFont_GetIsEmbedded(piece.Font) == 1;
        piece.RenderMode = FPDFTextObj_GetTextRenderMode(handle);

        float left, bottom, right, top;
        if (FPDFPageObj_GetBounds(handle, &left, &bottom, &right, &top) != 0)
        {
            piece.Bounds = new PdfRect(left, bottom, right, top);
        }

        if (piece.HasCharacters && piece.Font != 0)
        {
            MeasureLastAdvance(piece);
        }
    }

    /// <summary>
    /// Sets the advance width of a piece's last character. The font gives a width by Unicode
    /// value, which PDFium maps back to a character code; for ligatures, alternate glyphs and some
    /// encodings that isn't the code that was drawn. PDFium's loose box of a character is its
    /// advance box, from the code that was drawn, united with its ink; a space's ink box is its
    /// advance box. So where the boxes show the advance, it is exact. Otherwise the advance ends
    /// within the ink, and the font's width must agree with that.
    /// </summary>
    private static void MeasureLastAdvance(TextPiece piece)
    {
        float width = 0;
        var hasWidth = piece.LastUnicode <= char.MaxValue
            && FPDFFont_GetGlyphWidth(piece.Font, piece.LastUnicode, piece.FontSize, &width) != 0;
        piece.LastAdvance = width;
        if (!piece.HasMatrix || !IsAxisAligned(piece.Matrix) || piece.LastInkBox is not { } ink || piece.LastLooseBox is not { } loose)
        {
            return;
        }

        var (inkStart, inkEnd, inkDepth) = ExtentAlongBaseline(piece, ink);
        var (_, looseEnd, _) = ExtentAlongBaseline(piece, loose);
        if (double.IsNaN(inkEnd) || double.IsNaN(looseEnd))
        {
            return;
        }

        var tolerance = piece.FontSize * 1e-3;
        var isSpace = inkDepth < piece.FontSize * 0.01 && Math.Abs(inkStart) < tolerance;

        // The union copies one box's edge, so the ends differ only where the advance box sticks out.
        if (looseEnd > inkEnd || isSpace)
        {
            piece.LastAdvance = (float)looseEnd;
            return;
        }

        piece.IsEndKnown = hasWidth && width > inkStart + tolerance && width <= inkEnd + tolerance;
    }

    /// <summary>Whether text with this matrix runs along the page's x or y axis, so its boxes are exact.</summary>
    private static bool IsAxisAligned(FsMatrix m)
    {
        var tolerance = Math.Max(Math.Max(Math.Abs(m.A), Math.Abs(m.B)), Math.Max(Math.Abs(m.C), Math.Abs(m.D))) * RelativeTolerance;
        return (Math.Abs(m.B) <= tolerance && Math.Abs(m.C) <= tolerance) || (Math.Abs(m.A) <= tolerance && Math.Abs(m.D) <= tolerance);
    }

    /// <summary>
    /// Where a page-space box starts and ends along a piece's baseline, measured from its last
    /// character's origin, and how deep it is across the baseline, in text space units.
    /// </summary>
    private static (double Start, double End, double Depth) ExtentAlongBaseline(TextPiece piece, PdfRect box)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        ReadOnlySpan<(float X, float Y)> corners = [(box.Left, box.Bottom), (box.Left, box.Top), (box.Right, box.Bottom), (box.Right, box.Top)];
        foreach (var (x, y) in corners)
        {
            var (u, v) = ToTextSpace(piece.Matrix, x - piece.LastOrigin.X, y - piece.LastOrigin.Y);
            if (double.IsNaN(u))
            {
                return (double.NaN, double.NaN, double.NaN);
            }

            (minU, maxU, minV, maxV) = (Math.Min(minU, u), Math.Max(maxU, u), Math.Min(minV, v), Math.Max(maxV, v));
        }

        return (minU, maxU, maxV - minV);
    }

    private static bool TryJoin(TextPiece previous, TextPiece next, out string separator)
    {
        separator = string.Empty;
        if (next.ObjectIndex != previous.ObjectIndex + 1 || !CanJoin(previous, next))
        {
            return false;
        }

        var gap = GapAlongBaseline(previous, previous.End, next.FirstOrigin);
        if (gap is not { } value || value > MaxGap * previous.FontSize || value < -MaxOverlap * previous.FontSize)
        {
            return false;
        }

        // A line break PDFium reports between characters on one baseline is a misjudged
        // direction (common for lines of one-character objects); the gap decides instead.
        var space = previous.GapAfter == TextGap.Space
            || (previous.GapAfter == TextGap.LineBreak && value >= SpaceWidth(previous) / 2);
        if (space && !char.IsWhiteSpace(previous.Text[^1]) && !char.IsWhiteSpace(next.Text[0]))
        {
            separator = " ";
        }

        return true;
    }

    /// <summary>
    /// A text object with no characters in PDFium's text extraction and a bounding box of zero
    /// size. PDFium skips objects that draw only spaces (and reports the gap as a generated space),
    /// so these are usually spaces, sometimes empty strings.
    /// </summary>
    private static bool IsSpacer(TextPiece piece) =>
        !piece.HasCharacters && piece.Font != 0 && piece.HasMatrix
        && piece.Bounds is { } b && b.Width < 0.01f && b.Height < 0.01f;

    /// <summary>
    /// Joins <paramref name="previous"/>, the spacers <c>pieces[first..end)</c> and the text object
    /// <c>pieces[end]</c> when the spacers sit on the line between them. Each spacer that leaves a
    /// gap of at least half a space becomes a space in the run's text.
    /// </summary>
    private static bool TryJoinAcrossSpacers(TextPiece previous, IReadOnlyList<TextPiece> pieces, int first, int end)
    {
        var next = pieces[end];
        if (next.ObjectIndex != pieces[end - 1].ObjectIndex + 1 || !CanJoin(previous, next))
        {
            return false;
        }

        var size = previous.FontSize;
        var position = previous.End;
        var starts = new List<double>();
        for (var i = first; i <= end; i++)
        {
            var piece = pieces[i];
            (double X, double Y) origin = i < end ? (piece.Matrix.E, piece.Matrix.F) : piece.FirstOrigin;
            var gap = GapAlongBaseline(previous, position, origin);
            if (gap is not { } value || value < (i == first ? -MaxOverlap * size : 0) || value > MaxGap * size
                || (i < end && !SameStyle(previous, piece)))
            {
                return false;
            }

            position = origin;
            starts.Add(ToTextSpace(previous.Matrix, origin.X - previous.LastOrigin.X, origin.Y - previous.LastOrigin.Y).U);
        }

        var spaceWidth = SpaceWidth(previous);
        for (var i = first; i < end; i++)
        {
            // PDFium doesn't measure spacers: take a normal space, but no wider than the gap it leaves.
            var spacer = pieces[i];
            var gap = starts[i - first + 1] - starts[i - first];
            var isSpace = gap >= spaceWidth / 2;
            spacer.Text = isSpace ? " " : string.Empty;
            spacer.FirstOrigin = spacer.LastOrigin = (spacer.Matrix.E, spacer.Matrix.F);
            spacer.LastAdvance = isSpace ? (float)Math.Min(spaceWidth, gap) : 0;
        }

        return true;
    }

    /// <summary>The width of a space in a piece's font and size, in text space units.</summary>
    private static float SpaceWidth(TextPiece piece)
    {
        float width = 0;
        FPDFFont_GetGlyphWidth(piece.Font, ' ', piece.FontSize, &width);
        return width > 0 ? width : piece.FontSize / 4;
    }

    /// <summary>Whether two text objects with characters can be in one run, apart from where they are.</summary>
    private static bool CanJoin(TextPiece previous, TextPiece next) =>
        previous.IsWellFormed && next.IsWellFormed && previous.HasCharacters && next.HasCharacters
        && previous.Next == next && SameStyle(previous, next);

    private static bool SameStyle(TextPiece a, TextPiece b) =>
        a.Font != 0 && (a.Font == b.Font || SameTypeface(a, b)) && a.RenderMode == b.RenderMode
        && a.FontSize > 0 && Math.Abs(a.FontSize - b.FontSize) <= a.FontSize * 1e-3
        && a.HasMatrix && b.HasMatrix && SameOrientation(a.Matrix, b.Matrix)
        && SameColors(a, b) && SameMarks(a, b) && SameClip(a, b);

    /// <summary>
    /// Whether two embedded fonts have the same name, as two subsets of one font do, and the complete
    /// copy PaperKnife adds for new letters and the font it copies (PDFium reports names without
    /// subset tags). Text in them belongs together.
    /// </summary>
    private static bool SameTypeface(TextPiece a, TextPiece b) =>
        a.IsEmbedded && b.IsEmbedded && b.Font != 0 && a.FontName.Length > 0 && string.Equals(a.FontName, b.FontName, StringComparison.Ordinal);

    /// <summary>
    /// How far <paramref name="to"/> lies beyond <paramref name="from"/> along the baseline of
    /// <paramref name="piece"/>, in text space units; null when it isn't on that baseline.
    /// </summary>
    private static double? GapAlongBaseline(TextPiece piece, (double X, double Y) from, (double X, double Y) to)
    {
        var (fromU, fromV) = ToTextSpace(piece.Matrix, from.X - piece.LastOrigin.X, from.Y - piece.LastOrigin.Y);
        var (toU, toV) = ToTextSpace(piece.Matrix, to.X - piece.LastOrigin.X, to.Y - piece.LastOrigin.Y);
        if (double.IsNaN(toU) || Math.Abs(toV - fromV) > MaxBaselineShift * piece.FontSize)
        {
            return null;
        }

        return toU - fromU;
    }

    private static bool SameOrientation(FsMatrix a, FsMatrix b)
    {
        var scale = Math.Max(Math.Max(Math.Abs(a.A), Math.Abs(a.B)), Math.Max(Math.Abs(a.C), Math.Abs(a.D)));
        var tolerance = scale * RelativeTolerance;
        return Math.Abs(a.A - b.A) <= tolerance && Math.Abs(a.B - b.B) <= tolerance
            && Math.Abs(a.C - b.C) <= tolerance && Math.Abs(a.D - b.D) <= tolerance;
    }

    private static bool SameColors(TextPiece a, TextPiece b)
    {
        if (!TryGetColor(a.Handle, stroke: false, out var fillA) || !TryGetColor(b.Handle, stroke: false, out var fillB) || fillA != fillB)
        {
            return false;
        }

        // Render modes 1, 2, 5 and 6 stroke the glyph outlines.
        if (a.RenderMode is 1 or 2 or 5 or 6)
        {
            return TryGetColor(a.Handle, stroke: true, out var strokeA) && TryGetColor(b.Handle, stroke: true, out var strokeB) && strokeA == strokeB;
        }

        return true;
    }

    private static bool TryGetColor(nint handle, bool stroke, out (uint R, uint G, uint B, uint A) color)
    {
        uint r = 0, g = 0, b = 0, a = 0;
        var ok = stroke
            ? FPDFPageObj_GetStrokeColor(handle, &r, &g, &b, &a) != 0
            : FPDFPageObj_GetFillColor(handle, &r, &g, &b, &a) != 0;
        color = (r, g, b, a);
        return ok;
    }

    private static bool SameMarks(TextPiece a, TextPiece b) =>
        FPDFPageObj_CountMarks(a.Handle) == FPDFPageObj_CountMarks(b.Handle)
        && FPDFPageObj_GetMarkedContentID(a.Handle) == FPDFPageObj_GetMarkedContentID(b.Handle);

    private static bool SameClip(TextPiece a, TextPiece b) =>
        GetClipSignature(a).AsSpan().SequenceEqual(GetClipSignature(b));

    private static float[] GetClipSignature(TextPiece piece)
    {
        if (piece.ClipSignature is { } cached)
        {
            return cached;
        }

        var values = new List<float>();
        var clip = FPDFPageObj_GetClipPath(piece.Handle);
        if (clip != 0)
        {
            var paths = FPDFClipPath_CountPaths(clip);
            for (var p = 0; p < paths; p++)
            {
                var segments = FPDFClipPath_CountPathSegments(clip, p);
                values.Add(segments);
                for (var s = 0; s < segments; s++)
                {
                    var segment = FPDFClipPath_GetPathSegment(clip, p, s);
                    float x = 0, y = 0;
                    if (segment != 0)
                    {
                        FPDFPathSegment_GetPoint(segment, &x, &y);
                    }

                    values.Add(segment == 0 ? -1 : FPDFPathSegment_GetType(segment));
                    values.Add(x);
                    values.Add(y);
                    values.Add(segment != 0 && FPDFPathSegment_GetClose(segment) != 0 ? 1 : 0);
                }
            }
        }

        piece.ClipSignature = [.. values];
        return piece.ClipSignature;
    }

    private static TextRunLayout CreateRun(int pageIndex, List<TextPiece> pieces, List<string> separators)
    {
        var text = new StringBuilder();
        PdfRect? bounds = null;
        for (var i = 0; i < pieces.Count; i++)
        {
            text.Append(pieces[i].Text);
            if (i < separators.Count)
            {
                text.Append(separators[i]);
            }

            if (pieces[i].Bounds is { } pieceBounds)
            {
                bounds = bounds is { } b ? b.Union(pieceBounds) : pieceBounds;
            }
        }

        var first = pieces[0];
        var (fontName, isSubset) = ReadFontName(first.Font);
        var m = first.Matrix;
        var scale = first.HasMatrix ? MathF.Sqrt((m.C * m.C) + (m.D * m.D)) : 1f;
        var fullText = text.ToString();
        var run = new PdfTextRun(
            pageIndex,
            first.ObjectIndex,
            pieces.Count,
            fullText.TrimEnd(),
            bounds ?? default,
            fontName,
            first.Font != 0 && FPDFFont_GetIsEmbedded(first.Font) == 1,
            isSubset,
            first.FontSize * scale,
            first.RenderMode == FPDF_TEXTRENDERMODE_INVISIBLE);
        return new TextRunLayout(run, pieces.ToArray(), separators.ToArray(), fullText);
    }

    private static (string Name, bool IsSubset) ReadFontName(nint font)
    {
        if (font == 0)
        {
            return ("(no font)", false);
        }

        var name = RawFontName(font);
        if (name.Length == 0)
        {
            return ("(unnamed font)", false);
        }

        // Subset fonts carry a six-uppercase-letter tag, e.g. "ABCDEF+Arial-BoldMT" (ISO 32000-1, 9.6.4).
        var isSubset = name.Length > 7 && name[6] == '+' && !name.AsSpan(0, 6).ContainsAnyExceptInRange('A', 'Z');
        return (isSubset ? name[7..] : name, isSubset);
    }

    /// <summary>The font's name as PDFium reports it, or empty.</summary>
    private static string RawFontName(nint font)
    {
        var length = FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1)
        {
            return string.Empty;
        }

        var buffer = new byte[(int)length];
        fixed (byte* bytes = buffer)
        {
            FPDFFont_GetBaseFontName(font, bytes, length);
        }

        return Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
    }
}
