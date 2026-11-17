using System.Globalization;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core.Editing;

internal sealed record TextSupportResult(string? Problem, IReadOnlyList<string> Characters)
{
    public static TextSupportResult Supported { get; } = new(null, []);

    public bool IsSupported => Problem is null;
}

/// <summary>
/// Decides, before anything changes, whether new text can be drawn with the font a text object
/// already uses. PDFium's <c>FPDFText_SetText</c> silently turns characters it can't encode into
/// character code 0, so these checks (and a round-trip check after the edit) prevent garbage text.
/// </summary>
internal static unsafe class TextSupport
{
    // A Unicode noncharacter that fonts don't map, so it resolves to the font's .notdef glyph.
    private const uint MissingGlyphProbe = 0xFFFF;
    private const float ProbeSize = 12f;

    public static TextSupportResult Check(nint font, string fontName, string text)
    {
        if (font == 0)
        {
            return new("This text has no usable font, so it can't be edited.", []);
        }

        var layout = Distinct(text, IsLayoutCharacter);
        if (layout.Count > 0)
        {
            return new("Line breaks and tabs can't be added to a single line of text yet.", layout);
        }

        if (text.Any(char.IsSurrogate))
        {
            var runes = text.EnumerateRunes().Where(r => !r.IsBmp).Select(r => r.ToString()).Distinct().ToList();
            return new("Emoji and other characters outside the Basic Multilingual Plane aren't supported yet.", runes);
        }

        var shaping = Distinct(text, NeedsShaping);
        if (shaping.Count > 0)
        {
            return new("Scripts that need text shaping (for example Hindi, Arabic or Thai) can't be edited yet.", shaping);
        }

        var missing = FindMissingGlyphs(font, text);
        if (missing.Count > 0)
        {
            return new($"The font “{fontName}” in this PDF doesn't contain: {string.Join(' ', missing)}", missing);
        }

        return TextSupportResult.Supported;
    }

    private static bool IsLayoutCharacter(char c) =>
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static bool NeedsShaping(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return true;
        }

        return c switch
        {
            >= '\u0590' and <= '\u08FF' => true, // Hebrew, Arabic, Syriac, Thaana, NKo and related
            >= '\u0900' and <= '\u0DFF' => true, // Devanagari through Sinhala
            >= '\u0E00' and <= '\u0FFF' => true, // Thai, Lao, Tibetan
            >= '\u1000' and <= '\u109F' => true, // Myanmar
            >= '\u1780' and <= '\u18AF' => true, // Khmer, Mongolian
            >= '\uFB1D' and <= '\uFDFF' => true, // Hebrew and Arabic presentation forms
            >= '\uFE70' and <= '\uFEFF' => true, // Arabic presentation forms B
            _ => false,
        };
    }

    private static List<string> FindMissingGlyphs(nint font, string text)
    {
        var missing = new List<string>();
        var notDef = GlyphOutline(font, MissingGlyphProbe);
        var seen = new HashSet<char>();
        foreach (var c in text)
        {
            if (!seen.Add(c))
            {
                continue;
            }

            float width = 0;
            var drawable = FPDFFont_GetGlyphWidth(font, c, ProbeSize, &width) != 0 && width > 0;
            if (drawable)
            {
                var outline = GlyphOutline(font, c);
                drawable = char.IsWhiteSpace(c)
                    // A real space draws nothing. An outline means PDFium fell back to the
                    // font's missing-glyph shape (usually a box), which takes up width too.
                    ? outline is null or { Length: 0 }
                    : outline is { Length: > 0 } && (notDef is not { Length: > 0 } || !outline.AsSpan().SequenceEqual(notDef));
            }

            if (!drawable)
            {
                missing.Add(c.ToString());
            }
        }

        return missing;
    }

    /// <summary>A comparable signature of a glyph's outline, or null when PDFium has no glyph at all.</summary>
    private static float[]? GlyphOutline(nint font, uint codePoint)
    {
        var path = FPDFFont_GetGlyphPath(font, codePoint, ProbeSize);
        if (path == 0)
        {
            return null;
        }

        var count = FPDFGlyphPath_CountGlyphSegments(path);
        if (count <= 0)
        {
            return [];
        }

        var outline = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var segment = FPDFGlyphPath_GetGlyphPathSegment(path, i);
            float x = 0, y = 0;
            if (segment != 0)
            {
                FPDFPathSegment_GetPoint(segment, &x, &y);
            }

            outline[i * 3] = segment == 0 ? -1 : FPDFPathSegment_GetType(segment);
            outline[(i * 3) + 1] = x;
            outline[(i * 3) + 2] = y;
        }

        return outline;
    }

    private static List<string> Distinct(string text, Func<char, bool> predicate) =>
        text.Where(predicate).Select(Describe).Distinct().ToList();

    private static string Describe(char c) => c switch
    {
        '\n' or '\r' or '\u2028' => "line break",
        '\u2029' => "paragraph break",
        '\t' => "tab",
        _ when char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format
            => string.Create(CultureInfo.InvariantCulture, $"U+{(int)c:X4}"),
        _ => c.ToString(),
    };
}
