using System.Text;
using PaperKnife.Core.Content;

namespace PaperKnife.Core.Editing;

/// <summary>A glyph to draw: its code, its advance for a font size of 1, and the TJ adjustment after it.</summary>
internal readonly record struct PlannedGlyph(uint Code, double Width, double KerningAfter);

/// <summary>
/// A glyph of the text being replaced: its code, width and the TJ adjustment after it, the text
/// PDFium reads for it, and whether PDFium reads that adjustment as a space. <see cref="Reusable"/>
/// is false when the glyph was drawn in a different text state than the edited object's, so its
/// code and adjustments can't be copied.
/// </summary>
internal readonly record struct OldGlyph(
    uint Code,
    double Width,
    double KerningAfter,
    string Text,
    int Show,
    bool SpaceAfter,
    bool SeparatorBefore,
    bool Reusable);

/// <summary>How a text object's new text is drawn: its glyphs and the TJ adjustments around them.</summary>
internal sealed class GlyphPlan
{
    /// <summary>The TJ adjustment before the first glyph, in thousandths of an em (negative moves right).</summary>
    public required double InitialKerning { get; init; }

    public required IReadOnlyList<PlannedGlyph> Glyphs { get; init; }

    /// <summary>The gap that spaces after the last glyph (or spaces alone) leave, as a TJ adjustment.</summary>
    public required double TrailingKerning { get; init; }

    /// <summary>The number of glyphs up to the last character that isn't a space.</summary>
    public required int VisibleGlyphs { get; init; }

    /// <summary>Characters the font has no code for.</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>The characters drawn with codes chosen for them rather than kept from the old text, whose glyphs must be checked.</summary>
    public required IReadOnlyList<(string Text, uint Code)> NewGlyphs { get; init; }

    public string NewCharacters => string.Concat(NewGlyphs.Select(g => g.Text));

    public bool HasAdjustments => InitialKerning != 0 || Glyphs.Any(g => g.KerningAfter != 0);
}

/// <summary>
/// Chooses the glyphs that draw a text object's new text. Text kept from the old object keeps its
/// codes, its kerning and the gaps PDFium reads as spaces, so an edit changes only what changed.
/// Spaces the font can't draw become gaps, the way TeX sets every space: TeX fonts have no space
/// glyph, so without this no text with a space could be written in them.
/// </summary>
internal sealed class GlyphPlanner
{
    private const long MaxAlignmentCells = 4_000_000;

    public required PdfFontInfo Font { get; init; }

    /// <summary>The advance of a code for a font size of 1, as PDFium computes it.</summary>
    public required Func<uint, double?> Width { get; init; }

    /// <summary>Codes to prefer when a character has several, normally the ones the old text used.</summary>
    public required IReadOnlySet<uint> Preferred { get; init; }

    /// <summary>A code that draws a space, or null when spaces have to be gaps.</summary>
    public required uint? SpaceCode { get; init; }

    /// <summary>The usual gap between words in this font, in thousandths of an em.</summary>
    public required Func<double> WordGap { get; init; }

    /// <summary>The glyphs being replaced, or null when they can't be matched to the text PDFium reads.</summary>
    public required IReadOnlyList<OldGlyph>? Old { get; init; }

    public GlyphPlan Plan(string newText)
    {
        var old = Old ?? [];
        var oldChars = OldCharacters(old);
        var aligned = Align(oldChars, newText);
        var glyphs = new List<PlannedGlyph>();
        var sources = new List<int>();
        var pending = new List<int>();
        var missing = new List<string>();
        var fresh = new List<(string Text, uint Code)>();
        double initial = 0;
        var visible = 0;
        var j = 0;
        while (j < newText.Length)
        {
            var c = newText[j];
            var at = aligned[j];
            var source = at >= 0 ? oldChars[at] : default;
            if (IsSpace(c))
            {
                j++;
                if (at >= 0 && source.Kind == CharKind.Gap)
                {
                    pending.Add(source.Glyph);
                }
                else if (at >= 0 && source.Kind == CharKind.Glyph && old[source.Glyph] is { Text: " ", Reusable: true } space)
                {
                    Add(space.Code, space.Width, source.Glyph);
                }
                else if (SpaceCode is { } code && Width(code) is { } width)
                {
                    Add(code, width, -1);
                }
                else
                {
                    pending.Add(-1);
                }

                continue;
            }

            if (Reused(j) is { } reused)
            {
                Add(old[reused].Code, old[reused].Width, reused);
                j += old[reused].Text.Length;
                visible = glyphs.Count;
                continue;
            }

            var (codes, length) = Encode(newText, j);
            if (codes is not [var single])
            {
                var text = char.IsSurrogatePair(newText, j) ? newText.Substring(j, 2) : c.ToString();
                if (!missing.Contains(text))
                {
                    missing.Add(text);
                }

                j += text.Length;
                continue;
            }

            fresh.Add((newText.Substring(j, length), single));
            Add(single, Width(single) ?? double.NaN, -1);
            j += length;
            visible = glyphs.Count;
        }

        var trailing = pending.Count > 0 ? -pending.Sum(Gap) : 0;
        return new GlyphPlan
        {
            InitialKerning = initial,
            Glyphs = glyphs,
            TrailingKerning = trailing,
            VisibleGlyphs = visible,
            Missing = missing,
            NewGlyphs = fresh,
        };

        void Add(uint code, double width, int from)
        {
            if (glyphs.Count == 0)
            {
                initial = pending.Count > 0 ? -pending.Sum(Gap) : 0;
            }
            else
            {
                var previous = sources[^1];
                double kerning = 0;
                if (pending is [var gap] && gap >= 0 && gap == previous && from == previous + 1)
                {
                    // The same two glyphs with the same space between them: the original adjustment.
                    kerning = old[previous].KerningAfter;
                }
                else if (pending.Count > 0)
                {
                    // PDFium must still read the gap as a space; widen it only when it might not.
                    var amount = pending.Sum(Gap);
                    var needed = SpaceThreshold(Font, Width, code);
                    kerning = -(amount < needed * 1.02 ? needed * 1.25 : amount);
                }
                else if (previous >= 0 && from == previous + 1 && old[from].Show == old[previous].Show && !old[previous].SpaceAfter)
                {
                    kerning = old[previous].KerningAfter;
                }

                glyphs[^1] = glyphs[^1] with { KerningAfter = kerning };
            }

            glyphs.Add(new PlannedGlyph(code, width, 0));
            sources.Add(from);
            pending.Clear();
        }

        // An old glyph whose whole text the new text keeps at this position.
        int? Reused(int start)
        {
            var at = aligned[start];
            if (at < 0 || oldChars[at] is not { Kind: CharKind.Glyph, Offset: 0 } first || !old[first.Glyph].Reusable)
            {
                return null;
            }

            var text = old[first.Glyph].Text;
            for (var t = 1; t < text.Length; t++)
            {
                if (start + t >= newText.Length || aligned[start + t] != at + t)
                {
                    return null;
                }
            }

            return first.Glyph;
        }
    }

    /// <summary>The smallest gap before a code, in thousandths of an em, that PDFium reads as a space (cpdf_textpage.cpp).</summary>
    public static double SpaceThreshold(PdfFontInfo font, Func<uint, double?> width, uint next)
    {
        double threshold = 0;
        if (font.FirstCodeFor(" ") is { } space && width(space) is { } spaceWidth)
        {
            threshold = spaceWidth * 1000 > 1000.0 / 3 ? 0 : spaceWidth * 1000 / 2;
        }

        if (threshold <= 0)
        {
            var w = (width(next) ?? 0) * 1000;
            threshold = w < 300 ? w / 2 : w < 500 ? w / 4 : w < 700 ? w / 5 : w / 6;
        }

        return threshold;
    }

    /// <summary>
    /// Reads the glyphs of the text objects being replaced, matching them to the text PDFium reads
    /// for each object (<paramref name="pieces"/>), which includes a space wherever PDFium reads a
    /// TJ adjustment as one. Returns null when the two don't match exactly.
    /// </summary>
    public static List<OldGlyph>? ReadOldGlyphs(
        IReadOnlyList<ShowOp> shows,
        IReadOnlyList<TextPiece> pieces,
        IReadOnlyList<string> separators,
        Func<ShowOp, uint, double?> width,
        ShowOp edited)
    {
        var glyphs = new List<OldGlyph>();
        for (var s = 0; s < shows.Count; s++)
        {
            var show = shows[s];
            if (show.Font is not { } font || !pieces[s].HasCharacters)
            {
                return null;
            }

            var text = TextEquivalence.Normalize(pieces[s].Text);
            var codes = show.Codes();
            var reusable = ReferenceEquals(font, edited.Font) && show.FontSize == edited.FontSize && show.CharSpace == edited.CharSpace
                && show.WordSpace == edited.WordSpace && show.HorizontalScale == edited.HorizontalScale && show.Rise == edited.Rise;
            var position = 0;
            for (var i = 0; i < codes.Count; i++)
            {
                var (code, kerning) = codes[i];
                if (font.Unicode(code) is not { Length: > 0 } unicode || width(show, code) is not { } w)
                {
                    return null;
                }

                var glyphText = TextEquivalence.Normalize(Encodings.ExpandLigatures(unicode));
                if (position + glyphText.Length > text.Length || string.CompareOrdinal(text, position, glyphText, 0, glyphText.Length) != 0)
                {
                    return null;
                }

                position += glyphText.Length;

                // PDFium adds a space for a large enough adjustment after a glyph, before the next one.
                var spaceAfter = i < codes.Count - 1 && kerning != 0 && position < text.Length && text[position] == ' '
                    && !(font.Unicode(codes[i + 1].Code) is { } next && next.StartsWith(' ') && (position + 1 >= text.Length || text[position + 1] != ' '));
                if (spaceAfter)
                {
                    position++;
                }

                glyphs.Add(new OldGlyph(code, w, kerning, Comparable(unicode), s, spaceAfter, i == 0 && s > 0 && separators[s - 1] == " ", reusable));
            }

            if (position != text.Length)
            {
                return null;
            }
        }

        return glyphs;
    }

    private static bool IsSpace(char c) => c is ' ' or '\u00A0';

    /// <summary>
    /// Text as alignment compares it: ligatures as their letters, as PDFium reads them, and a
    /// no-break space and a space may stand in for each other, as <see cref="PdfFontInfo.Encode"/>
    /// lets them, but other spaces differ (an ideographic space is a full em wide).
    /// </summary>
    private static string Comparable(string text) =>
        Encodings.ExpandLigatures(text).Normalize(NormalizationForm.FormC).Replace('\u00A0', ' ');

    private static List<OldChar> OldCharacters(IReadOnlyList<OldGlyph> old)
    {
        var chars = new List<OldChar>();
        for (var k = 0; k < old.Count; k++)
        {
            if (old[k].SeparatorBefore)
            {
                chars.Add(new OldChar(' ', CharKind.Separator, -1, 0));
            }

            for (var t = 0; t < old[k].Text.Length; t++)
            {
                chars.Add(new OldChar(old[k].Text[t], CharKind.Glyph, k, t));
            }

            if (old[k].SpaceAfter)
            {
                chars.Add(new OldChar(' ', CharKind.Gap, k, 0));
            }
        }

        return chars;
    }

    /// <summary>For each character of the new text, the old character it keeps (a longest common subsequence), or -1.</summary>
    private static int[] Align(List<OldChar> old, string text)
    {
        var result = Enumerable.Repeat(-1, text.Length).ToArray();
        var n = old.Count;
        var m = text.Length;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > MaxAlignmentCells)
        {
            return result;
        }

        var normalized = Comparable(text);
        if (normalized.Length != m)
        {
            return result;
        }

        var lengths = new int[(n + 1) * (m + 1)];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var k = m - 1; k >= 0; k--)
            {
                lengths[(i * (m + 1)) + k] = old[i].C == normalized[k]
                    ? lengths[((i + 1) * (m + 1)) + k + 1] + 1
                    : Math.Max(lengths[((i + 1) * (m + 1)) + k], lengths[(i * (m + 1)) + k + 1]);
            }
        }

        for (int i = 0, k = 0; i < n && k < m;)
        {
            if (old[i].C == normalized[k])
            {
                result[k++] = i++;
            }
            else if (lengths[((i + 1) * (m + 1)) + k] >= lengths[(i * (m + 1)) + k + 1])
            {
                i++;
            }
            else
            {
                k++;
            }
        }

        return result;
    }

    /// <summary>The width of a gap, in thousandths of an em: an old gap it keeps, or the font's usual word gap.</summary>
    private double Gap(int oldGlyph) => oldGlyph >= 0 && Old is { } old ? -old[oldGlyph].KerningAfter : WordGap();

    /// <summary>Codes for the character at <paramref name="start"/>, or for a ligature starting there when the font draws the characters only together.</summary>
    private (List<uint>? Codes, int Length) Encode(string text, int start)
    {
        var length = char.IsSurrogatePair(text, start) ? 2 : 1;
        var codes = Font.Encode(text.Substring(start, length), Preferred, out var missing);
        if (missing.Count == 0)
        {
            return (codes, length);
        }

        for (var ligature = 3; ligature >= 2; ligature--)
        {
            if (start + ligature <= text.Length && Font.CodeFor(text.Substring(start, ligature), Preferred) is { } code)
            {
                return ([code], ligature);
            }
        }

        return (null, length);
    }

    private enum CharKind
    {
        Glyph,
        Gap,
        Separator,
    }

    private readonly record struct OldChar(char C, CharKind Kind, int Glyph, int Offset);
}
