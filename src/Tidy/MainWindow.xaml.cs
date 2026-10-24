using System.Windows;
using Tidy.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Tidy;

public partial class MainWindow : FluentWindow
{
    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => Nav.Navigate(typeof(Views.HomePage));
        Closed += (_, _) =>
        {
            // The big window is fully destroyed on close; only the tray stays. Give its memory back.
            Current = null;
            Dispatcher.BeginInvoke(Memory.TrimSelf, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    public void Navigate(Type page) => Nav.Navigate(page);
}
