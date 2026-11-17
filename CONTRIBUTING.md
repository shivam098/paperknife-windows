# Contributing to PaperKnife for Windows

Thank you for helping. PaperKnife's value comes from trust, so a few rules apply to every change.

## Ground rules

1. **Nothing leaves the device.** Don't add network calls, telemetry, crash uploaders, update
   checks or dependencies that make them.
2. **Edits are verified.** Every operation that changes a document goes through
   `DocumentSession`'s apply, save, reload and verify pipeline. Never weaken a check to make an
   edit pass; reject the edit and explain why instead.
3. **No faked edits.** Don't cover text with shapes, swap fonts silently or call covering
   "redaction".
4. **No documents we don't own.** Tests generate their PDFs. Don't commit third-party PDFs,
   fonts or users' files.
5. **Keep the engine UI-free.** `PaperKnife.Core` must not reference WinUI.

## Getting started

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet test --project tests/PaperKnife.Core.Tests/PaperKnife.Core.Tests.csproj
dotnet run --project src/PaperKnife.App
```

Warnings are treated as errors.

## Pull requests

- Describe the user-visible change and how you tested it.
- Add tests for new behavior, including the cases that must be refused.
- If you change what PaperKnife can or can't edit, update [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
  and the README.
- If you add a dependency, add it to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and make
  sure its license is compatible with Apache-2.0.

By contributing, you agree that your contributions are licensed under the Apache License 2.0.
