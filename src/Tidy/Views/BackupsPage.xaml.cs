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
        var hosts = await Task.Run(() => { try { return Network.HostsTimeline(); } catch { return []; } });
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
        Sections.Children.Add(new TextBlock { Text = "Copias del archivo hosts", Style = (Style)FindResource("Section") });
        if (hosts.Count == 0) Sections.Children.Add(Ui.Text("Tidy no ha modificado el archivo hosts.", "Muted", 0));
        else Sections.Children.Add(Ui.Text("Antes de cada cambio del archivo hosts, Tidy guarda cómo estaba. Restaurar una copia deshace todos los cambios que vinieron después: pulsa \"Qué cambia\" para ver la lista.", "Muted", 8));
        foreach (var (h, change) in Enumerable.Reverse(hosts))
            Sections.Children.Add(Row("DocumentText24", $"Copia del {Network.HostsCopyTime(h):dd/MM/yyyy HH:mm:ss}",
                $"Guardada antes de: {change.Title} · {Store.FormatBytes(h.Length)}",
                ("Qué cambia", async () => await Ui.Info($"Restaurar la copia del {Network.HostsCopyTime(h):dd/MM/yyyy HH:mm:ss}", await HostsChanges(h))),
                ("Restaurar", async () =>
                {
                    if (!await Ui.Confirm($"¿Restaurar la copia del {Network.HostsCopyTime(h):dd/MM/yyyy HH:mm:ss}?", await HostsChanges(h), "Restaurar")) return;
                    var changed = await Task.Run(() => Network.RestoreHosts(h));
                    Ui.Toast(changed ? "Archivo hosts restaurado" : "Sin cambios", changed ? "Tu versión de antes se ha guardado como copia nueva." : "El archivo hosts ya era igual que esa copia.");
                }), ControlAppearance.Secondary));
        if (hosts.Count > 0)
            Sections.Children.Add(Row("Delete24", "Borrar copias", "Eliges cuáles y ves la lista antes de borrar nada. Tu archivo hosts actual no se toca.",
                ("Elegir copias…", () => DeleteHostsCopies(hosts)), null));

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
        var lists = Store.CleanupsDir;
        Sections.Children.Add(new TextBlock { Text = "Historial", Style = (Style)FindResource("Section") });
        Sections.Children.Add(Row("Broom24", "Listas de archivos limpiados", "Cada limpieza guarda la lista exacta de lo que borró (temporales y cachés: no se pueden restaurar porque Windows y las apps los regeneran).",
            ("Abrir carpeta", () => { Directory.CreateDirectory(lists); Ui.OpenFolder(lists); return Task.CompletedTask; }), null));
        Sections.Children.Add(Row("DocumentText24", "Registro de actividad", "Todo lo que Tidy ha hecho, con fecha y hora.",
            ("Abrir", () => { if (!File.Exists(Store.LogPath)) Store.Log("Log created."); Ui.OpenText(Store.LogPath); return Task.CompletedTask; }), null));
    }

    void Section(string title, string icon, string detail, (string, Func<Task>)? action)
    {
        Sections.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Section") });
        Sections.Children.Add(Row(icon, title, detail, action, null));
    }

    /// <summary>What restoring this copy undoes, change by change, and how the file ends up. Historial shows the same list.</summary>
    internal static async Task<FrameworkElement> HostsChanges(FileInfo copy)
    {
        var (steps, net) = await Task.Run(() => Network.HostsUndo(copy));
        var list = new StackPanel();
        void Header(string text, double top) => list.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 4) });
        void Lines(List<string> lines, string prefix)
        {
            const int Max = 40; // ponytail: a hand-pasted list of thousands of lines is cut here, the count says how many more
            // translated piece by piece: "Vuelve: <line>" isn't a known text, and the block list line has its own template
            foreach (var l in lines.Take(Max)) list.Children.Add(Ui.Text($"{I18n.T(prefix)}: {I18n.T(l)}", "Muted", 2));
            if (lines.Count > Max) list.Children.Add(Ui.Text($"… y {lines.Count - Max} líneas más", "Muted", 2));
        }

        if (net.Added.Count + net.Removed.Count == 0)
            list.Children.Add(Ui.Text(steps.Count == 0 ? "Esta copia es igual que tu archivo hosts actual: restaurarla no cambia nada."
                : "Los cambios de después se anulan entre sí: tu archivo hosts quedaría igual que ahora.", null, 4));
        else
        {
            Header("Cómo queda tu archivo hosts", 0);
            Lines(net.Added, "Se quita");
            Lines(net.Removed, "Vuelve");
        }
        if (steps.Count > 0)
        {
            Header(steps.Count == 1 ? "El cambio que se deshace" : $"Los {steps.Count} cambios que se deshacen, del más reciente al más antiguo", 14);
            foreach (var s in steps)
            {
                list.Children.Add(new TextBlock { Text = $"{s.When:dd/MM/yyyy HH:mm} · {s.Title}", Margin = new Thickness(0, 8, 0, 2) });
                Lines(s.Added, "Añadió");
                Lines(s.Removed, "Borró");
            }
        }
        var content = new StackPanel { Width = 640 };
        content.Children.Add(Ui.Text("Tu versión actual se guarda como copia antes de restaurar, así que esto también se puede deshacer.", "Muted", 10));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) });
        return content;
    }

    async Task DeleteHostsCopies(List<(FileInfo Copy, Network.HostsChange Change)> hosts)
    {
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, FileInfo Copy)>();
        for (int i = hosts.Count - 1; i >= 0; i--)
        {
            var (h, change) = hosts[i];
            // a copy equal to the one before it adds nothing: those come ticked; the oldest is the closest to your original, never ticked
            var repeated = i > 0 && hosts[i - 1].Change.Added.Count + hosts[i - 1].Change.Removed.Count == 0;
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"Copia del {Network.HostsCopyTime(h):dd/MM/yyyy HH:mm:ss}", FontWeight = FontWeights.SemiBold });
            text.Children.Add(Ui.Text(string.Join(" · ", new[] { $"Guardada antes de: {change.Title}", Store.FormatBytes(h.Length),
                repeated ? "igual que la anterior" : i == 0 ? "la más antigua, la más parecida a tu archivo original" : null }.Where(s => s != null)), "Muted", 0));
            var box = new CheckBox { IsChecked = repeated, Content = text, Margin = new Thickness(0, 0, 0, 6) };
            boxes.Add((box, h));
            list.Children.Add(box);
        }
        var content = new StackPanel { Width = 640 };
        content.Children.Add(Ui.Text("Las copias marcadas se borran para siempre. Las repetidas ya vienen marcadas; tu archivo hosts actual no se toca.", null, 12));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) });
        if (!await Ui.Confirm("Borrar copias del archivo hosts", content, "Borrar las marcadas", danger: true)) return;
        var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Copy).ToList();
        if (chosen.Count == 0) return;
        var n = await Task.Run(() => Network.DeleteHostsBackups(chosen));
        Ui.Toast(n == chosen.Count ? "Copias borradas" : "Borradas en parte", n == chosen.Count ? $"{n} copias del archivo hosts borradas." : $"{n} de {chosen.Count} borradas; revisa el registro de actividad.",
            n == chosen.Count ? ControlAppearance.Success : ControlAppearance.Caution);
    }

    FrameworkElement Row(string icon, string title, string detail, (string Label, Func<Task> Run)? a, (string Label, Func<Task> Run)? b, ControlAppearance bLook = ControlAppearance.Danger)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Ui.Text(detail, "Muted", 0));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (act, i) in new[] { a, b }.Select((x, i) => (x, i)).Where(x => x.x != null))
        {
            var btn = new Wpf.Ui.Controls.Button { Content = act!.Value.Label, Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0), Appearance = i > 0 ? bLook : ControlAppearance.Secondary };
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
