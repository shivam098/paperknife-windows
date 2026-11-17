using PaperKnife.Core.Pdfium;

namespace PaperKnife.Core;

/// <summary>Whole-document tools that create new PDFs and never modify their inputs.</summary>
public static class PdfTools
{
    /// <summary>Combines documents, in order, into a new PDF.</summary>
    public static PdfSnapshot Merge(IReadOnlyList<PdfSnapshot> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("Choose at least one PDF to merge.", nameof(documents));
        }

        lock (PdfiumLibrary.Sync)
        {
            using var output = PdfDocument.CreateNew();
            foreach (var snapshot in documents)
            {
                using var source = PdfDocument.Load(snapshot);
                ThrowIfCopyingRestricted(source);
                if (!output.ImportPages(source, output.PageCount))
                {
                    throw new PdfException(PdfErrorCode.OperationFailed, "A document couldn't be merged.");
                }
            }

            return output.Save();
        }
    }

    /// <summary>Copies the given pages, in the given order, into a new PDF.</summary>
    public static PdfSnapshot ExtractPages(PdfSnapshot document, IReadOnlyList<int> pageIndices, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Count == 0)
        {
            throw new ArgumentException("Choose at least one page.", nameof(pageIndices));
        }

        lock (PdfiumLibrary.Sync)
        {
            using var source = PdfDocument.Load(document, password);
            ThrowIfCopyingRestricted(source);
            var pageCount = source.PageCount;
            foreach (var index in pageIndices)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(pageIndices));
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, pageCount, nameof(pageIndices));
            }

            using var output = PdfDocument.CreateNew();
            if (!output.ImportPages(source, 0, pageIndices))
            {
                throw new PdfException(PdfErrorCode.OperationFailed, "The pages couldn't be extracted.");
            }

            return output.Save();
        }
    }

    // Copying pages into a new, unprotected PDF would strip the author's restrictions.
    private static void ThrowIfCopyingRestricted(PdfDocument source)
    {
        if (!source.AllowsCopyingPages)
        {
            throw new PdfException(PdfErrorCode.Restricted, "The author of this PDF restricted copying its pages.");
        }
    }
}
