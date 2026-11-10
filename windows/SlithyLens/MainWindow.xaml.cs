using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace SlithyLens;

public partial class MainWindow : Window
{
    private static readonly string[] HeicExtensions = { ".heic", ".heif", ".hif" };
    private static readonly string[] OtherImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".tif", ".tiff", ".bmp", ".gif" };

    private Photo? _photo;
    private bool _busy;
    private string? _lastSaved;

    // Remembered per photo, so re-opening Crop/Lens picks up where you left off.
    private Rect? _lastCrop;
    private Point[]? _lastQuad;

    public MainWindow(string? path, bool cascade = false)
    {
        InitializeComponent();
        WindowPlacement.Restore(this);
        if (cascade && !double.IsNaN(Left) && !double.IsNaN(Top) && WindowState == WindowState.Normal)
        {
            // opened from Crop/Lens: sit a little down-right of the window it came from
            Left += 36;
            Top += 36;
        }

        View.ViewChanged += (_, _) => ZoomText.Text = View.HasImage ? $"{View.ZoomPercent:0}%" : "";
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += (_, _) => { if (!cascade) WindowPlacement.Save(this); };   // crop/lens windows don't move the saved spot
        Closed += (_, _) => _photo?.Dispose();

        Loaded += async (_, _) =>
        {
            UpdateButtons();
            if (path != null) await LoadAsync(path);
            else SetStatus("Open a HEIC photo to get started.");
        };
    }

    // ---------------------------------------------------------------- loading

    private async Task LoadAsync(string path)
    {
        if (_busy) return;
        if (View.Mode != EditMode.View) EndEdit();
        path = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(path);

        SetBusy(true);
        SetStatus($"Opening {name}…");
        try
        {
            var photo = await Task.Run(() => Photo.Load(path));
            _photo?.Dispose();
            _photo = photo;
            _lastCrop = null;
            _lastQuad = null;
            View.SetImage(photo.Preview);
            Title = $"{name} — {photo.Width} × {photo.Height} — SlithyLens";
            SetStatus($"{name}   ·   {photo.Width} × {photo.Height}   ·   {FormatSize(new FileInfo(path).Length)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open {name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await ShowOpenDialog();

    private async Task ShowOpenDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a photo",
            Filter = "HEIC photos (*.heic;*.heif)|*.heic;*.heif;*.hif|All images|*.heic;*.heif;*.hif;*.jpg;*.jpeg;*.png;*.webp;*.avif;*.tif;*.tiff;*.bmp;*.gif|All files|*.*",
        };
        if (_photo != null) dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_photo.SourcePath);
        if (dlg.ShowDialog(this) == true) await LoadAsync(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            await LoadAsync(files[0]);
            foreach (string extra in files.Skip(1)) OpenInNewProcess(extra);
        }
    }

    /// <summary>Left/Right arrow: previous/next photo of the same kind in the folder.</summary>
    private async Task StepAsync(int direction)
    {
        if (_photo == null || _busy) return;
        string current = _photo.SourcePath;
        string dir = System.IO.Path.GetDirectoryName(current) ?? ".";
        bool heic = HeicExtensions.Contains(System.IO.Path.GetExtension(current).ToLowerInvariant());
        string[] kinds = heic ? HeicExtensions : OtherImageExtensions;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir)
                .Where(f => kinds.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't list the folder: {ex.Message}");
            return;
        }
        files.Sort((a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));

        int i = files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        int next = i + direction;
        if (i < 0 || next < 0 || next >= files.Count)
        {
            SetStatus(direction > 0 ? "That's the last one in this folder." : "That's the first one in this folder.");
            return;
        }
        await LoadAsync(files[next]);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);   // Explorer's "IMG_2 before IMG_10" order

    // ---------------------------------------------------------------- the four buttons

    // Every saved file opens in its own window so it can be checked right away.
    private async void Jpg_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync("Saving JPG…", p => p.SaveJpg()) is SavedFile f)
            OpenInNewProcess(f.Path, cascade: true);
    }

    private async void Small_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync("Saving small JPG (≤ 500 KB)…", p => p.SaveSmall()) is SavedFile f)
            OpenInNewProcess(f.Path, cascade: true);
    }

    private async void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_photo == null || _busy) return;
        if (View.Mode == EditMode.Crop)
        {
            _lastCrop = View.CropRect;
            Int32Rect r = View.CropPixels;
            if (await SaveAsync("Cropping…", p => p.SaveCrop(r)) is SavedFile crop)
            {
                EndEdit();
                OpenInNewProcess(crop.Path, cascade: true);
            }
            return;
        }
        View.StartCrop(_lastCrop);
        UpdateButtons();
        SetStatus("Drag the edges or corners. Enter or “Save crop” saves _crop.jpg and opens it in a new window · Esc cancels · drag elsewhere to pan, wheel to zoom.");
    }

    private async void Lens_Click(object sender, RoutedEventArgs e)
    {
        if (_photo == null || _busy) return;
        if (View.Mode == EditMode.Lens)
        {
            _lastQuad = View.Quad;
            if (!View.QuadIsValid)
            {
                SetStatus("The corners are crossed. Drag them so they go top-left, top-right, bottom-right, bottom-left.");
                return;
            }
            Point[] q = View.Quad;
            if (await SaveAsync("Straightening…", p => p.SaveLens(q)) is SavedFile lens)
            {
                EndEdit();
                OpenInNewProcess(lens.Path, cascade: true);
            }
            return;
        }
        View.StartLens(_lastQuad);
        UpdateButtons();
        SetStatus("Drag the four corners onto the page's corners (a magnifier helps you place them). Enter or “Save lens” saves _lens.jpg and opens it in a new window · Esc cancels.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        EndEdit();
        SetStatus("Cancelled.");
    }

    private void EndEdit()
    {
        if (View.Mode == EditMode.Crop) _lastCrop = View.CropRect;
        if (View.Mode == EditMode.Lens) _lastQuad = View.Quad;
        View.EndEdit();
        UpdateButtons();
    }

    /// <summary>Runs a save off the UI thread and reports what was written. Null if it failed.</summary>
    private async Task<SavedFile?> SaveAsync(string busyText, Func<Photo, SavedFile> work)
    {
        if (_photo == null || _busy) return null;
        var photo = _photo;
        SetBusy(true);
        SetStatus(busyText);
        try
        {
            SavedFile f = await Task.Run(() => work(photo));
            _lastSaved = f.Path;
            SetStatus($"Saved  {System.IO.Path.GetFileName(f.Path)}  ({f.Width} × {f.Height}, {FormatSize(f.Bytes)})", showFolder: true);
            return f;
        }
        catch (Exception ex)
        {
            SetStatus("Save failed: " + ex.Message);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------------------------------------------------------------- view buttons, menu

    private void Fit_Click(object sender, RoutedEventArgs e) => View.Fit();
    private void Actual_Click(object sender, RoutedEventArgs e) => View.ActualPixels();

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuButton.ContextMenu;
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't register: " + ex.Message, "SlithyLens", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        MessageBox.Show(this,
            "SlithyLens is now in Open with, and \"Edit with SlithyLens\" is in the full right-click\n" +
            "menu (Shift+right-click, or Show more options), for .heic, .heif, .jpg, .jpeg and .png.\n\n" +
            "Your defaults haven't changed. New builds take these entries over the first time you open them.\n\n" +
            "If you ever want SlithyLens as the default for HEIC: right-click a .heic → Open with →\n" +
            "Choose another app → SlithyLens → Always.",
            "SlithyLens", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Unregister();
            SetStatus("SlithyLens removed from Open with.");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't unregister: " + ex.Message);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Mouse\n" +
        "  Wheel — zoom in/out around the pointer\n" +
        "  Left-drag — pan (or move a crop edge/corner when you grab one)\n" +
        "  Double-click — toggle fit / 100%\n" +
        "  Click the wheel — fit in window\n\n" +
        "Keys\n" +
        "  Ctrl+O open · F fit · 1 actual pixels\n" +
        "  ← / → previous / next photo in the folder\n" +
        "  C crop · L lens · Enter save crop/lens · Esc cancel\n\n" +
        "Files are saved next to the original (existing ones are replaced):\n" +
        "  JPG → name.jpg      Small → name_small.jpg (JPG, 500 KB or less)\n" +
        "  Crop → name_crop.jpg      Lens → name_lens.jpg\n" +
        "Each saved file opens in a new window to check it.\n" +
        "Small in that window gives name_crop_small.jpg / name_lens_small.jpg.\n" +
        "JPG is greyed out on a .jpg; Small is greyed out on a .jpg already ≤ 500 KB.",
        "SlithyLens", MessageBoxButton.OK, MessageBoxImage.None);

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"SlithyLens {AppInfo.Version}\n{Environment.ProcessPath}\n\n'Twas brillig, and the slithy toves\nDid gyre and gimble in the wabe…\n\n" +
        "HEIC decoding and JPEG encoding by ImageMagick (Magick.NET).",
        "About SlithyLens", MessageBoxButton.OK, MessageBoxImage.None);

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSaved == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_lastSaved}\""); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- keys

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool plain = Keyboard.Modifiers == ModifierKeys.None;

        if (ctrl && e.Key == Key.O) { e.Handled = true; await ShowOpenDialog(); return; }
        if (!plain || _busy) return;

        switch (e.Key)
        {
            case Key.Escape when View.Mode != EditMode.View:
                e.Handled = true;
                Cancel_Click(this, new RoutedEventArgs());
                break;
            case Key.Enter when View.Mode == EditMode.Crop:
                e.Handled = true;
                Crop_Click(this, new RoutedEventArgs());
                break;
            case Key.Enter when View.Mode == EditMode.Lens:
                e.Handled = true;
                Lens_Click(this, new RoutedEventArgs());
                break;
            case Key.C when View.Mode == EditMode.View:
                e.Handled = true;
                Crop_Click(this, new RoutedEventArgs());
                break;
            case Key.L when View.Mode == EditMode.View:
                e.Handled = true;
                Lens_Click(this, new RoutedEventArgs());
                break;
            case Key.F:
                e.Handled = true;
                View.Fit();
                break;
            case Key.D1:
            case Key.NumPad1:
                e.Handled = true;
                View.ActualPixels();
                break;
            case Key.Left when View.Mode == EditMode.View:
                e.Handled = true;
                await StepAsync(-1);
                break;
            case Key.Right when View.Mode == EditMode.View:
                e.Handled = true;
                await StepAsync(+1);
                break;
        }
    }

    // ---------------------------------------------------------------- UI state

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = _photo != null && !_busy;
        EditMode mode = View.Mode;

        OpenButton.IsEnabled = !_busy && mode == EditMode.View;
        bool jpgOk = _photo?.CanSaveJpg ?? true;
        JpgButton.IsEnabled = has && mode == EditMode.View && jpgOk;
        JpgButton.ToolTip = jpgOk ? "Save a full-size copy as name.jpg and open it" : "This photo is already a JPG";
        bool smallOk = !(_photo?.IsSmallJpeg ?? false);
        SmallButton.IsEnabled = has && mode == EditMode.View && smallOk;
        SmallButton.ToolTip = smallOk ? "JPG ≤ 500 KB: saves name_small.jpg and opens it" : "Already a JPG of 500 KB or less";
        CropButton.IsEnabled = has && mode != EditMode.Lens;
        LensButton.IsEnabled = has && mode != EditMode.Crop;
        FitButton.IsEnabled = ActualButton.IsEnabled = _photo != null;

        CropButton.Content = mode == EditMode.Crop ? "Save crop" : "Crop";
        LensButton.Content = mode == EditMode.Lens ? "Save lens" : "Lens";
        var accent = (Style)FindResource("AccentButton");
        var normal = (Style)FindResource(typeof(System.Windows.Controls.Button));
        CropButton.Style = mode == EditMode.Crop ? accent : normal;
        LensButton.Style = mode == EditMode.Lens ? accent : normal;
        CancelButton.Visibility = mode == EditMode.View ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = !_busy;
    }

    private void SetStatus(string text, bool showFolder = false)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        FolderButton.Visibility = showFolder ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, (bytes + 500) / 1000)} KB";

    public static void OpenInNewProcess(string path, bool cascade = false)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (cascade) psi.ArgumentList.Add("--cascade");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* ignore */ }
    }
}
