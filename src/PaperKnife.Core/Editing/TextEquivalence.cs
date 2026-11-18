using System.Globalization;
using System.Text;

namespace PaperKnife.Core.Editing;

/// <summary>
/// Compares text the way a reader sees it. Fonts often share one glyph between several code points
/// (for example U+0020 SPACE and U+00A0 NO-BREAK SPACE), so text read back from a PDF can differ
/// from what was written without any visible difference. Only canonically equivalent characters
/// (NFC, which also maps U+2126 OHM SIGN to U+03A9) and space separators are treated as equal;
/// compatibility variants such as "²" and "2" or "…" and "..." stay different because they look
/// different.
/// </summary>
internal static class TextEquivalence
{
    public static bool AreEquivalent(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>
    /// Like <see cref="AreEquivalent"/>, but ignores how much whitespace separates words and any at
    /// the ends. PDFium's text extraction skips text objects that draw only spaces and reports gaps
    /// as single generated spaces, so the spacing of rewritten text can't be read back exactly.
    /// Every space is checked to be drawable before it is written (see <see cref="TextSupport"/>).
    /// </summary>
    public static bool AreEquivalentIgnoringSpacing(string a, string b) =>
        string.Equals(CollapseSpaces(Normalize(a)), CollapseSpaces(Normalize(b)), StringComparison.Ordinal);

    public static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        return string.Create(normalized.Length, normalized, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = CharUnicodeInfo.GetUnicodeCategory(source[i]) == UnicodeCategory.SpaceSeparator ? ' ' : source[i];
            }
        });
    }

    /// <summary>Characters of <paramref name="expected"/> that are missing from <paramref name="actual"/>.</summary>
    public static IReadOnlyList<string> Lost(string expected, string actual)
    {
        var have = Normalize(actual);
        return expected
            .Where(c => !have.Contains(Normalize(c.ToString()), StringComparison.Ordinal))
            .Select(c => c.ToString())
            .Distinct()
            .ToList();
    }

    private static string CollapseSpaces(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
