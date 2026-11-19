# CM-Super Type 1 fonts

The `.pfb` files in [`fonts/`](fonts/) are the fonts of [CM-Super](https://ctan.org/pkg/cm-super)
version 0.3.4 (2008-07-09): Type 1 versions of the T1- and TS1-encoded Computer Modern fonts (EC
and TC), which pdfLaTeX embeds in PDFs of documents that use `\usepackage[T1]{fontenc}` (font
names such as `SFRM1000`). They were copied unmodified from `fonts/ps-type1/cm-super.zip` on CTAN
(`cm-super/pfb/`), where their source is also distributed.

Copyright (c) 2001, 2002, 2008 Vladimir Volovich. The fonts are free software under the GNU
General Public License, version 2 or (at your option) any later version (see
[`licenses/COPYING`](licenses/COPYING)), with a special exception that permits including them in
a PostScript or PDF document regardless of the license of the document (see
[`licenses/README`](licenses/README)).

PaperKnife, which is licensed under the Apache License 2.0, doesn't build them into its own code:
they ship as separate files next to the app (`Fonts\cm-super`), a separate work alongside it.
PaperKnife reads them only to copy one, whole and unmodified, into a PDF that already embeds part
of it, as the font exception permits: when a text edit needs a letter that part lacks, the font
is added to the edited page as a font of its own, used only by the edited text, after checking
that every glyph the PDF already had is identical in it. The PDF's own font isn't changed.
