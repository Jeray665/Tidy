using System.Windows;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy;

public partial class MainWindow : FluentWindow
{
    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        Theme.Attach(this);
        // the navigation frame cuts color inheritance: without this, text with no color of its own is black on the dark theme
        Nav.Navigated += (_, e) => (e.Page as FrameworkElement)?.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        Loaded += (_, _) =>
        {
            Nav.Navigate(typeof(Views.HomePage));
            Dispatcher.BeginInvoke(ShowWhatsNew, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        ShowUpdate(Updater.Available);
        Updater.Found += OnFound;
        Closed += (_, _) =>
        {
            Updater.Found -= OnFound;
            // The big window is fully destroyed on close; only the tray stays. Give its memory back.
            Current = null;
            Dispatcher.BeginInvoke(Memory.TrimSelf, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    public void Navigate(Type page) => Nav.Navigate(page);

    void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Toasts.Pause((Toast)((FrameworkElement)sender).DataContext);
    void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => Toasts.Restart((Toast)((FrameworkElement)sender).DataContext);
    void ToastClose_Click(object sender, RoutedEventArgs e) => Toasts.Close((Toast)((FrameworkElement)sender).DataContext);

    void OnFound(Release r) => Dispatcher.BeginInvoke(() => ShowUpdate(r));

    void ShowUpdate(Release? r)
    {
        UpdateItem.Visibility = r != null ? Visibility.Visible : Visibility.Collapsed;
        if (r != null) UpdateItem.Content = $"Nueva actualización ({r.Version})";
    }

    /// <summary>Once after each update, over the main screen: what changed since the version the user had.</summary>
    async void ShowWhatsNew()
    {
        if (!Version.TryParse(Store.Settings.LastSeenVersion, out var seen) || seen >= Updater.Current) return;
        Store.Settings.LastSeenVersion = Updater.Current.ToString();
        Store.Save();
        var news = Ui.Changelog().Where(c => c.Version > seen && c.Version <= Updater.Current).ToList();
        if (news.Count > 0) await Ui.Info($"Novedades de Tidy {Updater.Current}", Ui.ChangeList(news));
    }
}
