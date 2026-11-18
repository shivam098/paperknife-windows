using System.Text;

namespace PaperKnife.Core.Fonts;

internal enum PsTokenKind
{
    /// <summary>A literal name such as /FontName.</summary>
    Name,

    /// <summary>A number, an operator or another regular token such as def or RD.</summary>
    Regular,

    ArrayStart,

    ArrayEnd,

    /// <summary>A whole procedure, { ... }, including nested procedures.</summary>
    Procedure,

    String,

    HexString,

    DictStart,

    DictEnd,

    /// <summary>The binary data after "n RD" or "n -|": exactly n bytes after one separator.</summary>
    Binary,
}

/// <summary>A token's kind and where it is in the data.</summary>
internal readonly record struct PsToken(PsTokenKind Kind, int Start, int Length)
{
    public string Text(byte[] data) => Encoding.Latin1.GetString(data, Start, Length);

    public bool Is(byte[] data, string text) =>
        Kind is PsTokenKind.Regular or PsTokenKind.Name && Length == text.Length && Text(data) == text;

    /// <summary>The token's value when it is a whole number written with digits only, as Type 1 lengths and indexes are.</summary>
    public int? Integer(byte[] data)
    {
        if (Kind != PsTokenKind.Regular || Length is < 1 or > 9)
        {
            return null;
        }

        var value = 0;
        for (var i = Start; i < Start + Length; i++)
        {
            if (data[i] is < (byte)'0' or > (byte)'9')
            {
                return null;
            }

            value = (value * 10) + (data[i] - '0');
        }

        return value;
    }
}

/// <summary>
/// Splits PostScript, the language of Type 1 font programs, into tokens in a single pass, the way
/// FreeType's Type 1 parser reads it: comments are skipped, and a procedure, a string or the binary
/// data of a glyph or subroutine is one token.
/// </summary>
internal static class PostScriptTokenizer
{
    /// <summary>
    /// The tokens, or null when the data is malformed or has more than <paramref name="maxTokens"/>
    /// tokens. Reading stops at the operator <paramref name="stopAt"/> (for example closefile, after
    /// which a font program may hold anything).
    /// </summary>
    public static List<PsToken>? Tokenize(byte[] data, int maxTokens, string? stopAt = null)
    {
        var tokens = new List<PsToken>();
        var i = 0;
        while (true)
        {
            SkipSpaceAndComments(data, ref i);
            if (i >= data.Length)
            {
                return tokens;
            }

            if (tokens.Count >= maxTokens)
            {
                return null;
            }

            var start = i;
            switch (data[i])
            {
                case (byte)'/':
                    i++;
                    if (i < data.Length && data[i] == '/')
                    {
                        i++;
                    }

                    while (i < data.Length && IsRegular(data[i]))
                    {
                        i++;
                    }

                    tokens.Add(new PsToken(PsTokenKind.Name, start, i - start));
                    break;
                case (byte)'[':
                    tokens.Add(new PsToken(PsTokenKind.ArrayStart, i++, 1));
                    break;
                case (byte)']':
                    tokens.Add(new PsToken(PsTokenKind.ArrayEnd, i++, 1));
                    break;
                case (byte)'{':
                    if (!SkipProcedure(data, ref i))
                    {
                        return null;
                    }

                    tokens.Add(new PsToken(PsTokenKind.Procedure, start, i - start));
                    break;
                case (byte)'(':
                    if (!SkipString(data, ref i))
                    {
                        return null;
                    }

                    tokens.Add(new PsToken(PsTokenKind.String, start, i - start));
                    break;
                case (byte)'<' when i + 1 < data.Length && data[i + 1] == '<':
                    tokens.Add(new PsToken(PsTokenKind.DictStart, i, 2));
                    i += 2;
                    break;
                case (byte)'<':
                    if (!SkipHexString(data, ref i))
                    {
                        return null;
                    }

                    tokens.Add(new PsToken(PsTokenKind.HexString, start, i - start));
                    break;
                case (byte)'>' when i + 1 < data.Length && data[i + 1] == '>':
                    tokens.Add(new PsToken(PsTokenKind.DictEnd, i, 2));
                    i += 2;
                    break;
                case (byte)'>' or (byte)')' or (byte)'}':
                    return null;
                default:
                    while (i < data.Length && IsRegular(data[i]))
                    {
                        i++;
                    }

                    var token = new PsToken(PsTokenKind.Regular, start, i - start);
                    if (stopAt is not null && token.Is(data, stopAt))
                    {
                        return tokens;
                    }

                    tokens.Add(token);

                    // "n RD" (or "n -|") is followed by one separator and n bytes of binary data.
                    if ((token.Is(data, "RD") || token.Is(data, "-|")) && tokens.Count >= 2 && tokens[^2].Integer(data) is { } length)
                    {
                        if (length > data.Length - i - 1)
                        {
                            return null;
                        }

                        tokens.Add(new PsToken(PsTokenKind.Binary, i + 1, length));
                        i += 1 + length;
                    }

                    break;
            }
        }
    }

    public static bool IsWhite(byte b) => b is 0 or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ';

    private static bool IsRegular(byte b) =>
        !IsWhite(b) && b is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    private static void SkipSpaceAndComments(byte[] data, ref int i)
    {
        while (i < data.Length)
        {
            if (IsWhite(data[i]))
            {
                i++;
            }
            else if (data[i] == '%')
            {
                SkipComment(data, ref i);
            }
            else
            {
                return;
            }
        }
    }

    private static void SkipComment(byte[] data, ref int i)
    {
        while (i < data.Length && data[i] is not ((byte)'\r' or (byte)'\n'))
        {
            i++;
        }
    }

    private static bool SkipProcedure(byte[] data, ref int i)
    {
        var depth = 0;
        while (i < data.Length)
        {
            switch (data[i])
            {
                case (byte)'{':
                    depth++;
                    i++;
                    break;
                case (byte)'}':
                    i++;
                    if (--depth == 0)
                    {
                        return true;
                    }

                    break;
                case (byte)'(':
                    if (!SkipString(data, ref i))
                    {
                        return false;
                    }

                    break;
                case (byte)'<' when i + 1 < data.Length && data[i + 1] == '<':
                    i += 2;
                    break;
                case (byte)'<':
                    if (!SkipHexString(data, ref i))
                    {
                        return false;
                    }

                    break;
                case (byte)'%':
                    SkipComment(data, ref i);
                    break;
                default:
                    i++;
                    break;
            }
        }

        return false;
    }

    private static bool SkipString(byte[] data, ref int i)
    {
        var depth = 0;
        while (i < data.Length)
        {
            var b = data[i++];
            if (b == '\\')
            {
                i++;
            }
            else if (b == '(')
            {
                depth++;
            }
            else if (b == ')' && --depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    // <hex digits>, or the ASCII85 form <~ ... ~>.
    private static bool SkipHexString(byte[] data, ref int i)
    {
        i++;
        if (i < data.Length && data[i] == '~')
        {
            var end = data.AsSpan(i).IndexOf("~>"u8);
            if (end < 0)
            {
                return false;
            }

            i += end + 2;
            return true;
        }

        while (i < data.Length)
        {
            var b = data[i++];
            if (b == '>')
            {
                return true;
            }

            if (!IsWhite(b) && !Uri.IsHexDigit((char)b))
            {
                return false;
            }
        }

        return false;
    }
}
