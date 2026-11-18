using System.IO.Compression;

namespace PaperKnife.Core.Syntax;

/// <summary>Decodes stream data (ISO 32000-1, 7.4) and encodes the streams PaperKnife writes.</summary>
internal static class StreamFilters
{
    /// <summary>The most a single stream may decode to; larger streams are left to PDFium.</summary>
    public const int MaxDecodedLength = 64 * 1024 * 1024;

    /// <summary>The decoded data, or null when a filter isn't supported or the data is damaged.</summary>
    public static byte[]? Decode(ReadOnlySpan<byte> data, IReadOnlyList<string> filters, IReadOnlyList<CosDict?> parameters, out string? problem)
    {
        var current = data.ToArray();
        for (var i = 0; i < filters.Count; i++)
        {
            var p = i < parameters.Count ? parameters[i] : null;
            var next = filters[i] switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(current), p),
                "LZWDecode" or "LZW" => Predict(Lzw(current, Integer(p, "EarlyChange", 1)), p),
                "ASCIIHexDecode" or "AHx" => AsciiHex(current),
                "ASCII85Decode" or "A85" => Ascii85(current),
                "RunLengthDecode" or "RL" => RunLength(current),
                _ => null,
            };
            if (next is null)
            {
                problem = $"The stream's {filters[i]} data can't be decoded.";
                return null;
            }

            current = next;
        }

        problem = null;
        return current;
    }

    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    private static int Integer(CosDict? parameters, string key, int fallback) =>
        parameters?[key] is CosNumber n && n.IsInteger ? (int)n.Value : fallback;

    private static byte[]? Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxDecodedLength)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[]? Predict(byte[]? data, CosDict? parameters)
    {
        var predictor = Integer(parameters, "Predictor", 1);
        if (data is null || predictor == 1)
        {
            return data;
        }

        var colors = Integer(parameters, "Colors", 1);
        var bits = Integer(parameters, "BitsPerComponent", 8);
        var columns = Integer(parameters, "Columns", 1);
        if (colors is < 1 or > 32 || bits is not (1 or 2 or 4 or 8 or 16) || columns is < 1 or > 1_000_000)
        {
            return null;
        }

        var bytesPerPixel = Math.Max(1, ((colors * bits) + 7) / 8);
        var rowLength = ((colors * bits * columns) + 7) / 8;
        if (rowLength > data.Length)
        {
            return data.Length == 0 ? data : null;
        }

        if (predictor == 2)
        {
            if (bits != 8)
            {
                return null;
            }

            var result = (byte[])data.Clone();
            for (var row = 0; row + rowLength <= result.Length; row += rowLength)
            {
                for (var i = row + bytesPerPixel; i < row + rowLength; i++)
                {
                    result[i] += result[i - bytesPerPixel];
                }
            }

            return result;
        }

        if (predictor < 10)
        {
            return null;
        }

        var rows = data.Length / (rowLength + 1);
        var output = new byte[rows * rowLength];
        var prior = new byte[rowLength];
        for (var r = 0; r < rows; r++)
        {
            var type = data[r * (rowLength + 1)];
            if (type > 4)
            {
                return null;
            }

            var source = data.AsSpan((r * (rowLength + 1)) + 1, rowLength);
            var target = output.AsSpan(r * rowLength, rowLength);
            for (var i = 0; i < rowLength; i++)
            {
                int left = i >= bytesPerPixel ? target[i - bytesPerPixel] : 0;
                int up = prior[i];
                int upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                target[i] = (byte)(source[i] + type switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                });
            }

            target.CopyTo(prior);
        }

        return output;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
    }

    private static byte[]? AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var b in data)
        {
            if (b == '>')
            {
                break;
            }

            if (PdfLexer.IsWhitespace(b))
            {
                continue;
            }

            if (!PdfLexer.IsHexDigit(b))
            {
                return null;
            }

            if (high < 0)
            {
                high = PdfLexer.HexValue(b);
            }
            else
            {
                output.Add((byte)((high << 4) | PdfLexer.HexValue(b)));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return [.. output];
    }

    private static byte[]? Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        ulong value = 0;
        var count = 0;
        foreach (var b in data)
        {
            if (PdfLexer.IsWhitespace(b))
            {
                continue;
            }

            if (b == '~')
            {
                break;
            }

            if (b == 'z' && count == 0)
            {
                if (output.Count + 4 > MaxDecodedLength)
                {
                    return null;
                }

                output.AddRange([0, 0, 0, 0]);
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
            {
                return null;
            }

            value = (value * 85) + (ulong)(b - '!');
            if (++count == 5)
            {
                if (value > uint.MaxValue)
                {
                    return null;
                }

                output.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
                value = 0;
                count = 0;
            }
        }

        if (count == 1)
        {
            return null;
        }

        if (count > 1)
        {
            for (var i = count; i < 5; i++)
            {
                value = (value * 85) + 84;
            }

            for (var i = 0; i < count - 1; i++)
            {
                output.Add((byte)(value >> (24 - (8 * i))));
            }
        }

        return [.. output];
    }

    private static byte[]? RunLength(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var n = data[i++];
            if (n == 128)
            {
                break;
            }

            if (output.Count + 257 > MaxDecodedLength)
            {
                return null;
            }

            if (n < 128)
            {
                if (i + n + 1 > data.Length)
                {
                    return null;
                }

                output.AddRange(data.AsSpan(i, n + 1));
                i += n + 1;
            }
            else
            {
                if (i >= data.Length)
                {
                    return null;
                }

                output.AddRange(Enumerable.Repeat(data[i++], 257 - n));
            }
        }

        return [.. output];
    }

    private static byte[]? Lzw(byte[] data, int earlyChange)
    {
        var output = new MemoryStream();
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
            {
                table.Add([(byte)i]);
            }

            table.Add([]);
            table.Add([]);
        }

        Reset();
        var width = 9;
        var buffer = 0;
        var bits = 0;
        var position = 0;
        byte[]? previous = null;
        while (true)
        {
            while (bits < width)
            {
                if (position >= data.Length)
                {
                    return output.ToArray();
                }

                buffer = ((buffer << 8) | data[position++]) & 0xFFFFFF;
                bits += 8;
            }

            var code = (buffer >> (bits - width)) & ((1 << width) - 1);
            bits -= width;
            if (code == 256)
            {
                Reset();
                width = 9;
                previous = null;
                continue;
            }

            if (code == 257)
            {
                return output.ToArray();
            }

            byte[] entry;
            if (code < table.Count && code > 257)
            {
                entry = table[code];
            }
            else if (code < 256)
            {
                entry = table[code];
            }
            else if (code == table.Count && previous is not null)
            {
                entry = [.. previous, previous[0]];
            }
            else
            {
                return null;
            }

            output.Write(entry);
            if (output.Length > MaxDecodedLength)
            {
                return null;
            }

            if (previous is not null && table.Count < 4096)
            {
                table.Add([.. previous, entry[0]]);
            }

            previous = entry;
            if (table.Count + earlyChange >= (1 << width) && width < 12)
            {
                width++;
            }
        }
    }
}
