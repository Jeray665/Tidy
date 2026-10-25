using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tidy.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>Light/dark choice (follow Windows, light or dark) and the matching logo: the light one by default,
/// the dark one while the app is dark. XAML shows the logo through {DynamicResource AppLogo}.</summary>
public static class Theme
{
    static readonly List<FluentWindow> _windows = [];
    public static event Action? Changed;
    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
    public static string LogoName => IsDark ? "tidy-dark" : "tidy";

    public static void Init()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Store.Settings.Theme == "system")
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void Apply()
    {
        switch (Store.Settings.Theme)
        {
            case "light": ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true); break;
            case "dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true); break;
            default: ApplicationThemeManager.ApplySystemTheme(true); break;
        }
        Application.Current.Resources["AppLogo"] = Image($"{LogoName}.png");
        foreach (var w in _windows) Paint(w);
        Changed?.Invoke();
    }

    /// <summary>Replaces SystemThemeWatcher: the window follows Tidy's choice, not always Windows'.</summary>
    public static void Attach(FluentWindow w)
    {
        _windows.Add(w);
        w.Closed += (_, _) => _windows.Remove(w);
        w.SourceInitialized += (_, _) => Paint(w);
        Paint(w);
    }

    static void Paint(FluentWindow w)
    {
        w.Icon = Image($"{LogoName}.ico");
        if (w.IsLoaded || new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero)
            WindowBackgroundManager.UpdateBackground(w, ApplicationThemeManager.GetAppTheme(), w.WindowBackdropType);
    }

    static BitmapFrame Image(string file) => BitmapFrame.Create(new Uri($"pack://application:,,,/Assets/{file}"));

    public static System.Drawing.Icon TrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{LogoName}.ico")).Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
