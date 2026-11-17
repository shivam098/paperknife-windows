# PaperKnife product principles

This document is shared by every PaperKnife app (Windows, Android, iOS and macOS). Each platform
repository has its own roadmap and architecture; this file describes what they all promise.

## Vision

A free, open-source PDF toolkit that runs entirely on your own device and treats your documents
with care. It starts with the hardest thing online PDF tools offer, **editing existing text in
place**, and grows into the everyday toolbox: organizing pages, merging, splitting, annotating,
filling forms, compressing and recognizing scanned text.

## Principles

1. **Private by design.** Documents never leave the device. No account, no uploads, no
   telemetry, no ads. Every feature works without a network connection. Where the platform can
   enforce this (for example, an Android app without the `INTERNET` permission, or a sandboxed
   macOS app without the network entitlement), PaperKnife asks the operating system to enforce it.
2. **Honest edits.** An edit changes the document's actual content. PaperKnife never fakes an
   edit by drawing a white box over old text, never silently swaps fonts, and never "redacts" by
   covering text that is still there.
3. **Verify, then commit.** Every change is applied to a copy, saved, re-read and checked. If the
   result differs from what was intended, or anything else on the page would change, the edit is
   cancelled and the user is told why in plain language.
4. **The original is safe.** Saving writes a new file by default. Files are replaced atomically,
   never left half-written. Undo is always available while the document is open.
5. **Native on every platform.** Each platform gets its own app and repository, built with that
   platform's own UI toolkit, so it looks, feels and performs like a native app. The apps share
   the PDF engine (PDFium), the editing model and the test strategy.
6. **Free and open.** Apache License 2.0. No watermarks and no paywall on basic features.

## What "edit text in place" means

A PDF page doesn't contain paragraphs. It contains instructions such as *draw these glyphs, in
this font, at this position*. Editing text in place therefore means replacing the characters
drawn by one of those instructions while keeping its font, size, position and color.

| Situation | What PaperKnife does |
|---|---|
| The font contains every character you typed | Applies the edit and verifies the page. |
| The font is missing a character (common with embedded subset fonts) | Refuses, and names the missing characters. A later version will offer, with your consent, to embed a matching font. |
| The text needs shaping (Hindi, Arabic, Thai, …) | Refuses for now; shaping support is planned. |
| Rewriting the page would change anything else | Refuses and leaves the document untouched. |
| The page is scanned (an image of text) | Not editable as text until OCR support lands. |
| The PDF is digitally signed | Warns that editing invalidates the signature before the first edit. |
| The author restricted editing (PDF permissions) | Respects the restriction, including refusing to copy the PDF's pages into another document. |

## Capability map

| Capability | Difficulty | Windows | Other platforms |
|---|---|---|---|
| View, zoom, navigate | Low | M0 ✅ | First milestone |
| Edit existing text with the original font | High | M0 ✅ | After viewing |
| Rotate, delete, insert and reorder pages | Low | M0 ✅ (reorder UI in M1) | Early |
| Merge, split, extract pages | Low | Engine ✅, UI in M1 | Early |
| Fill forms | Medium | M1 | Later |
| Add text, images, signature images | Medium | M2 | Later |
| Annotations (highlight, notes, ink, shapes) | Medium | M2 | Later |
| Edit text with a fallback font (with consent) | High | M2 | Later |
| True redaction (content removed, not covered) | High | M3 | Later |
| Compress, images ↔ PDF, watermark, page numbers | Medium | M3 | Later |
| Password protect and unlock | Medium | M3 | Later |
| Recognize scanned text (OCR), searchable PDFs | High | M4 | Later |
| Convert PDF to Word or Excel | Very high | Research only | Research only |

## Non-goals for version 1

- Cloud storage, sync, sharing links or collaboration.
- Accounts, subscriptions or in-app purchases.
- Generative AI features.
- Pixel-perfect conversion to Office formats.

## Test documents

Repositories never contain documents we don't have the right to share. Tests generate their own
PDFs, or use documents created for PaperKnife and released under CC0. Fonts used to create test
documents must be under the SIL Open Font License or similar. Users' documents are never
collected, including in bug reports, unless a user chooses to attach one.

## Measuring quality without telemetry

Because PaperKnife collects no usage data, quality is measured against the shared test corpus:

- **Editable coverage:** the share of text runs in the corpus that can be edited.
- **Collateral changes:** must be zero; every accepted edit is verified to change nothing else.
- **Fidelity:** saved files must open in other major PDF readers with the same appearance.

## Sharing work across platforms

The apps are separate, but these are shared:

- **This document** (product principles and editing guarantees).
- **The engine:** PDFium, from the same prebuilt
  [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) release where possible.
- **The editing model:** immutable snapshots, transactional edits, verification before commit.
- **The test corpus** and its expected results.

Until a second platform starts implementation, this document and the corpus live in the Windows
repository. Once two apps depend on them, they should move to a dedicated repository.
