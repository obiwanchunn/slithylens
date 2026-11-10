using System.Windows;
using System.Windows.Threading;

namespace SlithyLens;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Command-line switches for scripted setup:
        //   SlithyLens.exe --register     add SlithyLens to "Open with" for .heic/.heif files
        //   SlithyLens.exe --unregister   remove it again
        if (e.Args.Length > 0 && e.Args[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
        {
            FileAssociation.Register();
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
        {
            FileAssociation.Unregister();
            Shutdown();
            return;
        }

        // If SlithyLens was set up in Open with by an older build, point it at this one.
        FileAssociation.RefreshIfOlder();

        var files = e.Args.Where(a => !a.StartsWith("--")).ToList();
        bool cascade = e.Args.Any(a => a.Equals("--cascade", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(files.FirstOrDefault(), cascade);
        MainWindow = window;
        window.Show();

        // Several files selected in Explorer: each extra one gets its own window.
        foreach (string extra in files.Skip(1))
            SlithyLens.MainWindow.OpenInNewProcess(extra);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "SlithyLens", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
