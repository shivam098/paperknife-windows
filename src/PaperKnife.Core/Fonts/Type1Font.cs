using System.Buffers.Binary;
using System.Globalization;
using PaperKnife.Core.Content;

namespace PaperKnife.Core.Fonts;

/// <summary>
/// A Type 1 font program (Adobe Type 1 Font Format, 1990), read far enough to tell whether a subset
/// was made from a given full font: its builtin encoding, its decrypted glyph programs
/// (charstrings) and subroutines, and the values that change how glyphs render. Reading is strict:
/// anything outside the usual structure (a repeated key, an unexpected token among the glyphs, a
/// glyph or subroutine defined twice, a multiple master font) makes it refuse the font rather
/// than guess how a renderer would read it. It runs in linear time on bounded input.
/// </summary>
internal sealed class Type1Font
{
    /// <summary>The largest cleartext part and whole program PaperKnife reads; real fonts are far smaller.</summary>
    public const int MaxCleartext = 256 * 1024;

    public const int MaxProgram = 4 * 1024 * 1024;

    private const int EexecKey = 55665;
    private const int CharStringKey = 4330;
    private const int MaxEntries = 65536;
    private const int MaxTokens = 1 << 20;
    private const int MaxSteps = 4_000_000;

    // Private dictionary values that change how glyphs render (hinting). Fonts that differ in them
    // aren't the same font, even if their outlines match.
    private static readonly HashSet<string> RenderingKeys = new(StringComparer.Ordinal)
    {
        "BlueValues", "OtherBlues", "FamilyBlues", "FamilyOtherBlues", "BlueScale", "BlueShift", "BlueFuzz",
        "StdHW", "StdVW", "StemSnapH", "StemSnapV", "ForceBold", "LanguageGroup", "RndStemUp", "ExpansionFactor",
    };

    private Type1Font()
    {
    }

    public string FontName { get; private init; } = string.Empty;

    public IReadOnlyList<double> FontMatrix { get; private init; } = [];

    /// <summary>The scale from glyph space to text space, normally 0.001.</summary>
    public double Scale => FontMatrix[0] > 0 ? FontMatrix[0] : 0.001;

    public int PaintType { get; private init; }

    /// <summary>The number of random bytes before each glyph program; -1 when they aren't encrypted.</summary>
    public int LenIV { get; private init; }

    /// <summary>The builtin encoding: glyph names by code. Empty when the font uses StandardEncoding.</summary>
    public IReadOnlyDictionary<int, string> Encoding { get; private init; } = new Dictionary<int, string>();

    public bool UsesStandardEncoding { get; private init; }

    /// <summary>Glyph programs by glyph name, decrypted, without the leading random bytes.</summary>
    public IReadOnlyDictionary<string, byte[]> CharStrings { get; private init; } = new Dictionary<string, byte[]>();

    public IReadOnlyDictionary<int, byte[]> Subrs { get; private init; } = new Dictionary<int, byte[]>();

    public IReadOnlyDictionary<string, string> RenderingValues { get; private init; } = new Dictionary<string, string>();

    /// <summary>The program the way a /FontFile stream holds it: cleartext, then the encrypted part, then the trailer.</summary>
    public byte[] Program { get; private init; } = [];

    public int Length1 { get; private init; }

    public int Length2 { get; private init; }

    public int Length3 { get; private init; }

    /// <summary>Reads a font in the PFB format Type 1 fonts are distributed in.</summary>
    public static Type1Font? FromPfb(ReadOnlySpan<byte> pfb)
    {
        if (pfb.Length > MaxProgram + 64)
        {
            return null;
        }

        var clear = new MemoryStream();
        var encrypted = new MemoryStream();
        var trailer = new MemoryStream();
        var at = 0;
        while (at + 6 <= pfb.Length && pfb[at] == 0x80 && pfb[at + 1] is 1 or 2)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(pfb[(at + 2)..]);
            if (length < 0 || length > pfb.Length - at - 6)
            {
                return null;
            }

            var segment = pfb.Slice(at + 6, length);
            var target = pfb[at + 1] == 2 ? encrypted : encrypted.Length == 0 ? clear : trailer;
            target.Write(segment);
            at += 6 + length;
        }

        return Parse(clear.ToArray(), encrypted.ToArray(), trailer.ToArray());
    }

    /// <summary>Reads a font embedded in a PDF (/FontFile), whose first two parts have the lengths /Length1 and /Length2.</summary>
    public static Type1Font? FromEmbedded(byte[] data, int length1, int length2)
    {
        if (data.Length > MaxProgram || length1 <= 0 || length2 <= 0 || length1 > data.Length || length2 > data.Length - length1)
        {
            return null;
        }

        return Parse(data[..length1], data[length1..(length1 + length2)], data[(length1 + length2)..]);
    }

    /// <summary>
    /// The builtin encoding of a program from its cleartext part alone: whether it is StandardEncoding,
    /// and otherwise the glyph names by code. Null when it can't be read.
    /// </summary>
    public static (bool Standard, IReadOnlyDictionary<int, string> Names)? ReadEncoding(byte[] cleartext) =>
        ReadCleartext(cleartext) is { } header ? (header.StandardEncoding, header.Encoding) : null;

    /// <summary>The PostScript name a program gives itself in its cleartext part, or null when it can't be read.</summary>
    public static string? ReadFontName(byte[] cleartext) => ReadCleartext(cleartext)?.Name;

    /// <summary>The advance width of a glyph in text space units (1000 per em for most fonts), or null.</summary>
    public double? Width(string glyph)
    {
        if (!CharStrings.TryGetValue(glyph, out var program))
        {
            return null;
        }

        // The width comes first, from hsbw (sbx wx) or sbw (sbx sby wx wy), possibly computed with div.
        var stack = new List<double>();
        for (var i = 0; i < program.Length;)
        {
            int v = program[i++];
            if (v >= 32)
            {
                if (CharStringAnalyzer.ReadNumber(program, v, ref i) is not { } number || stack.Count >= 24)
                {
                    return null;
                }

                stack.Add(number);
                continue;
            }

            switch (v)
            {
                case 13:
                    return stack.Count >= 2 ? stack[1] * Scale * 1000 : null;
                case 12 when i < program.Length:
                    var escaped = program[i++];
                    if (escaped == 7)
                    {
                        return stack.Count >= 3 ? stack[2] * Scale * 1000 : null;
                    }

                    if (escaped == 12 && stack.Count >= 2 && stack[^1] != 0)
                    {
                        var quotient = stack[^2] / stack[^1];
                        stack.RemoveRange(stack.Count - 2, 2);
                        stack.Add(quotient);
                        continue;
                    }

                    return null;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Why this font, a subset, isn't part of <paramref name="full"/>, or null when it is: same name,
    /// matrix, paint type, glyph encryption and rendering values; every glyph it has is identical in
    /// <paramref name="full"/>, and so is every subroutine those glyphs call, directly or not
    /// (subsetters replace the subroutines nothing calls by stubs); the glyphs an accented glyph is
    /// built from are there too; and its encoding maps codes to the same glyphs. Then every glyph
    /// it can draw renders the same with <paramref name="full"/>.
    /// </summary>
    public string? DifferenceFrom(Type1Font full)
    {
        if (BaseName(FontName) != BaseName(full.FontName))
        {
            return "it has a different name";
        }

        if (!FontMatrix.SequenceEqual(full.FontMatrix) || PaintType != full.PaintType)
        {
            return "its size or style differs";
        }

        if (LenIV != full.LenIV)
        {
            return "its glyphs are stored differently";
        }

        foreach (var key in RenderingKeys)
        {
            if (RenderingValues.GetValueOrDefault(key) != full.RenderingValues.GetValueOrDefault(key))
            {
                return $"its /{key} differs";
            }
        }

        if (UsesStandardEncoding != full.UsesStandardEncoding
            || Encoding.Any(e => e.Value != ".notdef" && full.Encoding.GetValueOrDefault(e.Key) != e.Value))
        {
            return "its encoding differs";
        }

        var budget = new CharStringAnalyzer.Budget(MaxSteps);
        foreach (var (name, program) in CharStrings)
        {
            if (!full.CharStrings.TryGetValue(name, out var other) || !other.AsSpan().SequenceEqual(program))
            {
                return $"its glyph “{name}” differs";
            }

            if (CharStringAnalyzer.Analyze(program, Subrs, budget) is not { } uses)
            {
                return $"PaperKnife couldn't follow its glyph “{name}”";
            }

            foreach (var index in uses.Subrs)
            {
                if (!full.Subrs.TryGetValue(index, out var subroutine) || !subroutine.AsSpan().SequenceEqual(Subrs[index]))
                {
                    return $"its subroutine {index} differs";
                }
            }

            foreach (var code in uses.Components)
            {
                if (Encodings.StandardName(code) is not { } component || !CharStrings.ContainsKey(component))
                {
                    return $"its glyph “{name}” is built from a glyph it doesn't have";
                }
            }
        }

        return null;
    }

    /// <summary>A font name without the six-letter subset tag ("ABCDEF+CMR10" is "CMR10").</summary>
    public static string BaseName(string name) =>
        name.Length > 7 && name[6] == '+' && !name.AsSpan(0, 6).ContainsAnyExceptInRange('A', 'Z') ? name[7..] : name;

    /// <summary>Type 1 decryption (section 7.1): eexec uses key 55665, charstrings 4330; <paramref name="skip"/> leading bytes are random.</summary>
    internal static byte[] Decrypt(ReadOnlySpan<byte> data, int key, int skip)
    {
        var plain = new byte[data.Length];
        var r = key;
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
        }

        return skip >= plain.Length ? [] : plain[skip..];
    }

    private static Type1Font? Parse(byte[] clear, byte[] encrypted, byte[] trailer)
    {
        if (clear.Length > MaxCleartext || clear.Length + encrypted.Length + trailer.Length > MaxProgram
            || ReadCleartext(clear) is not { } header)
        {
            return null;
        }

        var plain = Decrypt(IsHex(encrypted) ? FromHex(encrypted) : encrypted, EexecKey, 4);
        if (PostScriptTokenizer.Tokenize(plain, MaxTokens, "closefile") is not { } tokens)
        {
            return null;
        }

        // Each key once, and /lenIV before the glyphs it applies to, as renderers read them in order.
        int lenIVAt = -1, subrsAt = -1, charStringsAt = -1;
        var lenIV = 4;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Kind != PsTokenKind.Name)
            {
                continue;
            }

            var key = tokens[k].Text(plain);
            switch (key)
            {
                case "/lenIV":
                    if (lenIVAt >= 0 || k + 1 >= tokens.Count || SignedInteger(plain, tokens[k + 1]) is not { } iv || iv is < -1 or > 32)
                    {
                        return null;
                    }

                    lenIVAt = k;
                    lenIV = iv;
                    break;
                case "/Subrs":
                    if (subrsAt >= 0)
                    {
                        return null;
                    }

                    subrsAt = k;
                    break;
                case "/CharStrings":
                    if (charStringsAt >= 0)
                    {
                        return null;
                    }

                    charStringsAt = k;
                    break;
                default:
                    if (RenderingKeys.Contains(key[1..]))
                    {
                        if (values.ContainsKey(key[1..]))
                        {
                            return null;
                        }

                        values[key[1..]] = ReadValue(plain, tokens, k + 1);
                    }

                    break;
            }
        }

        if (charStringsAt < 0 || (lenIVAt >= 0 && (lenIVAt > charStringsAt || (subrsAt >= 0 && lenIVAt > subrsAt))))
        {
            return null;
        }

        var subrs = subrsAt < 0 ? [] : ReadSubrs(plain, tokens, subrsAt + 1, lenIV);
        var charStrings = ReadCharStrings(plain, tokens, charStringsAt + 1, lenIV);
        if (subrs is null || charStrings is null)
        {
            return null;
        }

        return new Type1Font
        {
            FontName = header.Name,
            FontMatrix = header.Matrix,
            PaintType = header.PaintType,
            LenIV = lenIV,
            Encoding = header.Encoding,
            UsesStandardEncoding = header.StandardEncoding,
            CharStrings = charStrings,
            Subrs = subrs,
            RenderingValues = values,
            Program = [.. clear, .. encrypted, .. trailer],
            Length1 = clear.Length,
            Length2 = encrypted.Length,
            Length3 = trailer.Length,
        };
    }

    private sealed record Cleartext(string Name, double[] Matrix, int PaintType, bool StandardEncoding, IReadOnlyDictionary<int, string> Encoding);

    private static Cleartext? ReadCleartext(byte[] clear)
    {
        if (clear.Length > MaxCleartext || PostScriptTokenizer.Tokenize(clear, MaxTokens, "eexec") is not { } tokens)
        {
            return null;
        }

        string? name = null;
        double[]? matrix = null;
        int? paintType = null;
        (bool Standard, Dictionary<int, string> Names)? encoding = null;
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Kind != PsTokenKind.Name)
            {
                continue;
            }

            switch (tokens[k].Text(clear))
            {
                case "/FontName":
                    if (name is not null || k + 1 >= tokens.Count || tokens[k + 1].Kind != PsTokenKind.Name)
                    {
                        return null;
                    }

                    name = tokens[k + 1].Text(clear)[1..];
                    break;
                case "/FontMatrix":
                    if (matrix is not null || ReadMatrix(clear, tokens, k + 1) is not { } m)
                    {
                        return null;
                    }

                    matrix = m;
                    break;
                case "/PaintType":
                    if (paintType is not null || k + 1 >= tokens.Count || tokens[k + 1].Integer(clear) is not { } paint)
                    {
                        return null;
                    }

                    paintType = paint;
                    break;
                case "/FontType":
                    if (k + 1 >= tokens.Count || tokens[k + 1].Integer(clear) != 1)
                    {
                        return null;
                    }

                    break;
                case "/Encoding":
                    if (encoding is not null || ReadBuiltinEncoding(clear, tokens, k + 1) is not { } read)
                    {
                        return null;
                    }

                    encoding = read;
                    break;
                case "/WeightVector" or "/BlendDesignPositions" or "/BlendDesignMap" or "/BlendAxisTypes" or "/Blend":
                    // Multiple master fonts render through instances PaperKnife doesn't model.
                    return null;
            }
        }

        return name is null || matrix is null || encoding is not { } e ? null : new Cleartext(name, matrix, paintType ?? 0, e.Standard, e.Names);
    }

    // [a b c d e f]
    private static double[]? ReadMatrix(byte[] data, List<PsToken> tokens, int k)
    {
        if (k + 7 >= tokens.Count || tokens[k].Kind != PsTokenKind.ArrayStart || tokens[k + 7].Kind != PsTokenKind.ArrayEnd)
        {
            return null;
        }

        var matrix = new double[6];
        for (var i = 0; i < 6; i++)
        {
            var token = tokens[k + 1 + i];
            if (token.Kind != PsTokenKind.Regular
                || !double.TryParse(token.Text(data), NumberStyles.Float, CultureInfo.InvariantCulture, out matrix[i]) || !double.IsFinite(matrix[i]))
            {
                return null;
            }
        }

        return matrix;
    }

    // "StandardEncoding def", or "256 array ... dup <code> /<name> put ... def" (with anything that
    // isn't a name in between, such as the loop that fills the array with .notdef).
    private static (bool Standard, Dictionary<int, string> Names)? ReadBuiltinEncoding(byte[] data, List<PsToken> tokens, int k)
    {
        var names = new Dictionary<int, string>();
        if (k < tokens.Count && tokens[k].Is(data, "StandardEncoding"))
        {
            return (true, names);
        }

        if (k + 1 >= tokens.Count || tokens[k].Integer(data) is null || !tokens[k + 1].Is(data, "array"))
        {
            return null;
        }

        for (var j = k + 2; j < tokens.Count; j++)
        {
            if (tokens[j].Is(data, "def"))
            {
                return (false, names);
            }

            if (tokens[j].Is(data, "dup") && j + 3 < tokens.Count && tokens[j + 1].Integer(data) is { } code && code < 256
                && tokens[j + 2].Kind == PsTokenKind.Name && tokens[j + 3].Is(data, "put"))
            {
                names[code] = tokens[j + 2].Text(data)[1..];
                j += 3;
            }
            else if (tokens[j].Kind == PsTokenKind.Name)
            {
                return null;
            }
        }

        return null;
    }

    // "count array", then "dup <index> <length> RD <binary> NP" for each.
    private static Dictionary<int, byte[]>? ReadSubrs(byte[] data, List<PsToken> tokens, int k, int lenIV)
    {
        if (k + 1 >= tokens.Count || tokens[k].Integer(data) is not { } count || count > MaxEntries || !tokens[k + 1].Is(data, "array"))
        {
            return null;
        }

        var subrs = new Dictionary<int, byte[]>();
        var j = k + 2;
        while (j < tokens.Count && tokens[j].Is(data, "dup"))
        {
            if (j + 5 >= tokens.Count || tokens[j + 1].Integer(data) is not { } index || index >= count
                || ReadProgram(data, tokens, j + 2, lenIV) is not { } program || !subrs.TryAdd(index, program))
            {
                return null;
            }

            j += 5;
            if (tokens[j].Is(data, "NP") || tokens[j].Is(data, "|"))
            {
                j++;
            }
            else if (j + 1 < tokens.Count && tokens[j].Is(data, "noaccess") && tokens[j + 1].Is(data, "put"))
            {
                j += 2;
            }
            else
            {
                return null;
            }
        }

        return subrs;
    }

    // "count dict dup begin", then "/<name> <length> RD <binary> ND" for each, then "end".
    private static Dictionary<string, byte[]>? ReadCharStrings(byte[] data, List<PsToken> tokens, int k, int lenIV)
    {
        if (k + 3 >= tokens.Count || tokens[k].Integer(data) is not { } count || count > MaxEntries
            || !tokens[k + 1].Is(data, "dict") || !tokens[k + 2].Is(data, "dup") || !tokens[k + 3].Is(data, "begin"))
        {
            return null;
        }

        var glyphs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var j = k + 4;
        while (j < tokens.Count && !tokens[j].Is(data, "end"))
        {
            if (tokens[j].Kind != PsTokenKind.Name || j + 4 >= tokens.Count
                || ReadProgram(data, tokens, j + 1, lenIV) is not { } program
                || !glyphs.TryAdd(tokens[j].Text(data)[1..], program) || glyphs.Count > count)
            {
                return null;
            }

            j += 4;
            if (tokens[j].Is(data, "ND") || tokens[j].Is(data, "|-"))
            {
                j++;
            }
            else if (j + 1 < tokens.Count && tokens[j].Is(data, "noaccess") && tokens[j + 1].Is(data, "def"))
            {
                j += 2;
            }
            else
            {
                return null;
            }
        }

        return j < tokens.Count && glyphs.Count > 0 ? glyphs : null;
    }

    // "<length> RD <binary>", decrypted.
    private static byte[]? ReadProgram(byte[] data, List<PsToken> tokens, int k, int lenIV)
    {
        if (tokens[k].Integer(data) is not { } length || !(tokens[k + 1].Is(data, "RD") || tokens[k + 1].Is(data, "-|"))
            || tokens[k + 2] is not { Kind: PsTokenKind.Binary } binary || binary.Length != length)
        {
            return null;
        }

        var raw = data.AsSpan(binary.Start, binary.Length);
        return lenIV < 0 ? raw.ToArray() : Decrypt(raw, CharStringKey, lenIV);
    }

    // The tokens of a value up to the operator that stores it, such as "[ -22 0 683 705 ]".
    private static string ReadValue(byte[] data, List<PsToken> tokens, int k)
    {
        var parts = new List<string>();
        for (var j = k; j < tokens.Count && parts.Count < 64; j++)
        {
            var token = tokens[j];
            if (token.Kind == PsTokenKind.Name
                || (token.Kind == PsTokenKind.Regular && token.Text(data) is "def" or "ND" or "|-" or "readonly" or "noaccess" or "executeonly" or "put"))
            {
                break;
            }

            parts.Add(token.Text(data));
        }

        return string.Join(' ', parts);
    }

    private static int? SignedInteger(byte[] data, PsToken token) =>
        token.Kind == PsTokenKind.Regular && token.Length <= 10
            && int.TryParse(token.Text(data), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool IsHex(byte[] data) =>
        data.Length >= 4 && data.AsSpan(0, 4).IndexOfAnyExcept("0123456789ABCDEFabcdef"u8) < 0;

    private static byte[] FromHex(byte[] data)
    {
        var digits = data.Where(b => Uri.IsHexDigit((char)b)).ToArray();
        return Convert.FromHexString(System.Text.Encoding.Latin1.GetString(digits, 0, digits.Length & ~1));
    }
}
