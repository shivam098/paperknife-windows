using System.Globalization;

namespace PaperKnife.Core.Content;

/// <summary>
/// Character encodings of simple fonts (ISO 32000-1, Annex D) and glyph names mapped to Unicode,
/// for fonts without a ToUnicode map. Covers WinAnsiEncoding fully, and the ASCII range of
/// StandardEncoding and MacRomanEncoding.
/// </summary>
internal static class Encodings
{
    // WinAnsiEncoding glyph names for codes 32 to 255; empty where the code is unused.
    private static readonly string[] WinAnsi =
    [
        "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "braceleft", "bar", "braceright", "asciitilde", "",
        "Euro", "", "quotesinglbase", "florin", "quotedblbase", "ellipsis", "dagger", "daggerdbl",
        "circumflex", "perthousand", "Scaron", "guilsinglleft", "OE", "", "Zcaron", "",
        "", "quoteleft", "quoteright", "quotedblleft", "quotedblright", "bullet", "endash", "emdash",
        "tilde", "trademark", "scaron", "guilsinglright", "oe", "", "zcaron", "Ydieresis",
        "space", "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section",
        "dieresis", "copyright", "ordfeminine", "guillemotleft", "logicalnot", "hyphen", "registered", "macron",
        "degree", "plusminus", "twosuperior", "threesuperior", "acute", "mu", "paragraph", "periodcentered",
        "cedilla", "onesuperior", "ordmasculine", "guillemotright", "onequarter", "onehalf", "threequarters", "questiondown",
        "Agrave", "Aacute", "Acircumflex", "Atilde", "Adieresis", "Aring", "AE", "Ccedilla",
        "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute", "Icircumflex", "Idieresis",
        "Eth", "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply",
        "Oslash", "Ugrave", "Uacute", "Ucircumflex", "Udieresis", "Yacute", "Thorn", "germandbls",
        "agrave", "aacute", "acircumflex", "atilde", "adieresis", "aring", "ae", "ccedilla",
        "egrave", "eacute", "ecircumflex", "edieresis", "igrave", "iacute", "icircumflex", "idieresis",
        "eth", "ntilde", "ograve", "oacute", "ocircumflex", "otilde", "odieresis", "divide",
        "oslash", "ugrave", "uacute", "ucircumflex", "udieresis", "yacute", "thorn", "ydieresis",
    ];

    // Windows-1252 characters for codes 128 to 159, which differ from Latin-1.
    private const string Cp1252High =
        "\u20AC\0\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\0\u017D\0" +
        "\0\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\0\u017E\u0178";

    private static readonly Dictionary<string, string> GlyphUnicode = BuildGlyphUnicode();

    public static string? WinAnsiName(int code) =>
        code is >= 32 and <= 255 && WinAnsi[code - 32].Length > 0 ? WinAnsi[code - 32] : null;

    /// <summary>StandardEncoding, for the ASCII range only.</summary>
    public static string? StandardName(int code) => code switch
    {
        39 => "quoteright",
        96 => "quoteleft",
        >= 32 and <= 126 => WinAnsiName(code),
        _ => null,
    };

    /// <summary>MacRomanEncoding, for the ASCII range only.</summary>
    public static string? MacRomanName(int code) => code is >= 32 and <= 126 ? WinAnsiName(code) : null;

    /// <summary>
    /// Text with the Latin ligatures U+FB00 to U+FB06 replaced by their letters, the way PDFium's
    /// text extraction reports them (cpdf_textpage.cpp, AddCharInfo).
    /// </summary>
    public static string ExpandLigatures(string text)
    {
        if (!text.Any(c => c is >= '\uFB00' and <= '\uFB06'))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\uFB00' => "ff",
                '\uFB01' => "fi",
                '\uFB02' => "fl",
                '\uFB03' => "ffi",
                '\uFB04' => "ffl",
                '\uFB05' => "\u017Ft",
                '\uFB06' => "st",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>The text a glyph name stands for (Adobe Glyph List conventions), or null.</summary>
    public static string? GlyphToUnicode(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            name = name[..dot];
        }

        if (name.Contains('_', StringComparison.Ordinal))
        {
            var parts = name.Split('_').Select(GlyphToUnicode).ToList();
            return parts.All(p => p is not null) ? string.Concat(parts) : null;
        }

        if (GlyphUnicode.TryGetValue(name, out var known))
        {
            return known;
        }

        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bmp))
        {
            return ((char)bmp).ToString();
        }

        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar)
            && scalar <= 0x10FFFF && scalar is < 0xD800 or > 0xDFFF)
        {
            return char.ConvertFromUtf32(scalar);
        }

        return null;
    }

    private static Dictionary<string, string> BuildGlyphUnicode()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var code = 32; code <= 255; code++)
        {
            if (WinAnsiName(code) is { } name)
            {
                var c = code switch
                {
                    >= 128 and <= 159 => Cp1252High[code - 128],
                    _ => (char)code,
                };
                map.TryAdd(name, c.ToString());
            }
        }

        foreach (var (name, text) in new[]
        {
            ("quoteright", "\u2019"), ("quoteleft", "\u2018"), ("nbspace", "\u00A0"), ("sfthyphen", "\u00AD"),
            ("minus", "\u2212"), ("dotlessi", "\u0131"), ("Lslash", "\u0141"), ("lslash", "\u0142"),
            ("fi", "\uFB01"), ("fl", "\uFB02"), ("ff", "\uFB00"), ("ffi", "\uFB03"), ("ffl", "\uFB04"),
            ("fraction", "\u2044"), ("ring", "\u02DA"), ("ogonek", "\u02DB"), ("caron", "\u02C7"),
            ("dotaccent", "\u02D9"), ("hungarumlaut", "\u02DD"), ("breve", "\u02D8"), ("Delta", "\u2206"),
            ("Omega", "\u2126"), ("pi", "\u03C0"), ("mu", "\u00B5"), ("rupee", "\u20B9"), ("uni20B9", "\u20B9"),
            ("Gamma", "\u0393"), ("Theta", "\u0398"), ("Lambda", "\u039B"), ("Xi", "\u039E"), ("Pi", "\u03A0"),
            ("Sigma", "\u03A3"), ("Upsilon", "\u03A5"), ("Phi", "\u03A6"), ("Psi", "\u03A8"),
        })
        {
            map.TryAdd(name, text);
        }

        return map;
    }
}
