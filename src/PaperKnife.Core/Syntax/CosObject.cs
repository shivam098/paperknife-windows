using System.Globalization;
using System.Text;

namespace PaperKnife.Core.Syntax;

/// <summary>A PDF object (ISO 32000-1, 7.3), as read from a file or a content stream.</summary>
internal abstract class CosObject;

internal sealed class CosNull : CosObject
{
    public static CosNull Instance { get; } = new();
}

internal sealed class CosBool(bool value) : CosObject
{
    public bool Value { get; } = value;
}

/// <summary>A number, with its original spelling so that it can be written back unchanged.</summary>
internal sealed class CosNumber(double value, string raw) : CosObject
{
    public double Value { get; } = value;

    public string Raw { get; } = raw;

    public bool IsInteger => !Raw.Contains('.', StringComparison.Ordinal) && Math.Abs(Value) <= int.MaxValue;
}

/// <summary>A name without its slash. Escapes are decoded; each byte maps to one char.</summary>
internal sealed class CosName(string value) : CosObject
{
    public string Value { get; } = value;
}

internal sealed class CosString(byte[] bytes) : CosObject
{
    public byte[] Bytes { get; } = bytes;
}

internal sealed class CosArray(List<CosObject> items) : CosObject
{
    public List<CosObject> Items { get; } = items;

    public int Count => Items.Count;

    public CosObject this[int index] => Items[index];
}

internal sealed class CosDict(Dictionary<string, CosObject> entries) : CosObject
{
    public Dictionary<string, CosObject> Entries { get; } = entries;

    public CosObject? this[string key] => Entries.GetValueOrDefault(key);
}

internal sealed class CosRef(int number, int generation) : CosObject
{
    public int Number { get; } = number;

    public int Generation { get; } = generation;
}

/// <summary>A stream: its dictionary and where its encoded data lies in the file.</summary>
internal sealed class CosStream(CosDict dict, int dataStart, int dataLength) : CosObject
{
    public CosDict Dict { get; } = dict;

    public int DataStart { get; } = dataStart;

    public int DataLength { get; } = dataLength;
}

/// <summary>Writes objects in PDF syntax, as Latin-1 text. Strings are written in hexadecimal.</summary>
internal static class CosWriter
{
    public static string ToText(CosObject value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    public static void Write(StringBuilder builder, CosObject value)
    {
        switch (value)
        {
            case CosNull:
                builder.Append("null");
                break;
            case CosBool b:
                builder.Append(b.Value ? "true" : "false");
                break;
            case CosNumber n:
                builder.Append(n.Raw);
                break;
            case CosName name:
                WriteName(builder, name.Value);
                break;
            case CosString s:
                WriteHex(builder, s.Bytes);
                break;
            case CosRef r:
                builder.Append(CultureInfo.InvariantCulture, $"{r.Number} {r.Generation} R");
                break;
            case CosArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(' ');
                    }

                    Write(builder, array[i]);
                }

                builder.Append(']');
                break;
            case CosDict dict:
                builder.Append("<<");
                foreach (var (key, entry) in dict.Entries)
                {
                    WriteName(builder, key);
                    builder.Append(' ');
                    Write(builder, entry);
                }

                builder.Append(">>");
                break;
            default:
                throw new ArgumentException("Streams can't be written inline.", nameof(value));
        }
    }

    public static void WriteName(StringBuilder builder, string name)
    {
        builder.Append('/');
        foreach (var c in name)
        {
            var b = (byte)c;
            if (b is < 0x21 or > 0x7E or (byte)'#' || PdfLexer.IsDelimiter(b))
            {
                builder.Append(CultureInfo.InvariantCulture, $"#{b:X2}");
            }
            else
            {
                builder.Append(c);
            }
        }
    }

    public static void WriteHex(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        builder.Append('<').Append(Convert.ToHexString(bytes)).Append('>');
    }

    /// <summary>A number in plain decimal notation, with at most ten decimals.</summary>
    public static string Number(double value)
    {
        var text = Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}
