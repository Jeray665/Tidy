using System.Windows;
using System.Windows.Threading;
using Tidy.Core;

namespace Tidy;

public partial class App : Application
{
    Mutex? _single;
    Tray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnCrash;
        var uninstall = e.Args.Contains("--uninstall");
#if DEBUG
        var setup = uninstall;   // dev and preview builds run straight from the build folder
#else
        var setup = uninstall || (!Install.IsPortable && !Install.IsInstalled);
#endif
        if (Install.IsPortable) Install.UsePortablePaths();
        if (setup)
        {
            Store.Load();
            I18n.Init(Store.Settings.Language);
            Theme.Init();
            new SetupWindow(uninstall).Show();
            return;
        }

        _single = new Mutex(true, @"Local\Tidy.Single", out var first);
        // right after an update the old process may still be closing
        for (var n = 0; !first && e.Args.Contains("--updated") && n < 50; n++)
        {
            Thread.Sleep(200);
            _single.Dispose();
            _single = new Mutex(true, @"Local\Tidy.Single", out first);
        }
        if (!first)
        {
            // already running (probably in the tray): ask it to show its window and leave
            try { EventWaitHandle.OpenExisting(@"Local\Tidy.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Tidy.Show");
        new Thread(() => { while (show.WaitOne()) Dispatcher.BeginInvoke(() => ShowMain()); }) { IsBackground = true }.Start();
        // the installer asks a running Tidy to close before replacing its exe
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Tidy.Quit");
        new Thread(() => { if (quit.WaitOne()) Dispatcher.BeginInvoke(Quit); }) { IsBackground = true }.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, a) => Store.Log($"FATAL ERROR: {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => { Store.Log($"TASK ERROR: {a.Exception}"); a.SetObserved(); };

        Store.Load();
        Store.MigrateFolders();
        Install.CleanOldExe();
        Store.CleanDownloads();
        if (Install.IsInstalled) try { Install.Register(); } catch (Exception ex) { Store.Log($"Register failed: {ex.Message}"); }
        // "what's new" shows once per version; a fresh install has nothing to catch up on, and 1.0.0 didn't record it
        if (Store.Settings.LastSeenVersion == null)
        {
            Store.Settings.LastSeenVersion = Store.FirstRun ? Updater.Current.ToString() : "1.0.0";
            Store.Save();
        }
        I18n.Init(Store.Settings.Language);
        Theme.Init();
        Live.Start();
        // logging off or shutting down mid-game: give the user's power plan back, it would otherwise outlive the reboot
        SessionEnding += (_, _) => GameMode.Stop();
        _tray = new Tray(this);
        Updater.StartAuto();
        if (e.Args.Contains("--updated") && e.Args.Contains("--tray"))
            _tray.Balloon(typeof(Views.HomePage), $"Tidy se ha actualizado a la versión {Updater.Current}. Haz clic para ver las novedades.");
        Store.Log($"Tidy {typeof(App).Assembly.GetName().Version} started ({(e.Args.Contains("--tray") ? "tray" : "window")}).");

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

    /// <summary>After an update or a language change: hand over to a new process, in the tray if the window wasn't open.</summary>
    public void Restart(Type? page = null)
    {
        var args = Tidy.MainWindow.Current == null ? "--updated --tray" : page != null ? $"--updated --page {page.Name}" : "--updated";
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Install.Current, args) { UseShellExecute = false });
        Quit();
    }

    public void Quit()
    {
        Store.Log("Tidy closed by the user.");
        GameMode.Stop(); // quitting mid-game puts the plan and priorities back, same as closing the game
        _tray?.Dispose();
        Shutdown();
    }
}
