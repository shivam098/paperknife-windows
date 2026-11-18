using System.Collections.Concurrent;

namespace PaperKnife.Core.Fonts;

/// <summary>
/// The fonts PaperKnife ships to draw new letters in subset fonts: the Type 1 Computer Modern fonts from the
/// American Mathematical Society's amsfonts 3.04 (SIL Open Font License 1.1), the versions TeX
/// distributions embed in PDFs. Unmodified; see third_party/amsfonts.
/// </summary>
internal static class BundledFonts
{
    private const string ResourcePrefix = "PaperKnife.Fonts.";

    private static readonly ConcurrentDictionary<string, Type1Font?> Cache = new(StringComparer.Ordinal);

    /// <summary>The full font with this PostScript name (for example "CMBX10"), or null when PaperKnife doesn't ship it.</summary>
    public static Type1Font? FindType1(string postScriptName) =>
        postScriptName.Length is > 0 and <= 32 && postScriptName.All(char.IsAsciiLetterOrDigit)
            ? Cache.GetOrAdd(postScriptName, Load)
            : null;

    private static Type1Font? Load(string name)
    {
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream($"{ResourcePrefix}{name.ToLowerInvariant()}.pfb");
        if (stream is null)
        {
            return null;
        }

        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Type1Font.FromPfb(data) is { } font && font.FontName == name ? font : null;
    }
}
