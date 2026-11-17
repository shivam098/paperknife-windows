using System.Runtime.InteropServices;
using System.Text;
using PaperKnife.Core.Pdfium;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core;

/// <summary>
/// A PDF loaded into PDFium. Public members only read; mutations are internal so that every change
/// goes through <see cref="DocumentSession"/>, which verifies it before committing.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private readonly PdfSnapshot? _source;
    private readonly Dictionary<int, PdfPage> _pages = [];
    private nint _handle;

    private PdfDocument(nint handle, PdfSnapshot? source)
    {
        _handle = handle;
        _source = source;
    }

    public int PageCount
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return FPDF_GetPageCount(Handle);
            }
        }
    }

    /// <summary>Number of digital signatures. Editing a signed PDF invalidates its signatures.</summary>
    public int SignatureCount
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return Math.Max(0, FPDF_GetSignatureCount(Handle));
            }
        }
    }

    /// <summary>Whether the author allows changing page content (always true for unprotected PDFs).</summary>
    public bool AllowsContentChanges => (Permissions & PermissionModifyContents) != 0;

    /// <summary>
    /// Whether the author allows inserting, rotating or deleting pages. The separate "assemble"
    /// permission only exists from security handler revision 3; before that, it needs "modify".
    /// </summary>
    public bool AllowsPageChanges =>
        AllowsContentChanges || (SecurityHandlerRevision >= 3 && (Permissions & PermissionAssemble) != 0);

    /// <summary>Whether the author allows copying this document's pages into another PDF.</summary>
    public bool AllowsCopyingPages => (Permissions & PermissionCopy) != 0 && AllowsPageChanges;

    internal bool IsDisposed => _handle == 0;

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    private uint Permissions
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return FPDF_GetDocPermissions(Handle);
            }
        }
    }

    private int SecurityHandlerRevision
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return FPDF_GetSecurityHandlerRevision(Handle);
            }
        }
    }

    public static PdfDocument Load(PdfSnapshot snapshot, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PdfiumLibrary.EnsureInitialized();
        lock (PdfiumLibrary.Sync)
        {
            var passwordBytes = password is null ? null : Encoding.UTF8.GetBytes(password + '\0');
            nint handle;
            fixed (byte* passwordPointer = passwordBytes)
            {
                handle = FPDF_LoadMemDocument64(snapshot.Pointer, (nuint)snapshot.Length, passwordPointer);
            }

            if (handle == 0)
            {
                throw PdfException.FromLastError();
            }

            return new PdfDocument(handle, snapshot);
        }
    }

    /// <summary>Returns the page at <paramref name="index"/>. Pages are cached and owned by the document.</summary>
    public PdfPage GetPage(int index)
    {
        lock (PdfiumLibrary.Sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FPDF_GetPageCount(Handle));
            if (_pages.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var handle = FPDF_LoadPage(_handle, index);
            if (handle == 0)
            {
                throw new PdfException(PdfErrorCode.PageError, $"Page {index + 1} couldn't be read.");
            }

            var page = new PdfPage(this, handle, index);
            _pages[index] = page;
            return page;
        }
    }

    /// <summary>Serializes the document, including pending page edits, to a new snapshot.</summary>
    public PdfSnapshot Save()
    {
        lock (PdfiumLibrary.Sync)
        {
            var handle = Handle;
            foreach (var page in _pages.Values)
            {
                page.CommitContent();
            }

            using var stream = new MemoryStream();
            var streamHandle = GCHandle.Alloc(stream);
            try
            {
                var writer = new FpdfFileWrite
                {
                    Version = 1,
                    WriteBlock = &WriteBlock,
                    State = GCHandle.ToIntPtr(streamHandle),
                };

                if (FPDF_SaveAsCopy(handle, &writer, FPDF_NO_INCREMENTAL) == 0 || stream.Length == 0)
                {
                    throw new PdfException(PdfErrorCode.OperationFailed, "The PDF couldn't be saved.");
                }
            }
            finally
            {
                streamHandle.Free();
            }

            return PdfSnapshot.FromStream(stream);
        }
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_handle == 0)
            {
                return;
            }

            CloseAllPages(commit: false);
            FPDF_CloseDocument(_handle);
            _handle = 0;
            GC.KeepAlive(_source);
        }
    }

    internal static PdfDocument CreateNew()
    {
        PdfiumLibrary.EnsureInitialized();
        lock (PdfiumLibrary.Sync)
        {
            var handle = FPDF_CreateNewDocument();
            if (handle == 0)
            {
                throw new PdfException(PdfErrorCode.OperationFailed, "A new PDF couldn't be created.");
            }

            return new PdfDocument(handle, null);
        }
    }

    internal void RotatePage(int index, int quarterTurns)
    {
        lock (PdfiumLibrary.Sync)
        {
            var page = GetPage(index);
            page.SetRotation((((page.Rotation + quarterTurns) % 4) + 4) % 4);
        }
    }

    internal void DeletePage(int index)
    {
        lock (PdfiumLibrary.Sync)
        {
            CloseAllPages(commit: true);
            FPDFPage_Delete(Handle, index);
        }
    }

    internal bool MovePage(int fromIndex, int toIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            CloseAllPages(commit: true);
            var index = fromIndex;
            return FPDF_MovePages(Handle, &index, 1, toIndex) != 0;
        }
    }

    internal bool ImportPages(PdfDocument source, int insertAt, IReadOnlyList<int>? pageIndices = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (PdfiumLibrary.Sync)
        {
            CloseAllPages(commit: true);
            var indices = pageIndices?.ToArray() ?? Enumerable.Range(0, source.PageCount).ToArray();
            if (indices.Length == 0)
            {
                return true;
            }

            fixed (int* indicesPointer = indices)
            {
                return FPDF_ImportPagesByIndex(Handle, source.Handle, indicesPointer, (uint)indices.Length, insertAt) != 0;
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(FpdfFileWrite* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->State).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception)
        {
            // Exceptions must not cross into native code; PDFium treats 0 as a failed write.
            return 0;
        }
    }

    private void CloseAllPages(bool commit)
    {
        foreach (var page in _pages.Values)
        {
            if (commit)
            {
                page.CommitContent();
            }

            page.Close();
        }

        _pages.Clear();
    }
}
