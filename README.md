# SlithyLens

Small, no-bloat photo tools for turning iPhone photos (HEIC) into JPGs: full size,
**Small** (500 KB or less), **Crop**, and **Lens**, which straightens a photographed page
or whiteboard from four corners, the way Office Lens did.

*'Twas brillig, and the slithy toves / Did gyre and gimble in the wabe.*

| Folder | What | Version |
|---|---|---|
| [`docs/`](docs) | **iPhone** web app — add it to the Home Screen; works offline. *(Not documentation — see below.)* | iPhone v002 |
| [`windows/`](windows) | **Windows** app — C# / .NET 10 WPF, build with `build.cmd` | v005 |

> **Why is the iPhone app in a folder called `docs`?** GitHub Pages' simple "Deploy from a
> branch" setting can only publish the top of the repo or a folder named exactly `docs`. So the
> iPhone app lives in `docs/` even though it isn't documentation. Pages setting:
> **Settings → Pages → Deploy from a branch → `main` → `/docs`**. Renaming the folder would break
> the site unless Pages is switched to a GitHub Actions workflow.

## iPhone

The app is published from `docs/` with GitHub Pages:
**https://obiwanchunn.github.io/slithylens/**

1. Open that link in **Safari** on the iPhone.
2. **Share** → **Add to Home Screen** → **Add**.
3. Open SlithyLens from the Home Screen.

Each result (`name.jpg`, `name_small.jpg`, `name_crop.jpg`, `name_lens.jpg`) opens so you can
check it; **Save…** brings up the share sheet — **Save to Files** keeps the name, **Save Image**
puts it in Photos. **‹ Back** returns to the photo it came from. Pinch to zoom, drag to pan,
double-tap for fit / 100%; dragging a crop edge or lens corner shows a magnifier.

Everything happens on the phone; photos are never uploaded. Saved copies are sRGB JPGs without
the original's date, location or camera details. Safari limits canvases to about 16.7 megapixels,
so 24/48 MP photos are worked on at that size (crops still come from the full-size original).

Files: `index.html` (layout), `app.js` (everything else), `sw.js` (offline), `manifest.webmanifest`
and `icons/` (Home Screen). No build step, no libraries. When releasing, bump `VERSION` in
`app.js`, the `?v=` on `app.js` in `index.html`, and `CACHE` in `sw.js` together. (GitHub Pages lets
browsers reuse files for 10 minutes; the new `?v=` makes phones fetch the new app.js at once.)

## Windows

See [`windows/README.md`](windows/README.md). In short: install the .NET 10 SDK, run
`windows\build.cmd`, open `windows\publish\SlithyLens.exe`, then **⋯ → Add SlithyLens to Open
with and Edit with**. HEIC decoding is ImageMagick (Magick.NET), so no Store extensions are needed.
