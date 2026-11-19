# Latin Modern Type 1 fonts

The `.pfb` files in [`fonts/`](fonts/) are the Type 1 Latin Modern fonts, version 2.004
(2009), of the [lm](https://ctan.org/pkg/lm) package version 2.005: the fonts pdfLaTeX embeds in
PDFs of documents that use `\usepackage{lmodern}` (font names such as `LMRoman10-Regular`). They
were copied unmodified from `fonts/lm.zip` on CTAN (`lm/fonts/type1/public/lm/`).

Copyright 2003–2009 B. Jackowski and J. M. Nowacki (on behalf of TeX users groups). The fonts
are licensed under the GUST Font License 1.0, which is the LaTeX Project Public License 1.3c
with a request that modified versions be renamed: see
[`licenses/GUST-FONT-LICENSE.TXT`](licenses/GUST-FONT-LICENSE.TXT), the package's
[`licenses/README-Latin-Modern.TXT`](licenses/README-Latin-Modern.TXT) and its list of files,
[`licenses/MANIFEST-Latin-Modern.TXT`](licenses/MANIFEST-Latin-Modern.TXT).

PaperKnife ships them as separate files next to the app (`Fonts\lm`), so the installer holds one
copy for every kind of PC, and uses them as it uses the AMS fonts (see
[`../amsfonts/README.md`](../amsfonts/README.md)): when a PDF embeds only part of one of these
fonts, and a text edit needs a letter that part lacks, PaperKnife adds the whole, unmodified font
to the edited page as a font of its own, used only by the edited text, after checking that every
glyph the PDF already had is identical in it. The PDF's own font isn't changed.
