# AMS Type 1 Computer Modern fonts

The `.pfb` files in [`fonts/`](fonts/) are the Computer Modern fonts (`cm*.pfb`) of
[amsfonts](https://ctan.org/pkg/amsfonts) version 3.04 (2013-01-14), the canonical Type 1
versions TeX distributions embed in PDFs. They were copied unmodified from
`fonts/amsfonts.zip` on CTAN (`amsfonts/pfb/`).

Copyright (c) 1997, 2009, American Mathematical Society (http://www.ams.org). The fonts are
licensed under the SIL Open Font License 1.1: see [`licenses/OFL.txt`](licenses/OFL.txt), which
also lists their Reserved Font Names, and [`licenses/OFL-FAQ.txt`](licenses/OFL-FAQ.txt).

PaperKnife compiles them into `PaperKnife.Core.dll` as resources. When a PDF made with TeX embeds
only part of one of these fonts, and a text edit needs a letter that part lacks, PaperKnife adds
the whole, unmodified font to the edited page as a font of its own, used only by the edited
text, after checking that every glyph the PDF already had is identical in it. The PDF's own
font isn't changed.
