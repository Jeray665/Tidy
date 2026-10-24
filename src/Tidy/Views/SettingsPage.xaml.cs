using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = $"Tidy {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        AutoRam.IsChecked = Store.Settings.AutoRam;
        Reminder.SelectedIndex = Store.Settings.CleanReminderDays switch { 0 => 0, <= 7 => 1, _ => 2 };
        Loaded += async (_, _) => Autostart.IsChecked = await Task.Run(() => Core.Autostart.IsEnabled);
    }

    async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        var enable = Autostart.IsChecked == true;
        Autostart.IsEnabled = false;
        var ok = await Task.Run(() => enable ? Core.Autostart.Enable() : Core.Autostart.Disable());
        Autostart.IsEnabled = true;
        if (!ok) Autostart.IsChecked = !enable;
        Ui.Toast(ok ? "Listo" : "No se pudo cambiar",
            ok ? (enable ? $"Tidy arrancará con Windows desde {Core.Autostart.InstallDir}." : "Tidy ya no arrancará con Windows.") : "Revisa el registro de actividad.",
            ok ? ControlAppearance.Success : ControlAppearance.Danger);
    }

    void AutoRam_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutoRam = AutoRam.IsChecked == true;
        Store.Save();
        Store.Log($"Liberación automática de RAM: {(Store.Settings.AutoRam ? "activada" : "desactivada")}");
    }

    void Reminder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Reminder.SelectedItem is not ComboBoxItem { Tag: string days } || !IsLoaded) return;
        Store.Settings.CleanReminderDays = int.Parse(days);
        Store.Save();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Store.LogPath)) Store.Log("Registro creado.");
        Ui.OpenText(Store.LogPath);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(Store.Dir);

    void Restore_Click(object sender, RoutedEventArgs e) => Ui.RunSystemTool("rstrui.exe");

    void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();
}
