using PaperKnife.Core.Pdfium;

namespace PaperKnife.Core;

public enum PdfErrorCode
{
    Unknown,
    InvalidFormat,
    PasswordRequired,
    UnsupportedSecurity,
    PageError,
    OperationFailed,
    Restricted,
}

/// <summary>A PDF couldn't be opened, read or written. <see cref="Exception.Message"/> is user-facing.</summary>
public sealed class PdfException : Exception
{
    public PdfException(PdfErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public PdfErrorCode ErrorCode { get; }

    /// <summary>Must be called while holding <see cref="PdfiumLibrary.Sync"/>, right after the failing call.</summary>
    internal static PdfException FromLastError() => PdfiumNative.FPDF_GetLastError() switch
    {
        PdfiumNative.FPDF_ERR_FILE => new(PdfErrorCode.InvalidFormat, "The file couldn't be read."),
        PdfiumNative.FPDF_ERR_FORMAT => new(PdfErrorCode.InvalidFormat, "This file isn't a PDF, or it is damaged."),
        PdfiumNative.FPDF_ERR_PASSWORD => new(PdfErrorCode.PasswordRequired, "This PDF is protected. Enter its password to open it."),
        PdfiumNative.FPDF_ERR_SECURITY => new(PdfErrorCode.UnsupportedSecurity, "This PDF uses a security scheme PaperKnife doesn't support."),
        PdfiumNative.FPDF_ERR_PAGE => new(PdfErrorCode.PageError, "A page in this PDF couldn't be read."),
        _ => new(PdfErrorCode.Unknown, "PDFium couldn't process this file."),
    };
}
