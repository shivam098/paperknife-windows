using System.Globalization;
using System.Text;

namespace PaperKnife.Core.Syntax;

internal enum PdfTokenKind
{
    End,
    Number,
    Name,
    String,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
}

/// <param name="Text">A number's spelling, a decoded name, or a keyword.</param>
/// <param name="Bytes">A string's decoded bytes.</param>
internal readonly record struct PdfToken(PdfTokenKind Kind, int Start, int End, string Text, double Number = 0, byte[]? Bytes = null)
{
    public bool IsKeyword(string keyword) => Kind == PdfTokenKind.Keyword && Text == keyword;
}

/// <summary>Splits PDF syntax into tokens (ISO 32000-1, 7.2). Used for files and content streams.</summary>
internal sealed class PdfLexer(ReadOnlyMemory<byte> data, int position = 0)
{
    public ReadOnlyMemory<byte> Data { get; } = data;

    public int Position { get; set; } = position;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public void SkipWhitespaceAndComments()
    {
        var span = Data.Span;
        while (Position < span.Length)
        {
            var b = span[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (Position < span.Length && span[Position] is not (byte)'\r' and not (byte)'\n')
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    public PdfToken Peek()
    {
        var position = Position;
        var token = Next();
        Position = position;
        return token;
    }

    public PdfToken Next()
    {
        SkipWhitespaceAndComments();
        var span = Data.Span;
        var start = Position;
        if (start >= span.Length)
        {
            return new PdfToken(PdfTokenKind.End, start, start, string.Empty);
        }

        var b = span[start];
        switch (b)
        {
            case (byte)'[':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayStart, start, Position, "[");
            case (byte)']':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayEnd, start, Position, "]");
            case (byte)'<' when start + 1 < span.Length && span[start + 1] == '<':
                Position += 2;
                return new PdfToken(PdfTokenKind.DictStart, start, Position, "<<");
            case (byte)'>' when start + 1 < span.Length && span[start + 1] == '>':
                Position += 2;
                return new PdfToken(PdfTokenKind.DictEnd, start, Position, ">>");
            case (byte)'<':
                return ReadHexString(span);
            case (byte)'(':
                return ReadLiteralString(span);
            case (byte)'/':
                return ReadName(span);
            case (byte)'{' or (byte)'}' or (byte)')' or (byte)'>':
                Position++;
                return new PdfToken(PdfTokenKind.Keyword, start, Position, ((char)b).ToString());
            default:
                return ReadWord(span);
        }
    }

    private PdfToken ReadWord(ReadOnlySpan<byte> span)
    {
        var start = Position;
        while (Position < span.Length && IsRegular(span[Position]))
        {
            Position++;
        }

        var text = Encoding.Latin1.GetString(span[start..Position]);
        return IsNumber(text) && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? new PdfToken(PdfTokenKind.Number, start, Position, text, value)
            : new PdfToken(PdfTokenKind.Keyword, start, Position, text);
    }

    private static bool IsNumber(string text)
    {
        var i = 0;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        var digits = 0;
        var dots = 0;
        for (; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                digits++;
            }
            else if (text[i] == '.' && dots == 0)
            {
                dots++;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    private PdfToken ReadName(ReadOnlySpan<byte> span)
    {
        var start = Position++;
        var name = new StringBuilder();
        while (Position < span.Length && IsRegular(span[Position]))
        {
            var b = span[Position];
            if (b == '#' && Position + 2 < span.Length && IsHexDigit(span[Position + 1]) && IsHexDigit(span[Position + 2]))
            {
                name.Append((char)((HexValue(span[Position + 1]) << 4) | HexValue(span[Position + 2])));
                Position += 3;
            }
            else
            {
                name.Append((char)b);
                Position++;
            }
        }

        return new PdfToken(PdfTokenKind.Name, start, Position, name.ToString());
    }

    private PdfToken ReadHexString(ReadOnlySpan<byte> span)
    {
        var start = Position++;
        var bytes = new List<byte>();
        var high = -1;
        while (Position < span.Length)
        {
            var b = span[Position++];
            if (b == '>')
            {
                break;
            }

            if (!IsHexDigit(b))
            {
                continue;
            }

            if (high < 0)
            {
                high = HexValue(b);
            }
            else
            {
                bytes.Add((byte)((high << 4) | HexValue(b)));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return new PdfToken(PdfTokenKind.String, start, Position, string.Empty, Bytes: [.. bytes]);
    }

    private PdfToken ReadLiteralString(ReadOnlySpan<byte> span)
    {
        // Like PDFium, line ends inside the string are kept as they are.
        var start = Position++;
        var bytes = new List<byte>();
        var depth = 1;
        while (Position < span.Length)
        {
            var b = span[Position++];
            if (b == '\\')
            {
                if (Position >= span.Length)
                {
                    break;
                }

                var e = span[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (Position < span.Length && span[Position] == '\n')
                        {
                            Position++;
                        }

                        break;
                    case (byte)'\n':
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                        var value = e - '0';
                        for (var n = 0; n < 2 && Position < span.Length && span[Position] is >= (byte)'0' and <= (byte)'7'; n++)
                        {
                            value = (value * 8) + (span[Position++] - '0');
                        }

                        bytes.Add((byte)value);
                        break;
                    default:
                        bytes.Add(e);
                        break;
                }
            }
            else if (b == '(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == ')')
            {
                if (--depth == 0)
                {
                    break;
                }

                bytes.Add(b);
            }
            else
            {
                bytes.Add(b);
            }
        }

        return new PdfToken(PdfTokenKind.String, start, Position, string.Empty, Bytes: [.. bytes]);
    }

    public static bool IsHexDigit(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => b - 'A' + 10,
    };
}

/// <summary>Reads objects from tokens: arrays, dictionaries and indirect references.</summary>
internal static class PdfObjectParser
{
    private const int MaxDepth = 100;

    /// <summary>The next object, or null at the end of the data or on syntax it doesn't understand.</summary>
    public static CosObject? Read(PdfLexer lexer, bool allowReferences = true) =>
        FromToken(lexer, lexer.Next(), allowReferences, 0);

    public static CosObject? FromToken(PdfLexer lexer, PdfToken token, bool allowReferences, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (token.Kind)
        {
            case PdfTokenKind.Number:
                var number = new CosNumber(token.Number, token.Text);
                if (allowReferences && number.IsInteger && number.Value >= 0 && TryReadReference(lexer, (int)number.Value) is { } reference)
                {
                    return reference;
                }

                return number;
            case PdfTokenKind.Name:
                return new CosName(token.Text);
            case PdfTokenKind.String:
                return new CosString(token.Bytes!);
            case PdfTokenKind.ArrayStart:
                var items = new List<CosObject>();
                while (true)
                {
                    var next = lexer.Next();
                    if (next.Kind == PdfTokenKind.ArrayEnd)
                    {
                        return new CosArray(items);
                    }

                    if (FromToken(lexer, next, allowReferences, depth + 1) is not { } item)
                    {
                        return null;
                    }

                    items.Add(item);
                }

            case PdfTokenKind.DictStart:
                var entries = new Dictionary<string, CosObject>(StringComparer.Ordinal);
                while (true)
                {
                    var key = lexer.Next();
                    if (key.Kind == PdfTokenKind.DictEnd)
                    {
                        return new CosDict(entries);
                    }

                    if (key.Kind != PdfTokenKind.Name || FromToken(lexer, lexer.Next(), allowReferences, depth + 1) is not { } value)
                    {
                        return null;
                    }

                    entries[key.Text] = value;
                }

            case PdfTokenKind.Keyword:
                return token.Text switch
                {
                    "true" => new CosBool(true),
                    "false" => new CosBool(false),
                    "null" => CosNull.Instance,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static CosRef? TryReadReference(PdfLexer lexer, int number)
    {
        var position = lexer.Position;
        var generation = lexer.Next();
        if (generation.Kind == PdfTokenKind.Number && generation.Number >= 0 && !generation.Text.Contains('.', StringComparison.Ordinal)
            && lexer.Next().IsKeyword("R"))
        {
            return new CosRef(number, (int)generation.Number);
        }

        lexer.Position = position;
        return null;
    }
}
