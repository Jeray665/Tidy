using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy;

/// <summary>Minimal INotifyPropertyChanged for list rows.</summary>
public abstract class Observable : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Refresh every binding on this row.</summary>
    protected void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
}

/// <summary>Dialogs, toasts and tiny view helpers shared by all pages.</summary>
public static class Ui
{
    /// <summary>Long lists (apps, processes, services…) stretch down to the bottom of the window instead of a fixed
    /// height, leaving this much room below for the buttons under them. They keep their own scroll, so thousands of
    /// rows stay fast; on a small window they keep 360 px and the page scrolls as before.</summary>
    public static readonly DependencyProperty FillBelowProperty = DependencyProperty.RegisterAttached("FillBelow", typeof(double), typeof(Ui),
        new PropertyMetadata(-1.0, (d, _) =>
        {
            if (d is not FrameworkElement list) return;
            // a list without a height measures every row (4,000 downloads = minutes frozen): start bounded, Fit grows it
            if (double.IsNaN(list.Height)) list.Height = 360;
            void Fit(object? s, EventArgs e)
            {
                if (!list.IsVisible) return;
                DependencyObject? p = VisualTreeHelper.GetParent(list);
                while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
                if (p is not ScrollViewer page) return;
                var top = list.TranslatePoint(new Point(0, 0), page).Y + page.VerticalOffset;
                var h = Math.Max(360, page.ViewportHeight - top - GetFillBelow(list) - 36); // 36: the pages' bottom margin
                if (Math.Abs(list.Height - h) > 1 || double.IsNaN(list.Height)) list.Height = h;
            }
            list.Loaded += (_, _) => list.LayoutUpdated += Fit;
            list.Unloaded += (_, _) => list.LayoutUpdated -= Fit;
        }));
    public static double GetFillBelow(DependencyObject d) => (double)d.GetValue(FillBelowProperty);
    public static void SetFillBelow(DependencyObject d, double v) => d.SetValue(FillBelowProperty, v);

    public static async Task<bool> Confirm(string title, object content, string primary, bool danger = false, string close = "Cancelar")
    {
        var w = MainWindow.Current;
        if (w == null) return false;
        var dialog = new ContentDialog(w.DialogHost)
        {
            Title = title,
            Content = content is string s ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap } : content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close, // Enter never confirms a destructive action by accident
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DialogMaxWidth = 820,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Restore point before system changes. If Windows can't make one, the user decides; nothing proceeds silently.</summary>
    public static async Task<bool> EnsureRestorePoint(string what)
    {
        var r = await Task.Run(() => Core.SafetyNet.EnsureRestorePoint(what));
        if (r.Ok) return true;
        if (await Confirm("No se pudo crear un punto de restauración", $"{r.Message}\n\n¿Activo la Protección del sistema en C: y lo intento de nuevo? (Es lo que usa Windows para \"Restaurar sistema\".)", "Activar y reintentar"))
        {
            await Task.Run(Core.SafetyNet.EnableProtection);
            r = await Task.Run(() => Core.SafetyNet.EnsureRestorePoint(what));
            if (r.Ok) { Toast("Punto de restauración creado", r.Message); return true; }
        }
        return await Confirm("¿Continuar sin punto de restauración?",
            "Tidy guarda igualmente una copia de cada valor que cambia para poder deshacerlo, pero sin punto de restauración no podrás usar \"Restaurar sistema\" de Windows.",
            "Continuar sin él", danger: true);
    }

    public static void Toast(string title, string message, ControlAppearance appearance = ControlAppearance.Success)
    {
        if (MainWindow.Current == null) return;
        Toasts.Show(title, message, appearance);
    }

    public static TextBlock Text(string text, string? style = null, double bottom = 8)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };
        if (style != null) t.Style = (Style)Application.Current.FindResource(style);
        return t;
    }

    /// <summary>Feeds live readings to a view only while it's on screen (1 s rate); off-screen it costs nothing.</summary>
    public static void LiveBind(FrameworkElement el, Action<Core.Snapshot> update)
    {
        void Handler(Core.Snapshot s) => el.Dispatcher.BeginInvoke(() => update(s));
        el.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Core.Live.Updated += Handler;
                Core.Live.Watch(true);
                if (Core.Live.Last is { } last) update(last);
            }
            else
            {
                Core.Live.Updated -= Handler;
                Core.Live.Watch(false);
            }
        };
    }

    public static Brush Health(double pctBad) => Res(pctBad >= 90 ? "SystemFillColorCriticalBrush" : pctBad >= 75 ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush");

    // We run elevated: never ShellExecute by file association (HKCU associations could hijack it). Absolute System32 tools only.
    public static void OpenText(string path) =>
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"), $"\"{path}\"") { UseShellExecute = false });

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{path}\"") { UseShellExecute = false });

    public static void RunSystemTool(string exe) =>
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, exe)) { UseShellExecute = false });

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Two choices plus Cancel. Cancel is the default button.</summary>
    public static async Task<ContentDialogResult> Ask(string title, object content, string primary, string secondary, bool danger = false)
    {
        var w = MainWindow.Current;
        if (w == null) return ContentDialogResult.None;
        return await new ContentDialog(w.DialogHost)
        {
            Title = title, Content = content is string s ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap } : content,
            PrimaryButtonText = primary, SecondaryButtonText = secondary, CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close, DialogMaxWidth = 820,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
        }.ShowAsync();
    }

    public static async Task Info(string title, object content)
    {
        var w = MainWindow.Current;
        if (w == null) return;
        await new ContentDialog(w.DialogHost)
        {
            Title = title, Content = content is string s ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap } : content,
            CloseButtonText = "Cerrar", DialogMaxWidth = 820,
        }.ShowAsync();
    }

    public static Border Pill(string text, bool danger = false, bool accent = false) => new()
    {
        Style = (Style)Application.Current.FindResource("Pill"),
        Background = Res(danger ? "SystemFillColorCriticalBackgroundBrush" : accent ? "SystemFillColorSuccessBackgroundBrush" : "ControlFillColorSecondaryBrush"),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res(danger ? "SystemFillColorCriticalBrush" : accent ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush") },
    };

    public static SymbolIcon Icon(string name, double size = 20) =>
        new(Enum.TryParse<SymbolRegular>(name, out var s) ? s : SymbolRegular.Settings24) { FontSize = size };

    /// <summary>A scrollable list of plain text lines for confirmation dialogs.</summary>
    public static FrameworkElement Lines(IEnumerable<string> lines, double height = 260) => new ListBox
    {
        Height = height, Margin = new Thickness(0, 8, 0, 8), ItemsSource = lines.ToList(),
        Style = (Style)Application.Current.FindResource("RowList"),
    };

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = false });

    /// <summary>ms-settings: pages through Explorer (the un-elevated shell resolves the protocol; we never ShellExecute it ourselves).</summary>
    public static void OpenSettings(string uri) => OpenFolder(uri);

    public static Brush Accent => Res("AccentTextFillColorPrimaryBrush");

    /// <summary>Web pages open in the user's normal (non-admin) browser through Explorer, never elevated.</summary>
    public static void OpenUrl(string url) => OpenFolder(url);

    public static List<(Version Version, List<string> Items)> Changelog()
    {
        using var r = new System.IO.StreamReader(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CHANGELOG.md")).Stream);
        return Core.Changelog.Parse(r.ReadToEnd());
    }

    /// <summary>Changelog sections as headed bullet lists, for the "what's new" dialogs.</summary>
    public static FrameworkElement ChangeList(IEnumerable<(Version Version, List<string> Items)> versions)
    {
        var panel = new StackPanel { MaxWidth = 640 };
        foreach (var (v, items) in versions)
        {
            panel.Children.Add(new TextBlock { Text = $"Versión {v}", FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 6) });
            foreach (var i in items)
                panel.Children.Add(new TextBlock { Text = "•  " + i, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5), LineHeight = 20 });
        }
        return new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? FileIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            // .exe only: their icon is a plain resource. Other types would go through shell icon handlers.
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                }
            }
        }
        catch { }
        return _icons[path] = img;
    }
}
