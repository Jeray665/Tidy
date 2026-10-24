using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public partial class BackupsPage : Page
{
    public BackupsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
    }

    async Task Refresh()
    {
        var latest = await Task.Run(() => { try { return SafetyNet.Latest(); } catch { return null; } });
        RestoreSub.Text = latest is { } d
            ? $"El más reciente es del {d:dd/MM/yyyy HH:mm}. Devuelve Windows (registro, drivers, programas) a ese momento; tus documentos no se tocan."
            : "No hay ninguno todavía. Tidy crea uno automáticamente antes del primer cambio.";

        var tweaks = await Task.Run(Tweaks.Changed);
        var services = await Task.Run(Services.LoadBackup);
        var tasks = await Task.Run(ScheduledTasks.LoadBackup);
        var dns = await Task.Run(() => Network.HasDnsBackup);
        var hosts = await Task.Run(Network.HostsBackups);
        var quarantine = await Task.Run(Uninstaller.Quarantines);

        Sections.Children.Clear();

        Section("Optimizaciones", "Wand24", tweaks.Count == 0 ? "Ninguna optimización aplicada por Tidy." : $"{tweaks.Count} aplicadas: " + string.Join(", ", tweaks.Take(6).Select(t => t.Title)) + (tweaks.Count > 6 ? "…" : ""),
            tweaks.Count > 0 ? ("Deshacer todas", async () =>
            {
                if (!await Ui.Confirm($"¿Deshacer {tweaks.Count} optimizaciones?", Ui.Lines(tweaks.Select(t => "•  " + t.Title)), "Deshacer todas")) return;
                var failed = await Task.Run(() => tweaks.Count(t => { try { Tweaks.Revert(t); return false; } catch { return true; } }));
                Ui.Toast("Optimizaciones deshechas", failed == 0 ? "Cada valor ha vuelto a como estaba." : $"{failed} no se pudieron deshacer; revisa el registro de actividad.",
                    failed == 0 ? ControlAppearance.Success : ControlAppearance.Caution);
            }) : null);

        Section("Servicios", "Settings24", services.Count == 0 ? "Ningún servicio cambiado por Tidy." : $"{services.Count} cambiados: " + string.Join(", ", services.Keys.Take(8)),
            services.Count > 0 ? ("Restaurar todos", async () =>
            {
                var n = await Task.Run(Services.RestoreAll);
                Ui.Toast("Servicios restaurados", $"{n} servicios vuelven a su tipo de inicio original.");
            }) : null);

        Section("Tareas programadas", "CalendarClock24", tasks.Count == 0 ? "Ninguna tarea cambiada por Tidy." : $"{tasks.Count} cambiadas: " + string.Join(", ", tasks.Keys.Take(4).Select(Path.GetFileName)),
            tasks.Count > 0 ? ("Restaurar todas", async () =>
            {
                var n = await Task.Run(ScheduledTasks.RestoreAll);
                Ui.Toast("Tareas restauradas", $"{n} tareas vuelven a su estado original.");
            }) : null);

        Section("DNS", "Globe24", dns ? "Tidy cambió el DNS de alguna conexión. Puedes volver al que tenías." : "DNS sin cambios.",
            dns ? ("Restaurar DNS original", async () =>
            {
                var n = await Task.Run(Network.RestoreDns);
                Ui.Toast("DNS restaurado", $"{n} conexiones restauradas.");
            }) : null);

        // hosts versions
        Sections.Children.Add(new TextBlock { Text = "Versiones del archivo hosts", Style = (Style)FindResource("Section") });
        if (hosts.Count == 0) Sections.Children.Add(Ui.Text("Tidy no ha modificado el archivo hosts.", "Muted", 0));
        foreach (var h in hosts.Take(10))
            Sections.Children.Add(Row("DocumentText24", $"Copia del {h.CreationTime:dd/MM/yyyy HH:mm:ss}", $"{Store.FormatBytes(h.Length)} · guardada antes de un cambio",
                ("Restaurar esta versión", async () =>
                {
                    if (!await Ui.Confirm("¿Restaurar esta versión del archivo hosts?", "La versión actual se guarda también como copia antes de sustituirla.", "Restaurar")) return;
                    await Task.Run(() => Network.WriteHosts(File.ReadAllText(h.FullName)));
                    Ui.Toast("Archivo hosts restaurado", $"Versión del {h.CreationTime:dd/MM HH:mm}.");
                }), null));

        // quarantine
        Sections.Children.Add(new TextBlock { Text = "Cuarentena del desinstalador", Style = (Style)FindResource("Section") });
        Sections.Children.Add(Ui.Text("Restos de programas desinstalados en profundidad. Si algo dejó de funcionar, restáuralo. Cuando lleves un tiempo sin problemas, puedes borrarlos para recuperar el espacio.", "Muted", 8));
        if (quarantine.Count == 0) Sections.Children.Add(Ui.Text("Vacía.", "Muted", 0));
        foreach (var q in quarantine)
            Sections.Children.Add(Row("Delete24", q.App, $"{q.Created:dd/MM/yyyy HH:mm} · {q.Items.Count} elementos · {Store.FormatBytes(q.Bytes)}",
                ("Restaurar", async () =>
                {
                    if (!await Ui.Confirm($"¿Restaurar los restos de {q.App}?", Ui.Lines(q.Items.Select(i => $"•  {i.Target.Replace("|", " › ")}")), "Restaurar")) return;
                    var (ok, failed) = await Task.Run(() => Uninstaller.Restore(q));
                    Ui.Toast(failed == 0 ? "Restaurado" : "Restaurado en parte", failed == 0 ? $"{ok} elementos de {q.App} han vuelto a su sitio." : $"{ok} restaurados, {failed} no (su sitio ya está ocupado). Siguen en la cuarentena.",
                        failed == 0 ? ControlAppearance.Success : ControlAppearance.Caution);
                }),
                ("Borrar definitivamente", async () =>
                {
                    if (!await Ui.Confirm($"¿Borrar para siempre los restos de {q.App}?", $"{q.Items.Count} elementos, {Store.FormatBytes(q.Bytes)}. Esto ya no se puede deshacer.", "Borrar definitivamente", danger: true)) return;
                    await Task.Run(() => Uninstaller.Purge(q));
                    Ui.Toast("Cuarentena borrada", $"Restos de {q.App} eliminados.");
                })));

        // cleaner lists
        var lists = Path.Combine(Store.Dir, "limpiezas");
        Sections.Children.Add(new TextBlock { Text = "Historial", Style = (Style)FindResource("Section") });
        Sections.Children.Add(Row("Broom24", "Listas de archivos limpiados", "Cada limpieza guarda la lista exacta de lo que borró (temporales y cachés: no se pueden restaurar porque Windows y las apps los regeneran).",
            ("Abrir carpeta", () => { Directory.CreateDirectory(lists); Ui.OpenFolder(lists); return Task.CompletedTask; }), null));
        Sections.Children.Add(Row("DocumentText24", "Registro de actividad", "Todo lo que Tidy ha hecho, con fecha y hora.",
            ("Abrir", () => { if (!File.Exists(Store.LogPath)) Store.Log("Registro creado."); Ui.OpenText(Store.LogPath); return Task.CompletedTask; }), null));
    }

    void Section(string title, string icon, string detail, (string, Func<Task>)? action)
    {
        Sections.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Section") });
        Sections.Children.Add(Row(icon, title, detail, action, null));
    }

    FrameworkElement Row(string icon, string title, string detail, (string Label, Func<Task> Run)? a, (string Label, Func<Task> Run)? b)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Ui.Text(detail, "Muted", 0));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (act, i) in new[] { a, b }.Select((x, i) => (x, i)).Where(x => x.x != null))
        {
            var btn = new Wpf.Ui.Controls.Button { Content = act!.Value.Label, Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0), Appearance = i > 0 ? ControlAppearance.Danger : ControlAppearance.Secondary };
            btn.Click += async (_, _) => { btn.IsEnabled = false; try { await act.Value.Run(); } finally { await Refresh(); } };
            buttons.Children.Add(btn);
        }
        return new CardControl { Icon = Ui.Icon(icon, 22), Header = header, Content = buttons, Margin = new Thickness(0, 0, 0, 6) };
    }

    void Rstrui_Click(object sender, RoutedEventArgs e) => Ui.RunSystemTool("rstrui.exe");

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"tidy-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.json", Filter = "Configuración de Tidy|*.json" };
        if (dlg.ShowDialog() != true) return;
        var profile = Tweaks.Export();
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        Ui.Toast("Configuración exportada", $"{profile.Tweaks.Count} optimizaciones guardadas en {Path.GetFileName(dlg.FileName)}.");
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Configuración de Tidy|*.json" };
        if (dlg.ShowDialog() != true) return;
        Tweaks.Profile? profile;
        try { profile = JsonSerializer.Deserialize<Tweaks.Profile>(File.ReadAllText(dlg.FileName)); }
        catch { profile = null; }
        if (profile == null) { Ui.Toast("Archivo no válido", "No es una configuración de Tidy.", ControlAppearance.Danger); return; }
        var pending = await Task.Run(() => Tweaks.FromProfile(profile));
        if (pending.Count == 0) { Ui.Toast("Nada que aplicar", "Este PC ya tiene todo lo de ese archivo."); return; }
        var panel = new StackPanel { Width = 680 };
        panel.Children.Add(Ui.Text($"Archivo creado el {profile.Created:dd/MM/yyyy}. Se aplicarán estas optimizaciones que este PC aún no tiene:", null, 4));
        panel.Children.Add(Ui.Lines(pending.Select(t => $"{(t.Risk == Risk.Advanced ? "⚠ " : "•  ")}{t.Title}")));
        if (!await Ui.Confirm($"¿Importar {pending.Count} optimizaciones?", panel, "Aplicar")) return;
        if (!await Ui.EnsureRestorePoint("Importar configuración")) return;
        var failed = await Task.Run(() => pending.Count(t => { try { Tweaks.Apply(t); return false; } catch { return true; } }));
        Ui.Toast("Configuración importada", failed == 0 ? $"{pending.Count} optimizaciones aplicadas." : $"{pending.Count - failed} aplicadas, {failed} no.",
            failed == 0 ? ControlAppearance.Success : ControlAppearance.Caution);
        await Refresh();
    }
}
