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

## Releasing

1. Set the new version in `Directory.Build.props` and write the release notes as
   `docs/releases/v<version>.md`.
2. Once that is merged, tag the commit and push the tag, for example
   `git tag v0.2.0` and `git push origin v0.2.0`.
3. The [Release workflow](.github/workflows/release.yml) runs the tests, builds the installer,
   the portable ZIPs and `SHA256SUMS.txt`, and publishes them as a GitHub release. Tags with a
   suffix, such as `v0.2.0-beta.1`, become pre-releases.

To test packaging without releasing, run the Release workflow from the Actions tab, or build the
same files locally with `pwsh build/package.ps1 -Version 0.2.0` (they're written to
`artifacts/release/`).

Never change the installer's `AppId` in `installer/PaperKnife.iss`: Windows uses it to find the
installed app for upgrades and uninstalling. When a shipped dependency changes, update its
license texts in `third_party/` and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

By contributing, you agree that your contributions are licensed under the Apache License 2.0.
