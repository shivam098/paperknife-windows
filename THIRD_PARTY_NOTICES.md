# Third-party notices

PaperKnife for Windows is licensed under the [Apache License 2.0](LICENSE). It ships with, or is
built from, the third-party software below. Their license and notice texts are in
[`third_party/`](third_party/) or linked below, and the build copies those in `third_party/`
next to the app, into `licenses\<component>\`.

## Shipped with the app

| Component | Version | License |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) | chromium/8152 | BSD-3-Clause, with Foxit-contributed code under Apache-2.0 ([text](third_party/pdfium/licenses/pdfium.txt)) |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (prebuilt PDFium, NuGet `bblanchon.PDFium.Win32`) | 159.0.8152 | Build scripts MIT; package Apache-2.0 |
| [.NET runtime](https://github.com/dotnet/runtime) (included because the app is self-contained) | 10.0 | MIT ([text](third_party/dotnet/licenses/LICENSE.TXT), [notices](third_party/dotnet/licenses/THIRD-PARTY-NOTICES.TXT)) |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK): WinUI 3 with its Foundation and InteractiveExperiences components (NuGet `Microsoft.WindowsAppSDK.WinUI`, self-contained) | 2.6.5 | Microsoft Software License Terms for Windows App SDK ([text](third_party/windowsappsdk/licenses/license.txt), [notices](third_party/windowsappsdk/licenses/NOTICE.txt)); files the NuGet packages place with the app are distributable (section 3) |
| [WebView2 SDK](https://aka.ms/webview2) (NuGet `Microsoft.Web.WebView2`, a WinUI dependency) | 1.0.3719.77 | BSD-3-Clause ([text](third_party/webview2/licenses/LICENSE.txt), [notices](third_party/webview2/licenses/NOTICE.txt)) |
| [C#/WinRT](https://github.com/microsoft/CsWinRT) runtime (`WinRT.Runtime.dll`) | 2.2.0 | MIT ([text](third_party/cswinrt/licenses/LICENSE.txt)) |
| Windows SDK projection for .NET (`Microsoft.Windows.SDK.NET.dll`, from the .NET SDK) | 10.0.19041.55 | [Microsoft Software License Terms for the Windows SDK](https://aka.ms/WinSDKLicenseURL) |

### Libraries compiled into PDFium

| Library | License | Text |
|---|---|---|
| Abseil | Apache-2.0 | [abseil.txt](third_party/pdfium/licenses/abseil.txt) |
| Anti-Grain Geometry 2.3 | AGG 2.3 license | [agg23.txt](third_party/pdfium/licenses/agg23.txt) |
| dragonbox | Apache-2.0 with LLVM exception, or Boost-1.0 | [Apache](third_party/pdfium/licenses/dragonbox-Apache2-LLVM.txt), [Boost](third_party/pdfium/licenses/dragonbox-Boost.txt) |
| fast_float | MIT | [fast_float.txt](third_party/pdfium/licenses/fast_float.txt) |
| FreeType | FreeType License (FTL) | [freetype.txt](third_party/pdfium/licenses/freetype.txt) |
| HarfBuzz | "Old MIT" | [harfbuzz.txt](third_party/pdfium/licenses/harfbuzz.txt) |
| Highway | Apache-2.0 | [highway.txt](third_party/pdfium/licenses/highway.txt) |
| ICU | Unicode License v3 | [icu.txt](third_party/pdfium/licenses/icu.txt) |
| Little CMS | MIT | [lcms.txt](third_party/pdfium/licenses/lcms.txt) |
| libjpeg-turbo | IJG and BSD-3-Clause | [libjpeg_turbo.md](third_party/pdfium/licenses/libjpeg_turbo.md), [libjpeg_turbo.ijg](third_party/pdfium/licenses/libjpeg_turbo.ijg) |
| OpenJPEG | BSD-2-Clause | [libopenjpeg.txt](third_party/pdfium/licenses/libopenjpeg.txt) |
| libpng | PNG Reference Library License v2 | [libpng.txt](third_party/pdfium/licenses/libpng.txt) |
| LLVM libc | Apache-2.0 with LLVM exception | [llvm-libc.txt](third_party/pdfium/licenses/llvm-libc.txt) |
| simdutf | MIT | [simdutf.txt](third_party/pdfium/licenses/simdutf.txt) |
| zlib | zlib License | [zlib.txt](third_party/pdfium/licenses/zlib.txt) |

Portions of this software are copyright © The FreeType Project (https://freetype.org). All rights reserved.

This software is based in part on the work of the Independent JPEG Group.

## In the installer

The installer (`PaperKnife-<version>-setup.exe`) is built with [Inno Setup](https://jrsoftware.org/isinfo.php)
6.7.3 (NuGet `Tools.InnoSetup`). It contains Inno Setup's setup program and installs Inno Setup's
uninstaller (`unins000.exe`) next to the app: Copyright (C) 1997-2026 Jordan Russell, portions
Copyright (C) 2000-2026 Martijn Laan, under the
[Inno Setup License](https://jrsoftware.org/files/is/license.txt). PaperKnife itself and the
portable ZIPs contain no Inno Setup code.

## Used only to build and test

| Component | License |
|---|---|
| [.NET SDK](https://github.com/dotnet/sdk) | MIT |
| [Microsoft.Windows.SDK.BuildTools](https://aka.ms/WinSDKLicenseURL) and Microsoft.Windows.SDK.BuildTools.MSIX | Microsoft Software License Terms |
| [xUnit.net v3](https://github.com/xunit/xunit) | Apache-2.0 |

Tests generate their PDFs at run time and may load fonts installed on the machine (for example
Arial); no font data or third-party documents are stored in this repository.
