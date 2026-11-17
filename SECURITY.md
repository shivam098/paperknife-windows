# Security policy

PaperKnife opens files from untrusted sources, so security reports are very welcome.

## Reporting a vulnerability

Please **don't open a public issue.** Use GitHub's private vulnerability reporting instead:
open the repository's **Security** tab and select **Report a vulnerability**.

Useful reports include:

- a PDF that crashes PaperKnife, hangs it or makes it use unbounded memory;
- any way a document could read or write files the user didn't choose, or send data off the
  device;
- an edit that changes content other than what the user edited without being rejected.

If a sample file is needed, share a minimal one that contains no personal information.

## Current limitations

The PDF engine (PDFium) runs inside the app process, without the sandbox it has in Chrome.
Running it in a separate low-privilege process is on the [roadmap](docs/ROADMAP.md).
