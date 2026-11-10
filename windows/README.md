# SlithyLens

A small Windows viewer for iPhone HEIC photos that turns them into JPGs, with a crop
tool and an Office Lens–style straightener.

*'Twas brillig, and the slithy toves / Did gyre and gimble in the wabe.*

## Build

You need the .NET 10 SDK (x64). (.NET 8, which MdView uses, reaches end of support on 2026-11-10;
the two install side by side.) `winget install Microsoft.DotNet.SDK.10` installs it.

1. Get the source onto a **local** disk: on GitHub, **Code → Download ZIP** (or `git clone`), or
   unzip a saved copy. Not inside a Google Drive folder: Drive's virtual disk fights with the
   build's temp files.
2. In the `windows` folder run `build.cmd` (in PowerShell: `.\build.cmd`).
   The first build downloads ImageMagick for .NET from nuget.org, so it takes a minute.
3. The program is `publish\SlithyLens.exe`.

`build.cmd standalone` makes a bigger exe that runs on a PC without .NET installed.

## Open with / Edit with (defaults untouched)

Once, open SlithyLens and click **⋯** → **Add SlithyLens to Open with and Edit with (.heic, .jpg, .png)…**.
That puts SlithyLens in each file's **Open with** list and adds **Edit with SlithyLens** to the full
right-click menu (Windows 11: **Show more options**, or Shift+right-click — Windows keeps its short
menu's "Edit with" for Store apps). Your default apps don't change.

Each newer build takes those entries over the first time you open it, so after building a new
version just open it once. **⋯ → About** shows which version you're running and where it is.
`SlithyLens.exe --register` / `--unregister` do the same from a command line.

If you ever do want it as the default for HEIC: right-click a .heic → **Open with** →
**Choose another app** → **SlithyLens** → **Always**.

## Use

| Button | Saves (next to the original), then opens it in a new window |
|---|---|
| **JPG** | `name.jpg`, full size, quality 92 — greyed out if the photo is already a .jpg |
| **Small** | `name_small.jpg`, a JPG of 500 KB or less — greyed out if the photo is already a .jpg that small |
| **Crop** → adjust → **Save crop** | `name_crop.jpg` |
| **Lens** → drag 4 corners → **Save lens** | `name_lens.jpg`, straightened to a rectangle |

So after Crop or Lens, click **Small** in the new window to get `name_crop_small.jpg` /
`name_lens_small.jpg`. Saving again replaces the earlier file of the same name.

**Mouse:** wheel zooms around the pointer · left-drag pans (or moves a crop edge or corner when you
grab one) · double-click toggles fit / 100% · clicking the wheel fits the photo in the window.
While you drag a crop or lens corner, a magnifier shows exactly where it sits.

**Keys:** Ctrl+O open · F fit · 1 actual pixels · ← → previous/next photo in the folder ·
C crop · L lens · Enter save · Esc cancel.

The photo opens fitted to the window. You can also drag files onto the window.

## Version

Set in `SlithyLens/SlithyLens.csproj`: bump `Version` (1.0.5 = v005) and `InformationalVersion`
together. Shown in **⋯ → About**.

## How the Small (≤ 500 KB) copies are made

"500 KB" is taken as 500,000 bytes, so it satisfies upload limits that count either 500,000 or
512,000. SlithyLens first keeps the full size and lowers the JPEG quality (no lower than 70).
If that isn't enough, it shrinks the picture and uses the best quality that fits at that size.
The status bar shows the size that came out.

## Notes

- Colors: iPhone photos are in Display P3. SlithyLens converts to standard sRGB so the JPGs
  look the same in every app and browser. EXIF (date taken, camera, GPS) is kept.
- It opens JPG, PNG, WebP, AVIF and TIFF as well, but only offers itself for .heic/.heif.
- Built with C#/.NET 10 WPF and ImageMagick (Magick.NET, Apache 2.0), which includes the HEIC
  decoder, so the Microsoft Store HEIF/HEVC extensions aren't needed.
- The icon is a tove (art/tove.svg; `art/make_svg.py` regenerates it).
