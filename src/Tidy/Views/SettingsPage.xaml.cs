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
        VersionText.Text = $"Tidy {Updater.Current} · {(Install.IsPortable ? "versión portátil" : "instalado")} · software libre (GPL-3.0)";
        AutoUpdate.IsChecked = Store.Settings.AutoUpdateCheck;
        ShowUpdateState();
        ShowRetired();
        if (Install.IsPortable)
        {
            Autostart.IsEnabled = false;
            Autostart.ToolTip = "La versión portátil no arranca con Windows: instala Tidy para usarlo.";
        }
        Updater.Found += OnFound;
        Unloaded += (_, _) => Updater.Found -= OnFound;
        AutoRam.IsChecked = Store.Settings.AutoRam;
        Reminder.SelectedIndex = Store.Settings.CleanReminderDays switch { 0 => 0, <= 7 => 1, _ => 2 };
        ThemeBox.SelectedIndex = Store.Settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageBox.SelectedIndex = Store.Settings.Language switch { "es" => 1, "en" => 2, _ => 0 };
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
        Store.Log($"Automatic RAM release: {(Store.Settings.AutoRam ? "on" : "off")}");
    }

    void Reminder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Reminder.SelectedItem is not ComboBoxItem { Tag: string days } || !IsLoaded) return;
        Store.Settings.CleanReminderDays = int.Parse(days);
        Store.Save();
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme } || !IsLoaded) return;
        Store.Settings.Theme = theme;
        Store.Save();
        Theme.Apply();
    }

    async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string lang } || !IsLoaded) return;
        var changes = I18n.Resolve(lang) != I18n.Resolve(Store.Settings.Language);
        Store.Settings.Language = lang;
        Store.Save();
        if (changes && await Ui.Confirm("Cambiar idioma", "Tidy se reinicia para cambiar el idioma. No se pierde nada.", "Reiniciar ahora", close: "Más tarde"))
            ((App)Application.Current).Restart(typeof(SettingsPage));
    }

    void OnFound(Release r) => Dispatcher.BeginInvoke(ShowUpdateState);

    void ShowUpdateState()
    {
        var r = Updater.Available;
        UpdateBtn.Visibility = r != null ? Visibility.Visible : Visibility.Collapsed;
        if (r != null) UpdateBtn.Content = $"Actualizar a {r.Version}";
        UpdateStatus.Text = r != null ? $"Hay una versión nueva: {r.Version}. Tus ajustes, historial y copias se conservan al actualizar."
            : Store.Settings.LastUpdateCheck is { } last ? $"Estás al día · comprobado el {last:dd/MM/yyyy HH:mm}"
            : "Optimizador seguro para Windows 11";
    }

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckBtn.IsEnabled = false;
        UpdateStatus.Text = "Buscando en GitHub…";
        try
        {
            var r = await Updater.Check();
            ShowUpdateState();
            if (r == null) Ui.Toast("Estás al día", $"Tidy {Updater.Current} es la última versión.");
        }
        catch (Exception ex)
        {
            Store.Log($"Update check failed: {ex.Message}");
            UpdateStatus.Text = "No se pudo conectar con GitHub. Revisa la conexión a Internet.";
        }
        finally { CheckBtn.IsEnabled = true; }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } r) return;
        var content = new StackPanel { MaxWidth = 640 };
        content.Children.Add(Ui.Text("Tidy se descarga de su página de GitHub y solo se instala si el archivo es exactamente el publicado (mismo tamaño y huella SHA-256). Luego se cierra y vuelve a abrirse ya actualizado.\n\nSe conservan tus ajustes, el historial, las copias de seguridad y lo que tengas optimizado."));
        if (!string.IsNullOrWhiteSpace(r.Notes))
        {
            content.Children.Add(Ui.Text("Qué trae:", null, 4));
            content.Children.Add(new ScrollViewer { MaxHeight = 220, Content = Ui.Text(r.Notes.Trim(), "Muted", 0) });
        }
        if (!await Ui.Confirm($"Actualizar a Tidy {r.Version}", content, "Descargar y actualizar")) return;

        UpdateBtn.IsEnabled = CheckBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatus.Text = "Descargando…";
        try
        {
            var file = await Updater.Download(r, new Progress<double>(p => UpdateProgress.Value = p));
            UpdateStatus.Text = "Comprobado. Instalando…";
            await Task.Run(() => Updater.Apply(file));
            ((App)Application.Current).Restart();
        }
        catch (Exception ex)
        {
            Store.Log($"Update failed: {ex}");
            Ui.Toast("No se pudo actualizar", ex.Message + " No se ha cambiado nada.", ControlAppearance.Danger);
            ShowUpdateState();
        }
        finally { UpdateBtn.IsEnabled = CheckBtn.IsEnabled = true; UpdateProgress.Visibility = Visibility.Collapsed; }
    }

    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutoUpdateCheck = AutoUpdate.IsChecked == true;
        Store.Save();
    }

    async void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        await Ui.Info($"Novedades de Tidy", Ui.ChangeList(Ui.Changelog()));

    void GitHub_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(Install.Repo);

    void ShowRetired()
    {
        RetiredList.Children.Clear();
        var applied = Tweaks.Retired.Where(Tweaks.IsApplied).ToList();
        RetiredBox.Visibility = applied.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in applied)
        {
            var reset = new Wpf.Ui.Controls.Button { Content = "Restablecer", Icon = new SymbolIcon(SymbolRegular.ArrowUndo24) };
            reset.Click += async (_, _) =>
            {
                if (!await Ui.Confirm($"¿Restablecer \"{t.Title}\"?", "Se devuelve el valor que había antes de que Tidy lo cambiara.", "Restablecer")) return;
                await Task.Run(() => Tweaks.Revert(t));
                Ui.Toast("Restablecido", t.Title);
                ShowRetired();
            };
            RetiredList.Children.Add(new CardControl { Header = t.Title, Content = reset, Margin = new Thickness(0, 0, 0, 6), Icon = new SymbolIcon(SymbolRegular.History24) });
        }
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Store.LogPath)) Store.Log("Log created.");
        Ui.OpenText(Store.LogPath);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(Store.Dir);

    void Restore_Click(object sender, RoutedEventArgs e) => Ui.RunSystemTool("rstrui.exe");

    void Quit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();
}
