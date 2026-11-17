namespace PaperKnife.Core.Pdfium;

internal static class PdfiumLibrary
{
    /// <summary>
    /// PDFium keeps global state and is not thread-safe, so every native call in the process is
    /// serialized on this (reentrant) lock.
    /// </summary>
    internal static readonly object Sync = new();

    private static bool s_initialized;

    internal static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (s_initialized)
            {
                return;
            }

            PdfiumNative.FPDF_InitLibrary();
            s_initialized = true;
        }
    }
}
