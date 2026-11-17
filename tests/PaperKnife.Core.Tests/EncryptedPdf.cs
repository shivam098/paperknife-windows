using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PaperKnife.Core.Tests;

/// <summary>
/// Single-page PDFs protected by the standard security handler (RC4, revision 2 or 3) with an
/// empty user password: they open without a password but carry the given permission flags.
/// Implements algorithms 1 to 5 of ISO 32000-1, section 7.6.3.
/// </summary>
internal static class EncryptedPdf
{
    private static readonly byte[] PasswordPadding =
        Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    private static readonly byte[] FileId = Convert.FromHexString("5061706572B46E69666554657374AA01");

    public static PdfSnapshot SinglePage(string content, int permissions, int revision)
    {
        var keyLength = revision >= 3 ? 16 : 5;
        var owner = OwnerValue("owner", revision, keyLength);
        var key = FileKey(owner, permissions, revision, keyLength);
        var user = UserValue(key, revision);
        var stream = Rc4(ObjectKey(key, objectNumber: 4), Encoding.Latin1.GetBytes(content));
        var encrypt = revision >= 3
            ? $"<< /Filter /Standard /V 2 /R {revision} /Length 128 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P {permissions} >>"
            : $"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P {permissions} >>";

        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => pdf.Write(Encoding.Latin1.GetBytes(text));
        void WriteObject(int number, string body)
        {
            offsets.Add(pdf.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        offsets.Add(pdf.Position);
        Write($"4 0 obj\n<< /Length {stream.Length} >>\nstream\n");
        pdf.Write(stream);
        Write("\nendstream\nendobj\n");
        WriteObject(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        WriteObject(6, encrypt);

        var xref = pdf.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        var id = Convert.ToHexString(FileId);
        Write($"trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfSnapshot.FromBytes(pdf.ToArray());
    }

#pragma warning disable CA5351 // The PDF standard security handler is defined in terms of MD5 and RC4.

    // Algorithm 3: the O entry for the given owner password and an empty user password.
    private static byte[] OwnerValue(string ownerPassword, int revision, int keyLength)
    {
        var hash = MD5.HashData(Pad(ownerPassword));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash);
            }
        }

        var rc4Key = hash[..keyLength];
        var value = Rc4(rc4Key, Pad(string.Empty));
        if (revision >= 3)
        {
            for (var i = 1; i <= 19; i++)
            {
                value = Rc4(Xor(rc4Key, i), value);
            }
        }

        return value;
    }

    // Algorithm 2: the file encryption key for the empty user password.
    private static byte[] FileKey(byte[] owner, int permissions, int revision, int keyLength)
    {
        var permissionBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(permissionBytes, permissions);
        var hash = MD5.HashData(Join(Pad(string.Empty), owner, permissionBytes, FileId));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, keyLength));
            }
        }

        return hash[..keyLength];
    }

    // Algorithms 4 (revision 2) and 5 (revision 3): the U entry.
    private static byte[] UserValue(byte[] key, int revision)
    {
        if (revision < 3)
        {
            return Rc4(key, PasswordPadding);
        }

        var value = Rc4(key, MD5.HashData(Join(PasswordPadding, FileId)));
        for (var i = 1; i <= 19; i++)
        {
            value = Rc4(Xor(key, i), value);
        }

        return Join(value, new byte[16]);
    }

    // Algorithm 1: the key for the strings and streams of one object (generation 0).
    private static byte[] ObjectKey(byte[] key, int objectNumber)
    {
        byte[] suffix = [(byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), 0, 0];
        return MD5.HashData(Join(key, suffix))[..Math.Min(key.Length + 5, 16)];
    }

#pragma warning restore CA5351

    private static byte[] Pad(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var length = Math.Min(bytes.Length, 32);
        var padded = new byte[32];
        bytes.AsSpan(0, length).CopyTo(padded);
        PasswordPadding.AsSpan(0, 32 - length).CopyTo(padded.AsSpan(length));
        return padded;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Xor(byte[] key, int value) => key.Select(b => (byte)(b ^ value)).ToArray();

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }
}
