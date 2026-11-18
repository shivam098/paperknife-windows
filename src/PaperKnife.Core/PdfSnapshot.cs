using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PaperKnife.Core;

/// <summary>
/// An immutable, fully serialized PDF held in memory. The bytes live on the pinned object heap, so
/// PDFium can read them in place for as long as a document loaded from the snapshot is open.
/// </summary>
public sealed class PdfSnapshot
{
    private readonly byte[] _data;

    private PdfSnapshot(byte[] data) => _data = data;

    public int Length => _data.Length;

    public ReadOnlySpan<byte> Span => _data;

    internal ReadOnlyMemory<byte> Memory => _data;

    internal unsafe byte* Pointer => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_data));

    public static PdfSnapshot FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var length = stream.Length;
        if (length == 0)
        {
            throw new PdfException(PdfErrorCode.InvalidFormat, "The file is empty.");
        }

        if (length > Array.MaxLength)
        {
            throw new PdfException(PdfErrorCode.OperationFailed, "The file is too large to open (over 2 GB).");
        }

        var data = GC.AllocateUninitializedArray<byte>((int)length, pinned: true);
        stream.ReadExactly(data);
        return new PdfSnapshot(data);
    }

    public static PdfSnapshot FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new PdfException(PdfErrorCode.InvalidFormat, "The file is empty.");
        }

        var data = GC.AllocateUninitializedArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(data);
        return new PdfSnapshot(data);
    }

    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_data);
    }

    public bool ContentEquals(PdfSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) || _data.AsSpan().SequenceEqual(other._data);
    }

    internal static PdfSnapshot FromStream(MemoryStream stream) => FromBytes(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
}
