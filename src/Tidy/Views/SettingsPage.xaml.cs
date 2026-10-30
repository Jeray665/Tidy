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
        VersionText.Text = $"Tidy {Updater.Current} · {(Install.IsPortable ? "versión portátil" : "instalado")} · software libre (GPL-3.0) · © 2026 Jeray665";
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
        ShowClean();
        ThemeBox.SelectedIndex = Store.Settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Store.Settings.Language) ?? LanguageBox.Items[0];
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

    // ---------- scheduled cleanup ----------
    bool _showingClean;
    readonly List<(System.Windows.Controls.CheckBox Box, CleanCategory Category)> _cleanBoxes = [];

    void ShowClean()
    {
        _showingClean = true;
        var s = Store.Settings;
        var mode = s.CleanReminderDays <= 0 ? 0 : s.AutoClean ? 2 : 1;
        CleanMode.SelectedIndex = mode;
        CleanEvery.SelectedIndex = s.CleanReminderDays switch { 1 => 0, > 1 and <= 7 => 1, > 7 and <= 14 => 2, _ => 3 };
        CleanOptions.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        CleanModeHint.Text = mode switch
        {
            0 => "No te avisa ni limpia por su cuenta. Puedes limpiar cuando quieras desde Limpieza.",
            1 => "Te avisa con una notificación si hay más de 300 MB que liberar. Nunca borra nada sin enseñarte la lista.",
            _ => "Limpia sin preguntar solo lo que marques aquí y te avisa de lo que liberó. Cada vez guarda la lista exacta de lo borrado.",
        };
        CleanCats.Children.Clear();
        _cleanBoxes.Clear();
        var chosen = CleanReminder.Scheduled(auto: false);
        foreach (var c in Cleaner.Categories)
        {
            var locked = mode == 2 && c.RecycleBin; // your own files are in it: never emptied without the list in front of you
            var box = new System.Windows.Controls.CheckBox
            {
                Content = locked ? $"{c.Name} (nunca sola)" : c.Name, IsChecked = !locked && chosen.Contains(c), IsEnabled = !locked,
                ToolTip = locked ? "La papelera nunca se vacía sola: ahí están tus archivos. Vacíala desde Limpieza, viendo la lista." : c.Description,
                Margin = new Thickness(0, 0, 18, 6),
            };
            box.Click += CleanCategory_Click;
            _cleanBoxes.Add((box, c));
            CleanCats.Children.Add(box);
        }
        var last = new[] { s.LastClean, s.LastCleanReminder }.Max();
        var next = last?.AddDays(Math.Max(1, s.CleanReminderDays));
        // translated piece by piece: the joined sentence isn't a known text
        CleanNext.Text = I18n.T(next is { } n && n > DateTime.Now ? $"Próxima vez: a partir del {n:dd/MM/yyyy}." : "Próxima vez: cuando Tidy lleve una hora abierto.")
                         + " " + I18n.T("Nunca mientras juegas ni nada más encender.");
        _showingClean = false;
    }

    async void CleanMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingClean || !IsLoaded) return;
        var s = Store.Settings;
        var mode = CleanMode.SelectedIndex;
        if (mode == 2 && !s.AutoClean && !await ConfirmAutoClean(CleanReminder.Scheduled(auto: true))) { ShowClean(); return; }
        s.AutoClean = mode == 2;
        s.CleanReminderDays = mode == 0 ? 0 : s.CleanReminderDays > 0 ? s.CleanReminderDays : 30;
        Store.Save();
        Store.Log($"Scheduled cleanup: {(mode == 0 ? "off" : mode == 1 ? "remind" : "automatic")}, every {s.CleanReminderDays} days");
        ShowClean();
    }

    void CleanEvery_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingClean || !IsLoaded || CleanEvery.SelectedItem is not ComboBoxItem { Tag: string days }) return;
        Store.Settings.CleanReminderDays = int.Parse(days);
        Store.Save();
        ShowClean();
    }

    async void CleanCategory_Click(object sender, RoutedEventArgs e)
    {
        var (box, category) = _cleanBoxes.First(b => b.Box == sender);
        // adding something to what gets deleted without asking deserves the same confirmation as turning it on
        if (box.IsChecked == true && Store.Settings.AutoClean && !await ConfirmAutoClean([category])) { box.IsChecked = false; return; }
        var ids = _cleanBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Category.Id).ToList();
        // the recycle bin box is locked while cleaning by itself: keep whatever the reminder had
        if (_cleanBoxes.FirstOrDefault(b => !b.Box.IsEnabled) is { Category: var locked } && CleanReminder.Scheduled(auto: false).Contains(locked)) ids.Add(locked.Id);
        Store.Settings.CleanCategories = ids;
        Store.Save();
    }

    static async Task<bool> ConfirmAutoClean(List<CleanCategory> cats)
    {
        if (cats.Count == 0) { Ui.Toast("Marca qué limpiar", "Elige al menos una cosa en \"Qué incluye\" antes de dejar que Tidy limpie solo.", ControlAppearance.Caution); return false; }
        var content = new StackPanel { Width = 640 };
        content.Children.Add(Ui.Text("Tidy borrará esto sin preguntarte cada vez, solo de estas carpetas:", null, 6));
        foreach (var c in cats)
        {
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = c.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            content.Children.Add(Ui.Text(c.Description, "Muted", 4));
        }
        content.Children.Add(new Border { Height = 10 });
        content.Children.Add(Ui.Text("Cada vez guarda la lista exacta de lo borrado (Copias de seguridad › Listas de archivos limpiados) y te avisa con una notificación. Nunca mientras juegas, nunca la papelera, y puedes volver a \"Avisarme\" cuando quieras.", "Muted", 0));
        return await Ui.Confirm("¿Dejar que Tidy limpie solo?", content, "Sí, limpiar solo");
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
        UpdateBtn.IsEnabled = CheckBtn.IsEnabled = false;
        try
        {
            await RunUpdate(new Progress<double>(p =>
            {
                UpdateProgress.Visibility = Visibility.Visible;
                UpdateProgress.Value = p;
                UpdateStatus.Text = p < 1 ? "Descargando…" : "Comprobando e instalando…";
            }));
            ShowUpdateState();
        }
        finally { UpdateBtn.IsEnabled = CheckBtn.IsEnabled = true; UpdateProgress.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Asks first, then downloads, verifies, installs and restarts Tidy. Shared by Settings and the banner
    /// at the top of the window. Returns only if the user said no or something failed (nothing is changed then).</summary>
    internal static async Task RunUpdate(IProgress<double> progress)
    {
        if (Updater.Available is not { } r) return;
        var content = new StackPanel { MaxWidth = 640 };
        content.Children.Add(Ui.Text("Tidy se descarga de su página de GitHub y solo se instala si el archivo es exactamente el publicado (mismo tamaño y huella SHA-256). Luego se cierra y vuelve a abrirse ya actualizado.\n\nSe conservan tus ajustes, el historial, las copias de seguridad y lo que tengas optimizado."));
        var notes = Changelog.NotesFor(r.Notes, I18n.Lang == "es");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            content.Children.Add(Ui.Text("Qué trae:", null, 4));
            content.Children.Add(new ScrollViewer { MaxHeight = 220, Content = Ui.Text(notes, "Muted", 0) });
        }
        if (!await Ui.Confirm($"Actualizar a Tidy {r.Version}", content, "Descargar y actualizar")) return;

        try
        {
            progress.Report(0);
            var file = await Updater.Download(r, progress);
            await Task.Run(() => Updater.Apply(file));
            ((App)Application.Current).Restart();
        }
        catch (Exception ex)
        {
            Store.Log($"Update failed: {ex}");
            Ui.Toast("No se pudo actualizar", ex.Message + " No se ha cambiado nada.", ControlAppearance.Danger);
        }
    }

    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutoUpdateCheck = AutoUpdate.IsChecked == true;
        Store.Save();
    }

    async void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        await Ui.Info($"Novedades de Tidy", Ui.ChangeList(Ui.Changelog()));

    void GitHub_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(Install.Repo);
    void Privacy_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl($"{Install.Repo}/blob/main/PRIVACY.md{(I18n.Lang == "es" ? "#privacidad" : "")}");
    void Licenses_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl($"{Install.Repo}/blob/main/THIRD-PARTY-NOTICES.md");

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
