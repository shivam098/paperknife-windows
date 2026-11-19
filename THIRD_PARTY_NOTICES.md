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
| [AMS Type 1 Computer Modern fonts](https://ctan.org/pkg/amsfonts) (`cm*.pfb`, compiled into `PaperKnife.Core.dll`, unmodified; see [`third_party/amsfonts`](third_party/amsfonts/README.md)) | amsfonts 3.04 | SIL Open Font License 1.1 ([text](third_party/amsfonts/licenses/OFL.txt), [FAQ](third_party/amsfonts/licenses/OFL-FAQ.txt)); Copyright (c) 1997, 2009, American Mathematical Society |
| [Latin Modern Type 1 fonts](https://ctan.org/pkg/lm) (`lm*.pfb`, separate files in `Fonts\lm`, unmodified; see [`third_party/lm`](third_party/lm/README.md)) | 2.004 (lm 2.005) | GUST Font License 1.0, which is the LaTeX Project Public License 1.3c with a request to rename modified versions ([text](third_party/lm/licenses/GUST-FONT-LICENSE.TXT), [README](third_party/lm/licenses/README-Latin-Modern.TXT), [file list](third_party/lm/licenses/MANIFEST-Latin-Modern.TXT)); Copyright 2003–2009 B. Jackowski and J. M. Nowacki |
| [CM-Super Type 1 fonts](https://ctan.org/pkg/cm-super) (`sf*.pfb`, separate files in `Fonts\cm-super`, unmodified, a separate work; see [`third_party/cm-super`](third_party/cm-super/README.md)) | 0.3.4 | GNU General Public License, version 2 or later, with an exception for including the fonts in PostScript or PDF documents ([text](third_party/cm-super/licenses/COPYING), [exception](third_party/cm-super/licenses/README)); Copyright (c) 2001, 2002, 2008 Vladimir Volovich. Source: `fonts/ps-type1/cm-super` on [CTAN](https://ctan.org/pkg/cm-super) |
| [Adobe Glyph List](https://github.com/adobe-type-tools/agl-aglfn) (`glyphlist.txt`, compiled into `PaperKnife.Core.dll`, unmodified; see [`third_party/agl`](third_party/agl/README.md)) | 2.0 | BSD-3-Clause ([text](third_party/agl/licenses/LICENSE.txt)); Copyright 2002-2019 Adobe |

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
Arial). The only font data in this repository are the AMS, Latin Modern and CM-Super fonts above;
no third-party documents are stored in it.
