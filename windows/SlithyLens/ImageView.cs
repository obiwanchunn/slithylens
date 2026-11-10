using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SlithyLens;

public enum EditMode { View, Crop, Lens }

/// <summary>
/// Draws the photo with zoom (mouse wheel) and pan (left drag), plus the crop
/// rectangle or the four-corner "lens" shape on top. Everything is drawn in
/// OnRender; the crop/lens geometry is kept in image-pixel coordinates.
/// </summary>
public sealed class ImageView : FrameworkElement
{
    private const double HandleRadius = 7;     // drawn size of a handle (DIPs)
    private const double GrabTolerance = 12;   // how close the mouse must be to grab one
    private const double LoupeRadius = 70;
    private const double MaxZoomPerPixel = 32; // device pixels per image pixel

    private static readonly Brush Backdrop = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush HintBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0x9B, 0xFF)));
    private static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)));
    private static readonly Pen EdgeDark = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3.5));
    private static readonly Pen EdgeLight = Frozen(new Pen(Accent, 1.5));
    private static readonly Pen Thirds = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen HandlePen = Frozen(new Pen(Accent, 2));
    private static readonly Pen LoupeRing = Frozen(new Pen(new SolidColorBrush(Colors.White), 2.5));
    private static readonly Pen Crosshair = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0x40, 0x40)), 1));

    private BitmapSource? _source;
    private double _scale = 1;          // DIPs per image pixel
    private Vector _offset;             // screen position of the image's top-left corner
    private bool _fitMode = true;       // keep refitting when the window resizes

    private Rect _crop;                                 // image pixels
    private readonly Point[] _quad = new Point[4];      // image pixels: TL, TR, BR, BL

    private enum DragKind { None, Pan, Handle }
    private DragKind _drag;
    private int _handle;                 // crop: 0..7 clockwise from top-left; lens: 0..3
    private Point _panStart;
    private Vector _offsetAtPanStart;
    private Vector _grabDelta;           // keeps a grabbed handle from jumping to the cursor

    public EditMode Mode { get; private set; } = EditMode.View;

    /// <summary>Raised when the zoom changes (for the zoom label).</summary>
    public event EventHandler? ViewChanged;

    public ImageView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---------------------------------------------------------------- public API

    public bool HasImage => _source != null;
    private double ImgW => _source?.PixelWidth ?? 1;
    private double ImgH => _source?.PixelHeight ?? 1;

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Zoom in percent of real pixels (100 = one image pixel per screen pixel).</summary>
    public double ZoomPercent => _scale * DpiScale * 100;

    public void SetImage(BitmapSource? source)
    {
        _source = source;
        Mode = EditMode.View;
        _drag = DragKind.None;
        Fit();
    }

    public void Fit()
    {
        _fitMode = true;
        if (_source != null && ActualWidth > 0 && ActualHeight > 0)
        {
            _scale = FitScale();
            _offset = new Vector((ActualWidth - ImgW * _scale) / 2, (ActualHeight - ImgH * _scale) / 2);
        }
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>100%: one image pixel per screen pixel, centred on the middle of the window.</summary>
    public void ActualPixels() => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / DpiScale / _scale);

    public void StartCrop(Rect? previous)
    {
        if (_source == null) return;
        if (previous is Rect p && p.Width >= 1 && p.Height >= 1 && p.Right <= ImgW && p.Bottom <= ImgH)
            _crop = p;
        else
        {
            double mx = ImgW * 0.05, my = ImgH * 0.05;
            _crop = new Rect(mx, my, ImgW - 2 * mx, ImgH - 2 * my);
        }
        Mode = EditMode.Crop;
        InvalidateVisual();
    }

    public void StartLens(Point[]? previous)
    {
        if (_source == null) return;
        if (previous is { Length: 4 })
            Array.Copy(previous, _quad, 4);
        else
        {
            double mx = ImgW * 0.08, my = ImgH * 0.08;
            _quad[0] = new Point(mx, my);
            _quad[1] = new Point(ImgW - mx, my);
            _quad[2] = new Point(ImgW - mx, ImgH - my);
            _quad[3] = new Point(mx, ImgH - my);
        }
        Mode = EditMode.Lens;
        InvalidateVisual();
    }

    public void EndEdit()
    {
        Mode = EditMode.View;
        _drag = DragKind.None;
        InvalidateVisual();
    }

    public Rect CropRect => _crop;

    /// <summary>The crop rounded to whole pixels and kept inside the image.</summary>
    public Int32Rect CropPixels
    {
        get
        {
            int x = (int)Math.Clamp(Math.Round(_crop.Left), 0, ImgW - 1);
            int y = (int)Math.Clamp(Math.Round(_crop.Top), 0, ImgH - 1);
            int r = (int)Math.Clamp(Math.Round(_crop.Right), x + 1, ImgW);
            int b = (int)Math.Clamp(Math.Round(_crop.Bottom), y + 1, ImgH);
            return new Int32Rect(x, y, r - x, b - y);
        }
    }

    public Point[] Quad => (Point[])_quad.Clone();

    /// <summary>True when the four corners make a proper (convex, not twisted) shape.</summary>
    public bool QuadIsValid
    {
        get
        {
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector a = _quad[(i + 1) % 4] - _quad[i];
                Vector b = _quad[(i + 2) % 4] - _quad[(i + 1) % 4];
                double cross = Vector.CrossProduct(a, b);
                if (Math.Abs(cross) < 1e-6) return false;
                int s = Math.Sign(cross);
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- coordinates

    private Point ToScreen(Point p) => new(p.X * _scale + _offset.X, p.Y * _scale + _offset.Y);
    private Point ToImage(Point s) => new((s.X - _offset.X) / _scale, (s.Y - _offset.Y) / _scale);
    private Point ClampToImage(Point p) => new(Math.Clamp(p.X, 0, ImgW), Math.Clamp(p.Y, 0, ImgH));

    private double FitScale()
    {
        const double pad = 8;
        double w = Math.Max(1, ActualWidth - 2 * pad), h = Math.Max(1, ActualHeight - 2 * pad);
        return Math.Min(w / ImgW, h / ImgH);
    }

    private void ZoomAt(Point screen, double factor)
    {
        if (_source == null) return;
        double min = Math.Min(FitScale(), 1 / DpiScale) * 0.5;
        double max = MaxZoomPerPixel / DpiScale;
        double newScale = Math.Clamp(_scale * factor, min, Math.Max(min, max));
        Point anchor = ToImage(screen);
        _scale = newScale;
        _offset = new Vector(screen.X - anchor.X * _scale, screen.Y - anchor.Y * _scale);
        _fitMode = false;
        KeepOnScreen();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Don't let the photo be dragged completely out of sight.</summary>
    private void KeepOnScreen()
    {
        const double margin = 60;
        static double Limit(double v, double lo, double hi) => lo > hi ? (lo + hi) / 2 : Math.Clamp(v, lo, hi);
        _offset.X = Limit(_offset.X, margin - ImgW * _scale, ActualWidth - margin);
        _offset.Y = Limit(_offset.Y, margin - ImgH * _scale, ActualHeight - margin);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fitMode) Fit();
        else
        {
            // keep the same part of the photo in the middle
            _offset += new Vector((info.NewSize.Width - info.PreviousSize.Width) / 2,
                                  (info.NewSize.Height - info.PreviousSize.Height) / 2);
            KeepOnScreen();
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- handles

    private Point[] CropHandlePoints()
    {
        Rect r = _crop;
        double cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
            new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy),
        };
    }

    /// <summary>Which handle (if any) is under the mouse. -1 for none.</summary>
    private int HitHandle(Point screen)
    {
        if (Mode == EditMode.Lens)
        {
            int best = -1; double bestD = GrabTolerance;
            for (int i = 0; i < 4; i++)
            {
                double d = (ToScreen(_quad[i]) - screen).Length;
                if (d <= bestD) { best = i; bestD = d; }
            }
            return best;
        }
        if (Mode == EditMode.Crop)
        {
            Point[] pts = CropHandlePoints();
            for (int i = 0; i < 8; i += 2)                       // corners first
                if ((ToScreen(pts[i]) - screen).Length <= GrabTolerance) return i;
            for (int i = 1; i < 8; i += 2)
                if ((ToScreen(pts[i]) - screen).Length <= GrabTolerance) return i;

            // anywhere along an edge works too
            Point tl = ToScreen(_crop.TopLeft), br = ToScreen(_crop.BottomRight);
            bool inX = screen.X > tl.X && screen.X < br.X, inY = screen.Y > tl.Y && screen.Y < br.Y;
            const double t = 6;
            if (inX && Math.Abs(screen.Y - tl.Y) <= t) return 1;
            if (inY && Math.Abs(screen.X - br.X) <= t) return 3;
            if (inX && Math.Abs(screen.Y - br.Y) <= t) return 5;
            if (inY && Math.Abs(screen.X - tl.X) <= t) return 7;
        }
        return -1;
    }

    private Point HandlePoint(int h) => Mode == EditMode.Lens ? _quad[h] : CropHandlePoints()[h];

    private Cursor CursorForHandle(int h)
    {
        if (Mode == EditMode.Lens) return Cursors.Cross;
        return h switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };
    }

    private void MoveHandle(Point img)
    {
        img = ClampToImage(img);
        if (Mode == EditMode.Lens)
        {
            _quad[_handle] = img;
            return;
        }
        const double min = 1;
        double l = _crop.Left, t = _crop.Top, r = _crop.Right, b = _crop.Bottom;
        if (_handle is 0 or 6 or 7) l = Math.Min(img.X, r - min);
        if (_handle is 2 or 3 or 4) r = Math.Max(img.X, l + min);
        if (_handle is 0 or 1 or 2) t = Math.Min(img.Y, b - min);
        if (_handle is 4 or 5 or 6) b = Math.Max(img.Y, t + min);
        _crop = new Rect(new Point(l, t), new Point(r, b));
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_source == null) return;
        Point pos = e.GetPosition(this);

        int h = HitHandle(pos);
        if (h < 0 && e.ClickCount == 2)
        {
            // double-click: toggle between fit and 100%
            if (_fitMode) ZoomAt(pos, 1 / DpiScale / _scale);
            else Fit();
            e.Handled = true;
            return;
        }

        if (h >= 0)
        {
            _drag = DragKind.Handle;
            _handle = h;
            _grabDelta = ToScreen(HandlePoint(h)) - pos;
            Cursor = CursorForHandle(h);
        }
        else
        {
            _drag = DragKind.Pan;
            _panStart = pos;
            _offsetAtPanStart = _offset;
            Cursor = Cursors.SizeAll;
        }
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_source == null) return;
        Point pos = e.GetPosition(this);
        switch (_drag)
        {
            case DragKind.Pan:
                _offset = _offsetAtPanStart + (pos - _panStart);
                _fitMode = false;
                KeepOnScreen();
                InvalidateVisual();
                break;
            case DragKind.Handle:
                MoveHandle(ToImage(pos + _grabDelta));
                InvalidateVisual();
                break;
            default:
                int h = HitHandle(pos);
                Cursor = h >= 0 ? CursorForHandle(h) : Cursors.Hand;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            InvalidateVisual();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_source == null) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.2, e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        // clicking the wheel itself: back to fit-in-window
        if (e.ChangedButton == MouseButton.Middle && _source != null)
        {
            Fit();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(RenderSize));
        if (_source == null)
        {
            var text = new FormattedText("Open a HEIC photo (Ctrl+O) or drop one here",
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 16, HintBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
            return;
        }

        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y));
        dc.DrawImage(_source, new Rect(0, 0, ImgW, ImgH));
        dc.Pop();

        if (Mode == EditMode.Crop) DrawCrop(dc);
        else if (Mode == EditMode.Lens) DrawLens(dc);

        if (_drag == DragKind.Handle) DrawLoupe(dc);
    }

    private Rect ImageScreenRect => new(ToScreen(new Point(0, 0)), ToScreen(new Point(ImgW, ImgH)));

    private void DrawCrop(DrawingContext dc)
    {
        Rect r = new(ToScreen(_crop.TopLeft), ToScreen(_crop.BottomRight));
        dc.DrawGeometry(Shade, null, new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(ImageScreenRect), new RectangleGeometry(r)));

        for (int i = 1; i <= 2; i++)
        {
            double x = r.Left + r.Width * i / 3, y = r.Top + r.Height * i / 3;
            dc.DrawLine(Thirds, new Point(x, r.Top), new Point(x, r.Bottom));
            dc.DrawLine(Thirds, new Point(r.Left, y), new Point(r.Right, y));
        }
        dc.DrawRectangle(null, EdgeDark, r);
        dc.DrawRectangle(null, EdgeLight, r);

        Point[] pts = CropHandlePoints();
        for (int i = 0; i < 8; i++)
        {
            Point p = ToScreen(pts[i]);
            double s = i % 2 == 0 ? HandleRadius : HandleRadius * 0.75;
            dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - s, p.Y - s, 2 * s, 2 * s));
        }

        Int32Rect px = CropPixels;
        DrawLabel(dc, $"{px.Width} × {px.Height}", new Point(r.Left + 6, r.Top + 6));
    }

    private StreamGeometry QuadGeometry(Func<Point, Point> map)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(map(_quad[0]), true, true);
            ctx.PolyLineTo(new[] { map(_quad[1]), map(_quad[2]), map(_quad[3]) }, true, false);
        }
        g.Freeze();
        return g;
    }

    private void DrawLens(DrawingContext dc)
    {
        var quad = QuadGeometry(ToScreen);
        dc.DrawGeometry(Shade, null, new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(ImageScreenRect), quad));
        dc.DrawGeometry(null, EdgeDark, quad);
        dc.DrawGeometry(null, QuadIsValid ? EdgeLight : new Pen(Brushes.OrangeRed, 2), quad);

        for (int i = 0; i < 4; i++)
        {
            Point p = ToScreen(_quad[i]);
            dc.DrawEllipse(HandleFill, HandlePen, p, HandleRadius + 2, HandleRadius + 2);
        }
        if (!QuadIsValid)
            DrawLabel(dc, "Corners are crossed — drag them back into a four-sided shape", ToScreen(_quad[0]) + new Vector(12, 12));
    }

    /// <summary>Magnified view next to the handle being dragged, for placing it exactly.</summary>
    private void DrawLoupe(DrawingContext dc)
    {
        Point target = HandlePoint(_handle);
        Point at = ToScreen(target);

        // Sit up-left of the handle, flipping to whichever side has room.
        double gap = LoupeRadius + 30;
        double cx = at.X - gap < LoupeRadius ? at.X + gap : at.X - gap;
        double cy = at.Y - gap < LoupeRadius ? at.Y + gap : at.Y - gap;
        var c = new Point(cx, cy);

        double zoom = Math.Max(_scale * 4, Math.Min(4 / DpiScale, MaxZoomPerPixel / DpiScale));
        zoom = Math.Min(zoom, MaxZoomPerPixel / DpiScale);

        dc.PushClip(new EllipseGeometry(c, LoupeRadius, LoupeRadius));
        dc.DrawRectangle(Backdrop, null, new Rect(c.X - LoupeRadius, c.Y - LoupeRadius, 2 * LoupeRadius, 2 * LoupeRadius));
        dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, c.X - target.X * zoom, c.Y - target.Y * zoom));
        dc.DrawImage(_source, new Rect(0, 0, ImgW, ImgH));
        var thin = new Pen(Accent, 1.5 / zoom);
        if (Mode == EditMode.Lens) dc.DrawGeometry(null, thin, QuadGeometry(p => p));
        else dc.DrawRectangle(null, thin, _crop);
        dc.Pop();
        dc.DrawLine(Crosshair, new Point(c.X - LoupeRadius, c.Y), new Point(c.X + LoupeRadius, c.Y));
        dc.DrawLine(Crosshair, new Point(c.X, c.Y - LoupeRadius), new Point(c.X, c.Y + LoupeRadius));
        dc.Pop();
        dc.DrawEllipse(null, EdgeDark, c, LoupeRadius, LoupeRadius);
        dc.DrawEllipse(null, LoupeRing, c, LoupeRadius, LoupeRadius);
    }

    private void DrawLabel(DrawingContext dc, string s, Point at)
    {
        var text = new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X, at.Y, text.Width + 12, text.Height + 6), 3, 3);
        dc.DrawText(text, new Point(at.X + 6, at.Y + 3));
    }
}
