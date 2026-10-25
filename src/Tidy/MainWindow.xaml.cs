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
