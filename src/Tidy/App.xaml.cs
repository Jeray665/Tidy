using System.Windows;
using System.Windows.Threading;
using Tidy.Core;
using Wpf.Ui.Appearance;

namespace Tidy;

public partial class App : Application
{
    Mutex? _single;
    Tray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\Tidy.Single", out var first);
        if (!first)
        {
            // already running (probably in the tray): ask it to show its window and leave
            try { EventWaitHandle.OpenExisting(@"Local\Tidy.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Tidy.Show");
        new Thread(() => { while (show.WaitOne()) Dispatcher.BeginInvoke(() => ShowMain()); }) { IsBackground = true }.Start();

        DispatcherUnhandledException += OnCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Store.Log($"ERROR fatal: {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => { Store.Log($"ERROR tarea: {a.Exception}"); a.SetObserved(); };

        Store.Load();
        ApplicationThemeManager.ApplySystemTheme();
        Live.Start();
        _tray = new Tray(this);
        Store.Log($"Tidy {typeof(App).Assembly.GetName().Version} iniciado ({(e.Args.Contains("--tray") ? "bandeja" : "ventana")}).");

        // --page StoragePage opens straight on that page (shortcuts, and screenshots without clicking)
        var i = Array.IndexOf(e.Args, "--page");
        var page = i >= 0 && i + 1 < e.Args.Length ? typeof(App).Assembly.GetType($"Tidy.Views.{e.Args[i + 1]}") : null;
        if (!e.Args.Contains("--tray")) ShowMain(page?.IsSubclassOf(typeof(System.Windows.Controls.Page)) == true ? page : null);
    }

    void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Every action is atomic per item (backup first, then write), so an unexpected error never leaves things half-done:
        // log it, tell the user, keep running.
        Store.Log($"ERROR: {e.Exception}");
        e.Handled = true;
        Ui.Toast("Algo ha fallado", e.Exception.Message, Wpf.Ui.Controls.ControlAppearance.Danger);
    }

    public void ShowMain(Type? page = null)
    {
        var w = Tidy.MainWindow.Current ?? new Tidy.MainWindow();
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        if (page != null) w.Navigate(page);
    }

    public void Quit()
    {
        Store.Log("Tidy cerrado por el usuario.");
        _tray?.Dispose();
        Shutdown();
    }
}
