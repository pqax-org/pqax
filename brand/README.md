# The PQAX logo

The mark, the wordmark, and the lock-ups of PQAX, chosen on 23 October 2026 after five rounds of drafts. The logo refers to PQAX, the format, and to the tools that read or write its files. Use the files as they are.

| File | Content | Where it fits |
|---|---|---|
| `mark.svg` | The mark alone: a Parquet file of three row groups of column chunks in `#3C7AFE`, with its footer in `#EA580C`. One file for light and dark mode. | Favicons, package icons, avatars, anywhere the name is not needed |
| `wordmark.svg` | PQAX in Red Hat Display Bold, as paths, in `currentColor` | Beside the mark, where a page draws the name in its own color |
| `logo.svg` | The mark and the wordmark, the wordmark in `currentColor` | A page that sets its text color by mode |
| `logo-light.svg` | The mark and the wordmark in slate 800 `#1E293B` | A light background, where a file cannot switch with the mode |
| `logo-dark.svg` | The mark and the wordmark in slate 200 `#E2E8F0` | A dark background |
| `png/mark-<size>.png` | The mark on a transparent background, at 16, 32, 48, 64, 128, 180, 192, 256, 512, and 1024 px | Where SVG is not accepted |
| `png/logo-light-<height>.png`, `png/logo-dark-<height>.png` | The two lock-ups on a transparent background, 64 and 256 px high | Where SVG is not accepted |

## The rules

- **The mark** is a square on a grid of 4 units out of 64: three row groups of column chunks, the folded corner of a file cutting the last chunk of the first row group, and the footer, the metadata that PQAX is built from, below them. The colors are fixed: `#3C7AFE` for the chunks, 3.9:1 on white and 5.4:1 on black, and `#EA580C` for the footer, 3.6:1 and 5.9:1. No other color, no outline, no tile behind it.
- **The wordmark** is PQAX in Red Hat Display Bold, version 5.0.0, under the [SIL Open Font License 1.1](https://github.com/RedHatOfficial/RedHatFont/tree/5.0.0), with the kerning of the font plus 6% of the cap height between the letters, converted to paths: no font file is needed to show it. Its color is slate 800 `#1E293B` on a light background and slate 200 `#E2E8F0` on a dark one, at least 13:1 on both.
- **The lock-up** puts the mark beside the wordmark: a square of 1.2 times the cap height, centered on the cap height, 0.5 cap height from the P, measured on the ink. The lock-up files are 38.4 units high for a cap height of 32; keep them 18 px high or more.
- **The PNG files** were rendered from the SVG files by Chromium at one device pixel per unit. The mark alone serves both modes, since its colors do not change.

## Where the files are used

| Place | Files |
|---|---|
| pqax.org, in `site/` | `logo.svg` inline in the header, `favicon.svg`, `favicon.ico`, and `apple-touch-icon.png` from the mark |
| pqax.tools, in [pqax-org/pqax.tools](https://github.com/pqax-org/pqax.tools) | The same, with the word "tools" in the same font beside the lock-up |
| The NuGet packages | `src/dotnet/pqax-icon.png`, a copy of `png/mark-128.png`, packed as the icon of every package |
| This repository | `logo-light.svg` and `logo-dark.svg` at the top of the README, switched by the reader's color scheme |

The drafts and the rounds that led here are not in this repository.
