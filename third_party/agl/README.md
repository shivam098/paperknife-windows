# Adobe Glyph List

[`glyphlist.txt`](glyphlist.txt) is the Adobe Glyph List, table version 2.0, from
[adobe-type-tools/agl-aglfn](https://github.com/adobe-type-tools/agl-aglfn) (commit
`0276763a3cdf204a1badc283d90f5bc7ac13130d`), copied unmodified.

Copyright 2002-2019 Adobe (http://www.adobe.com/). It is licensed under the BSD 3-Clause
License, whose text is at the top of the file and in [`licenses/LICENSE.txt`](licenses/LICENSE.txt).

PaperKnife compiles it into `PaperKnife.Core.dll` as a resource. It tells which character a
glyph name such as `lcaron` or `afii10017` stands for, as PDFium (through FreeType) reads them,
for glyph names PaperKnife's own tables don't cover. PaperKnife uses that to encode new text in
fonts whose glyphs are selected by name, and to draw, in a complete copy of a TeX font, the
letters a PDF's character map says each code stands for.
