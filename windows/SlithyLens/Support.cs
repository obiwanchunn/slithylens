using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace SlithyLens;

/// <summary>Version info from the .csproj (Version / InformationalVersion).</summary>
internal static class AppInfo
{
    private static readonly Assembly Asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

    /// <summary>e.g. "v005 (2026-11-08)"</summary>
    public static string Version =>
        Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(unknown version)";

    /// <summary>e.g. 5 for v005 (the last number of Version 1.0.5)</summary>
    public static int Build => Asm.GetName().Version?.Build ?? 0;
}

/// <summary>Remembers the window's size and position between runs.</summary>
internal static class WindowPlacement
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlithyLens", "window.json");

    public static void Restore(Window w)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FilePath));
            if (p == null || p.Width < 200 || p.Height < 150) return;

            // Only use it if it's still on a screen (monitors come and go).
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!screen.IntersectsWith(new Rect(p.Left + 50, p.Top + 10, Math.Max(1, p.Width - 100), 40))) return;

            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = p.Left; w.Top = p.Top; w.Width = p.Width; w.Height = p.Height;
            if (p.Maximized) w.WindowState = WindowState.Maximized;
        }
        catch { /* first run or unreadable: use defaults */ }
    }

    public static void Save(Window w)
    {
        try
        {
            var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
            if (r.IsEmpty) return;
            var p = new Placement(r.Left, r.Top, r.Width, r.Height, w.WindowState == WindowState.Maximized);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(p));
        }
        catch { /* not important */ }
    }
}

/// <summary>
/// Per-user registration (no admin needed). Puts SlithyLens in "Open with", adds
/// "Edit with SlithyLens" to the full right-click menu, and lists it in Settings →
/// Default apps. Existing defaults are never changed.
/// </summary>
internal static class FileAssociation
{
    private const string AppName = "SlithyLens";
    private const string EditVerb = "SlithyLens.Edit";

    // one ProgId per kind of file, so Explorer shows the right type name
    private static readonly (string ProgId, string Description, string[] Extensions)[] Types =
    {
        ("SlithyLens.Heic", "HEIC photo", new[] { ".heic", ".heif", ".hif" }),
        ("SlithyLens.Jpeg", "JPEG image", new[] { ".jpg", ".jpeg" }),
        ("SlithyLens.Png",  "PNG image",  new[] { ".png" }),
    };

    public static void Register()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        string command = $"\"{exe}\" \"%1\"";

        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        foreach (var (progId, description, extensions) in Types)
        {
            using (var prog = classes.CreateSubKey(progId))
            {
                prog.SetValue("", description);
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            }
            foreach (string ext in extensions)
            {
                using var k = classes.CreateSubKey(ext + @"\OpenWithProgids");
                k.SetValue(progId, "");
            }
        }
        using (var app = classes.CreateSubKey(@"Applications\SlithyLens.exe"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            using var supported = app.CreateSubKey("SupportedTypes");
            foreach (var t in Types)
                foreach (string ext in t.Extensions) supported.SetValue(ext, "");
        }

        // Shows SlithyLens under Settings → Apps → Default apps.
        using (var caps = Registry.CurrentUser.CreateSubKey(@"Software\SlithyLens\Capabilities"))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "HEIC viewer, cropper and JPG converter");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var t in Types)
                foreach (string ext in t.Extensions) assoc.SetValue(ext, t.ProgId);
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue(AppName, @"Software\SlithyLens\Capabilities");

        // "Edit with SlithyLens" in the right-click menu, whatever app is the default.
        // (Windows 11 shows it under "Show more options" / Shift+right-click.)
        foreach (var t in Types)
            foreach (string ext in t.Extensions)
            {
                using var verb = classes.CreateSubKey($@"SystemFileAssociations\{ext}\shell\{EditVerb}");
                verb.SetValue("MUIVerb", "Edit with SlithyLens");
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = verb.CreateSubKey("command");
                cmd.SetValue("", command);
            }

        // Which build owns the entries, so newer builds can take over (see RefreshIfOlder).
        using (var me = Registry.CurrentUser.CreateSubKey(@"Software\SlithyLens"))
        {
            me.SetValue("ExePath", exe);
            me.SetValue("Build", AppInfo.Build);
        }

        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Called at every start. If SlithyLens has been set up (by any build) and the entries point
    /// at a different exe, take them over, unless they belong to a newer build that still exists.
    /// Does nothing if setup was never run, and never touches defaults.
    /// </summary>
    public static void RefreshIfOlder()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            using var me = Registry.CurrentUser.OpenSubKey(@"Software\SlithyLens");
            if (me == null) return;
            using (var caps = me.OpenSubKey("Capabilities"))
                if (caps == null) return;                           // never set up

            string? owner = me.GetValue("ExePath") as string;
            int ownerBuild = me.GetValue("Build") is int b ? b : 0;
            if (string.Equals(owner, exe, StringComparison.OrdinalIgnoreCase)) return;
            if (ownerBuild > AppInfo.Build && owner != null && File.Exists(owner)) return;

            Register();
        }
        catch { /* not important enough to bother the user */ }
    }

    public static void Unregister()
    {
        using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
        {
            if (classes != null)
            {
                classes.DeleteSubKeyTree(@"Applications\SlithyLens.exe", false);
                foreach (var (progId, _, extensions) in Types)
                {
                    classes.DeleteSubKeyTree(progId, false);
                    foreach (string ext in extensions)
                    {
                        classes.DeleteSubKeyTree($@"SystemFileAssociations\{ext}\shell\{EditVerb}", false);
                        using var k = classes.OpenSubKey(ext + @"\OpenWithProgids", writable: true);
                        k?.DeleteValue(progId, false);
                    }
                }
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\SlithyLens", false);
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            reg?.DeleteValue(AppName, false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
