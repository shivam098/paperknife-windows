# PaperKnife for Windows — roadmap

Milestones are ordered by value to users and by risk. Each milestone ends with a usable app.

## M0 — Foundation ✅

- Repository, Apache-2.0 license, third-party notices, CI on x64 and Arm64.
- `PaperKnife.Core`: PDFium bindings, immutable document snapshots, transactional edits with
  verification (character support, round trip, object counts, pixel comparison of the page with
  the edited text hidden), PDF permission rules, undo/redo, rotate/delete/move pages, append
  documents, merge and extract (engine only), atomic save.
- WinUI 3 app: open (picker, drag and drop, command line, passwords), page view and zoom, select
  text by clicking or from a list, edit and verify, undo/redo, page commands, save as,
  unsaved-changes prompt.
- Tests that generate their own PDFs, including cases PaperKnife must refuse.

## M1 — A dependable everyday editor

- Page thumbnails, continuous scrolling, fit to width or page, keyboard navigation.
- Drag to reorder pages; extract, split and merge in the UI (the engine already supports them).
- Find text (Ctrl+F).
- Show and fill form fields.
- Recent files.
- ✅ *Open with* and Default apps registration, added by the installer.
- ✅ Releases on GitHub: one installer for x64 and Arm64 that needs no administrator rights,
  plus portable ZIPs, built by CI from a tag. Still to do: code signing, a winget manifest and a
  Microsoft Store listing.
- A real-world test corpus, generated with common producers (Word, LibreOffice, browsers, LaTeX)
  using openly licensed fonts, and a report of how much of it can be edited.
- Accessibility review with Narrator, keyboard only and high contrast.

## M2 — Better editing

- ✅ **Surgical content editing.** Text edits change only the edited text operations in the page's
  content stream and are appended to the file as an incremental update, keeping every other byte
  (letter and word spacing, print colors, inline images, shadings, Type 3 fonts). PaperKnife
  reads the file structure and content itself and leaves anything uncertain to PDFium's page
  rewrite. Text the edit keeps keeps its kerning and word gaps, and spaces in fonts without a
  space glyph (TeX's) are drawn as gaps. Still to do: encrypted files, and compacting the file
  when it is saved.
- **Fallback fonts with consent.** When the document's font lacks a character, offer to use a
  similar installed font, embedding only the glyphs used and only if the font's license allows
  embedding. ✅ Done for the fonts of PDFs made with pdfLaTeX: when a subset of a Type 1 Computer
  Modern, Latin Modern or CM-Super font lacks a letter, the edited text is drawn with a complete
  copy of PaperKnife's identical font, and the PDF's own font stays as it was. Still to do:
  TrueType and OpenType subsets (for example from Word, from fonts installed on Windows when
  their license allows embedding, and Latin Modern as XeLaTeX and LuaLaTeX embed it), symbols
  LaTeX sets in a separate font (such as © in body text), and similar fonts for everything else.
- ✅ Edit whole lines in PDFs that draw text one character or word at a time: consecutive text
  objects are grouped into runs, only the changed characters are rewritten and the rest of the
  line moves to fit. Still to do: lines split across fonts (for example the "fi" ligature in PDFs
  saved from Chrome, which sits in a font of its own), paragraphs, and reflowing inside the
  original area.
- ✅ Delete a whole line. Still to do: add text boxes, images and signature images.
- Annotations: highlight, underline, notes, ink and shapes.

## M3 — Toolbox

- Compress (downsample and recompress images, with a preview of the quality).
- Images to PDF and PDF to images.
- Watermarks, page numbers, headers and footers.
- Password protect and unlock (qpdf).
- True redaction that removes content instead of covering it.

## M4 — Scanned documents

- Recognize text offline (Windows OCR or Tesseract) and make scanned PDFs searchable.
- Research: editing recognized text while matching the scanned appearance.

## Throughout

- **Security:** move PDFium into a separate low-privilege process so a malicious PDF can't reach
  the user's files; fuzz the engine with the corpus.
- **Performance:** load large files without reading them fully into memory, cache renderings,
  and keep undo affordable for very large documents.
- **Localization:** starting with English and Hindi.
