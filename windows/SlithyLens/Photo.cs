using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace SlithyLens;

/// <summary>One saved output file, for the status bar.</summary>
public sealed record SavedFile(string Path, long Bytes, int Width, int Height, uint Quality);

/// <summary>
/// The open photo. Holds the full-resolution pixels (upright, sRGB) in ImageMagick
/// plus a WPF bitmap of the same pixels for the screen. All the saving lives here.
/// </summary>
public sealed class Photo : IDisposable
{
    /// <summary>JPEG quality for the full-size saves (name.jpg, name_crop.jpg, name_lens.jpg).</summary>
    public const uint FullQuality = 92;

    /// <summary>Size cap for the _small copies. 500,000 bytes fits both "500 KB" conventions.</summary>
    public const long SmallLimitBytes = 500_000;

    // The search range for the _small copies. Below MinSmallQuality we shrink the
    // picture instead of crushing the quality further.
    private const uint MaxSmallQuality = 90;
    private const uint MinSmallQuality = 70;

    private readonly MagickImage _image;

    public string SourcePath { get; }
    public BitmapSource Preview { get; }
    public int Width => (int)_image.Width;
    public int Height => (int)_image.Height;

    private Photo(string path, MagickImage image, BitmapSource preview)
    {
        SourcePath = path;
        _image = image;
        Preview = preview;
    }

    public static Photo Load(string path)
    {
        var image = new MagickImage(path);
        try
        {
            // libheif already applies the HEIC rotation; this covers JPEGs etc. with an EXIF orientation.
            image.AutoOrient();

            // iPhone photos are Display P3. Convert to sRGB so the JPEGs look right everywhere.
            // (Does nothing when the file has no embedded colour profile.)
            try { image.TransformColorSpace(ColorProfiles.SRGB); } catch (MagickException) { /* keep as is */ }

            // The pixels are upright now, so the EXIF must say "upright" too, or viewers rotate twice.
            var exif = image.GetExifProfile();
            if (exif != null)
            {
                exif.SetValue(ExifTag.Orientation, (ushort)1);
                exif.RemoveThumbnail();
                image.SetProfile(exif);
            }

            // JPEG has no transparency (only matters for PNGs and the like).
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }

            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool smallJpeg = (ext == ".jpg" || ext == ".jpeg") && new FileInfo(path).Length <= SmallLimitBytes;
            return new Photo(path, image, ToBitmapSource(image)) { IsSmallJpeg = smallJpeg };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static BitmapSource ToBitmapSource(IMagickImage<byte> image)
    {
        int w = (int)image.Width, h = (int)image.Height;
        byte[] bgr;
        using (var pixels = image.GetPixelsUnsafe())
            bgr = pixels.ToByteArray(PixelMapping.BGR) ?? throw new InvalidOperationException("Couldn't read the pixels.");
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, bgr, w * 3);
        bmp.Freeze();
        return bmp;
    }

    // ---------- output names: saved next to the source file ----------

    private string OutPathUnchecked(string suffix)
    {
        string dir = System.IO.Path.GetDirectoryName(SourcePath) ?? ".";
        string name = System.IO.Path.GetFileNameWithoutExtension(SourcePath);
        return System.IO.Path.Combine(dir, name + suffix + ".jpg");
    }

    private bool IsSource(string path) =>
        string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(SourcePath), StringComparison.OrdinalIgnoreCase);

    private string OutPath(string suffix)
    {
        string path = OutPathUnchecked(suffix);
        if (IsSource(path))
            throw new InvalidOperationException($"That would overwrite the original ({System.IO.Path.GetFileName(path)}).");
        return path;
    }

    /// <summary>False when the photo is already name.jpg, so the JPG button would overwrite it.</summary>
    public bool CanSaveJpg => !IsSource(OutPathUnchecked(""));

    /// <summary>True for a .jpg/.jpeg that is already within the size cap (the Small button has nothing to do).</summary>
    public bool IsSmallJpeg { get; private init; }

    // ---------- the four buttons ----------

    /// <summary>Button 1: name.jpg</summary>
    public SavedFile SaveJpg() => WriteFull(_image, OutPath(""));

    /// <summary>
    /// Button 2: name_small.jpg (≤ 500 KB). On a crop or lens result this gives
    /// name_crop_small.jpg / name_lens_small.jpg, since that file is already name_crop.jpg.
    /// </summary>
    public SavedFile SaveSmall() => WriteSmall(_image, OutPath("_small"));

    /// <summary>Button 3: name_crop.jpg (full size; the window then opens it for checking)</summary>
    public SavedFile SaveCrop(System.Windows.Int32Rect r)
    {
        using var img = _image.Clone();
        img.Crop(new MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
        img.ResetPage();
        return WriteFull(img, OutPath("_crop"));
    }

    /// <summary>
    /// Button 4: name_lens.jpg. Straightens the four-corner shape (top-left, top-right,
    /// bottom-right, bottom-left, in image pixels) into a rectangle.
    /// </summary>
    public SavedFile SaveLens(System.Windows.Point[] q)
    {
        using var img = Unwarp(_image, q);
        return WriteFull(img, OutPath("_lens"));
    }

    private static IMagickImage<byte> Unwarp(IMagickImage<byte> src, System.Windows.Point[] q)
    {
        static double Dist(System.Windows.Point a, System.Windows.Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // Output size: the longer of each pair of opposite edges (what Office Lens does too).
        uint w = (uint)Math.Max(1, Math.Round(Math.Max(Dist(q[0], q[1]), Dist(q[3], q[2]))));
        uint h = (uint)Math.Max(1, Math.Round(Math.Max(Dist(q[0], q[3]), Dist(q[1], q[2]))));

        var img = src.Clone();
        try
        {
            img.VirtualPixelMethod = VirtualPixelMethod.Edge;
            var settings = new DistortSettings(DistortMethod.Perspective)
            {
                Viewport = new MagickGeometry(0, 0, w, h),
            };
            // pairs of (source x, y) → (destination x, y)
            img.Distort(settings,
                q[0].X, q[0].Y, 0, 0,
                q[1].X, q[1].Y, w, 0,
                q[2].X, q[2].Y, w, h,
                q[3].X, q[3].Y, 0, h);
            img.ResetPage();
            return img;
        }
        catch
        {
            img.Dispose();
            throw;
        }
    }

    // ---------- JPEG writing ----------

    private static byte[] Encode(IMagickImage<byte> img, uint quality)
    {
        img.Quality = quality;
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    private static SavedFile WriteFull(IMagickImage<byte> img, string path)
    {
        byte[] data = Encode(img, FullQuality);
        File.WriteAllBytes(path, data);
        return new SavedFile(path, data.Length, (int)img.Width, (int)img.Height, FullQuality);
    }

    /// <summary>
    /// Best-looking JPEG that fits under the size cap: keep full size and only lower the
    /// quality if that's enough; otherwise shrink the picture and pick the best quality
    /// that fits at the smaller size.
    /// </summary>
    private static SavedFile WriteSmall(IMagickImage<byte> src, string path)
    {
        double scale = 1.0;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using var img = src.Clone();
            if (scale < 0.999)
            {
                uint w = (uint)Math.Max(1, Math.Round(src.Width * scale));
                uint h = (uint)Math.Max(1, Math.Round(src.Height * scale));
                img.Resize(w, h);
            }

            byte[] best = Encode(img, MaxSmallQuality);
            uint bestQ = MaxSmallQuality;
            if (best.Length > SmallLimitBytes)
            {
                byte[] low = Encode(img, MinSmallQuality);
                if (low.Length > SmallLimitBytes)
                {
                    // File size goes roughly with the pixel count, so shrink by the square root.
                    scale *= Math.Sqrt((double)SmallLimitBytes / low.Length) * 0.97;
                    continue;
                }
                // Binary search: lo always fits, hi never does.
                best = low; bestQ = MinSmallQuality;
                uint lo = MinSmallQuality, hi = MaxSmallQuality;
                while (hi - lo > 1)
                {
                    uint mid = (lo + hi) / 2;
                    byte[] d = Encode(img, mid);
                    if (d.Length <= SmallLimitBytes) { best = d; bestQ = mid; lo = mid; }
                    else hi = mid;
                }
            }
            File.WriteAllBytes(path, best);
            return new SavedFile(path, best.Length, (int)img.Width, (int)img.Height, bestQ);
        }
        throw new InvalidOperationException("Couldn't get the JPEG under 500 KB.");
    }

    public void Dispose() => _image.Dispose();
}
