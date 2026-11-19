# Test fonts

`DejaVuSans.ttf` is DejaVu Sans from [DejaVu fonts](https://dejavu-fonts.github.io/) 2.37
(`dejavu-fonts-ttf-2.37.zip` from the project's GitHub releases), copied unmodified. Its license is
in [`DejaVu-LICENSE.txt`](DejaVu-LICENSE.txt): the Bitstream Vera Fonts license, with DejaVu's
changes in the public domain.

The conformance corpus embeds it in the documents of cases that need an embedded TrueType font,
where the other tests use Arial when Windows has it: unlike Arial, DejaVu Sans may be
redistributed, so the corpus can be shared with PaperKnife's other apps.
