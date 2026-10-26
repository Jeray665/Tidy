using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public partial class HistoryPage : Page
{
    List<HistoryEntry> _entries = [];
    int _shown = 60;
    // what can still be undone right now (a later restore elsewhere may have done it already)
    HashSet<string> _undoable = [];

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
    }

    async Task Refresh()
    {
        if (Benchmark.All().Count == 0) await Task.Run(() => Benchmark.Measure("Primera medición"));
        ShowCompare();
        var (boots, first) = await Task.Run(() => (Benchmark.Boots(), History.FirstChange()));
        ShowBoots(boots, first);
        (_entries, _undoable) = await Task.Run(() => (History.All(), Undoable()));
        ShowEntries();
    }

    // ---------- before / after ----------
    void ShowCompare()
    {
        var all = Benchmark.All();
        Compare.Children.Clear();
        Compare.RowDefinitions.Clear();
        Compare.ColumnDefinitions.Clear();
        if (all.Count == 0) return;
        Measurement a = all[0], b = all[^1];
        foreach (var w in new[] { "2*", "*", "*", "*" })
            Compare.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)new GridLengthConverter().ConvertFrom(w)! });

        var rows = new List<(string Name, double? Before, double? Now, string Unit, bool LowerIsBetter)>
        {
            ("Arranque de Windows", a.BootSec, b.BootSec, "s", true),
            ("Memoria RAM en uso", a.RamUsedGb, b.RamUsedGb, "GB", true),
            ("Procesos abiertos", a.Processes, b.Processes, "", true),
            ("Apps que arrancan con Windows", a.StartupApps, b.StartupApps, "", true),
            ("Servicios en marcha", a.ServicesRunning, b.ServicesRunning, "", true),
            ("Espacio libre en C:", a.FreeCGb, b.FreeCGb, "GB", false),
        };
        void Cell(int r, int c, string text, bool bold = false, string? brush = null, bool right = true)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (brush != null) t.Foreground = Ui.Res(brush);
            Grid.SetRow(t, r); Grid.SetColumn(t, c);
            Compare.Children.Add(t);
        }
        Compare.RowDefinitions.Add(new RowDefinition());
        Cell(0, 0, "", right: false);
        Cell(0, 1, $"Antes ({a.When:dd/MM/yy})", true, "TextFillColorSecondaryBrush");
        Cell(0, 2, all.Count > 1 ? $"Ahora ({b.When:dd/MM HH:mm})" : "Ahora", true, "TextFillColorSecondaryBrush");
        Cell(0, 3, "Diferencia", true, "TextFillColorSecondaryBrush");
        for (var i = 0; i < rows.Count; i++)
        {
            var (name, before, now, unit, lower) = rows[i];
            Compare.RowDefinitions.Add(new RowDefinition());
            string F(double? v) => v is { } x ? (unit == "" ? $"{x:0}" : $"{x:0.0} {unit}") : "—";
            Cell(i + 1, 0, name, right: false);
            Cell(i + 1, 1, F(before));
            Cell(i + 1, 2, F(now), true);
            if (all.Count > 1 && before is { } x0 && now is { } x1 && Math.Abs(x1 - x0) > 0.05)
            {
                var better = lower ? x1 < x0 : x1 > x0;
                var pct = x0 != 0 ? $" ({(x1 - x0) / x0 * 100:+0;-0} %)" : "";
                Cell(i + 1, 3, $"{(x1 > x0 ? "+" : "−")}{F(Math.Abs(x1 - x0))}{pct}", true, better ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
            }
            else Cell(i + 1, 3, all.Count > 1 ? "igual" : "", false, "TextFillColorTertiaryBrush");
        }
        CompareNote.Text = all.Count > 1
            ? $"{all.Count} mediciones. \"Antes\" es la primera ({a.Label.ToLowerInvariant()}). Para comparar en igualdad, mide recién arrancado el PC y sin juegos abiertos."
            : "Esta es tu primera medición: queda guardada como el \"antes\". Vuelve después de optimizar (o tras unos días) y pulsa \"Medir ahora\".";
    }

    async void Measure_Click(object sender, RoutedEventArgs e)
    {
        MeasureBtn.IsEnabled = false;
        await Task.Run(() => Benchmark.Measure("Medición manual"));
        MeasureBtn.IsEnabled = true;
        ShowCompare();
        Ui.Toast("Medición guardada", "Comparada con la primera medición.");
    }

    void ShowBoots(List<BootTime> boots, DateTime? firstChange)
    {
        BootBars.Children.Clear();
        if (boots.Count == 0)
        {
            BootSummary.Text = Benchmark.BootError ?? "Windows todavía no tiene registrado ningún tiempo de arranque. Aparecerán tras el próximo reinicio.";
            return;
        }
        var max = boots.Max(b => b.Seconds);
        foreach (var b in boots.Take(15))
        {
            var after = firstChange is { } f && b.When > f;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.Children.Add(new TextBlock { Text = $"{b.When:dd/MM HH:mm}", Foreground = Ui.Res("TextFillColorSecondaryBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var bar = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Background = Ui.Res(after ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush"),
            };
            bar.Loaded += (_, _) => bar.Width = Math.Max(4, ((FrameworkElement)bar.Parent).ActualWidth * b.Seconds / max);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var secs = new TextBlock { Text = $"{b.Seconds:0.0} s", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(secs, 2);
            row.Children.Add(secs);
            BootBars.Children.Add(row);
        }
        var before = boots.Where(b => firstChange == null || b.When <= firstChange).ToList();
        var afterList = firstChange == null ? [] : boots.Where(b => b.When > firstChange).ToList();
        BootSummary.Text = before.Count > 0 && afterList.Count > 0
            ? $"Media antes de Tidy: {before.Average(b => b.Seconds):0.0} s · después: {afterList.Average(b => b.Seconds):0.0} s ({(afterList.Average(b => b.Seconds) - before.Average(b => b.Seconds)) / before.Average(b => b.Seconds) * 100:+0;-0} %)"
            : $"Último arranque: {boots[0].Seconds:0.0} s · media de los últimos {boots.Count}: {boots.Average(b => b.Seconds):0.0} s";
    }

    // ---------- timeline ----------
    static HashSet<string> Undoable()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tweaks.Changed()) set.Add($"tweak:{t.Id}");
        foreach (var s in Services.LoadBackup().Keys) set.Add($"service:{s}");
        foreach (var t in ScheduledTasks.LoadBackup().Keys) set.Add($"task:{t}");
        if (Network.HasDnsBackup) set.Add("dns");
        foreach (var h in Network.HostsBackups()) set.Add($"hosts:{h.Name}");
        foreach (var q in Uninstaller.Quarantines()) set.Add($"quarantine:{q.Id}");
        foreach (var m in Storage.Moves()) set.Add($"moved:{m}");
        foreach (var b in Images.Batches()) set.Add($"created:{b}");
        var plans = PowerPlan.All();
        foreach (var p in plans.Where(p => !p.Active)) set.Add($"power:{p.Id}");
        set.Add("recycle");
        set.Add("startup");
        // these carry everything needed to undo inside the entry itself
        string[] selfContained = ["startup:", "powertime:", "policies:", .. Winget.Available ? ["winget:"] : Array.Empty<string>()];
        foreach (var e in History.All().Where(e => e.Undo != null && selfContained.Any(e.Undo.StartsWith))) set.Add(e.Undo!);
        return set;
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) { _shown = 60; ShowEntries(); } }
    void More_Click(object sender, RoutedEventArgs e) { _shown += 100; ShowEntries(); }

    void ShowEntries()
    {
        var filter = Filter.SelectedItem as ComboBoxItem;
        var kinds = (filter?.Tag as string)?.Split(',').Select(Enum.Parse<HistoryKind>).ToHashSet();
        var list = _entries.Where(e => kinds == null || kinds.Contains(e.Kind))
            .Where(e => Filter.SelectedIndex != 1 || CanUndo(e)).ToList();
        Entries.Children.Clear();
        if (list.Count == 0) Entries.Children.Add(Ui.Text(_entries.Count == 0 ? "Todavía no hay cambios registrados. A partir de ahora, todo lo que haga Tidy aparecerá aquí." : "Nada con este filtro.", "Muted"));
        DateTime? day = null;
        foreach (var e in list.Take(_shown))
        {
            if (day != e.When.Date)
            {
                day = e.When.Date;
                Entries.Children.Add(new TextBlock
                {
                    Text = day == DateTime.Today ? "Hoy" : day == DateTime.Today.AddDays(-1) ? "Ayer" : $"{day:D}",
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 6), Foreground = Ui.Res("TextFillColorSecondaryBrush"),
                });
            }
            Entries.Children.Add(Row(e));
        }
        MoreBtn.Visibility = list.Count > _shown ? Visibility.Visible : Visibility.Collapsed;
    }

    bool CanUndo(HistoryEntry e) => e.Undo != null && _undoable.Contains(e.Undo);

    UIElement Row(HistoryEntry e)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(Ui.Text($"{e.When:HH:mm}{(e.Detail != "" ? " · " + e.Detail : "")}", "Muted", 0));
        var card = new CardControl { Icon = Ui.Icon(KindIcon(e.Kind), 20), Header = header, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(14, 8, 14, 8) };
        if (CanUndo(e))
        {
            var btn = new Wpf.Ui.Controls.Button { Content = UndoLabel(e.Undo!) };
            btn.Click += async (_, _) => { btn.IsEnabled = false; await Undo(e); };
            card.Content = btn;
        }
        return card;
    }

    static string KindIcon(HistoryKind k) => k switch
    {
        HistoryKind.Tweak => "Wand24", HistoryKind.Service => "Settings24", HistoryKind.Task => "CalendarClock24", HistoryKind.Network => "Globe24",
        HistoryKind.Uninstall => "Delete24", HistoryKind.Clean => "Broom24", HistoryKind.Files => "Document24", HistoryKind.App => "AppsList24",
        HistoryKind.Power => "Flash24", HistoryKind.Startup => "Rocket24", HistoryKind.Game => "Games24", _ => "Info24",
    };

    static string UndoLabel(string undo) => undo.Split(':')[0] switch
    {
        "tweak" => "Deshacer", "service" or "task" or "dns" => "Volver a como estaba", "hosts" => "Restaurar versión anterior",
        "quarantine" => "Restaurar restos", "moved" => "Devolver a su sitio", "created" => "Mandar copias a la Papelera", "winget" => "Desinstalar", "recycle" => "Abrir Papelera", "power" => "Volver al plan anterior", "powertime" => "Volver al tiempo anterior",
        "policies" => "Volver a bloquearlas",
        "startup" => undo.Contains(':') ? "Volver a como estaba" : "Ir a Inicio de Windows", _ => "Deshacer",
    };

    async Task Undo(HistoryEntry e)
    {
        var (kind, arg) = (e.Undo!.Split(':')[0], e.Undo.Contains(':') ? e.Undo[(e.Undo.IndexOf(':') + 1)..] : "");
        try
        {
            switch (kind)
            {
                case "recycle": Ui.OpenFolder("shell:RecycleBinFolder"); return;
                case "startup" when arg == "": MainWindow.Current?.Navigate(typeof(StartupPage)); return;
            }
            // hosts: the full list of what goes back, the same one Copias de seguridad shows
            var hostsCopy = kind == "hosts" ? Network.HostsBackups().FirstOrDefault(h => h.Name == arg) : null;
            if (!await Ui.Confirm($"¿{UndoLabel(e.Undo)}?", hostsCopy != null ? await BackupsPage.HostsChanges(hostsCopy) : $"{e.Title}\n\n" + kind switch
                {
                    "tweak" => "Se devuelve el valor que había antes de que Tidy lo cambiara.",
                    "service" => "El servicio vuelve al tipo de inicio que tenía antes de Tidy (los cambios posteriores también se deshacen).",
                    "task" => "La tarea vuelve al estado que tenía antes de Tidy.",
                    "dns" => "Todas tus conexiones vuelven al DNS que tenían antes de Tidy.",
                    "hosts" => "Se pone la versión del archivo hosts de antes de este cambio. La actual se guarda también como copia.",
                    "quarantine" => "Los restos vuelven a su sitio original.",
                    "moved" => "Cada archivo vuelve a la carpeta de donde se movió. Si allí ya hay otro con el mismo nombre, ese se queda donde está y no se pisa nada.",
                    "winget" => "Se desinstala el programa con su desinstalador oficial.",
                    "created" => $"Las {Images.Record(arg)?.Count(File.Exists) ?? 0} copias que creó Tidy van a la Papelera (puedes recuperarlas de allí). Los originales no se tocan.",
                    "power" => "Se vuelve a activar el plan de energía anterior.",
                    "powertime" => "Se vuelve a poner el tiempo que había antes en el plan activo.",
                    "startup" => "La app vuelve a arrancar con Windows (o a no hacerlo) como antes de este cambio.",
                    "policies" => "Se vuelven a poner las directivas que bloqueaban estas herramientas, tal como estaban.",
                    _ => "",
                }, UndoLabel(e.Undo))) return;
            var ok = await Task.Run(() => kind switch
            {
                "tweak" => Tweaks.Find(arg) is { } t && Do(() => Tweaks.Revert(t)),
                "service" => Services.Restore(arg),
                "task" => ScheduledTasks.RestoreOriginal(arg),
                "dns" => Network.RestoreDns() > 0,
                "hosts" => Network.HostsBackups().FirstOrDefault(h => h.Name == arg) is { } h && Do(() => Network.RestoreHosts(h)),
                "quarantine" => Uninstaller.Quarantines().FirstOrDefault(q => q.Id == arg) is { } q && Uninstaller.Restore(q).Failed == 0,
                "moved" => Storage.UndoMove(arg),
                "created" => Images.UndoBatch(arg),
                "winget" => Winget.Uninstall(arg).Ok,
                "startup" => StartupApps.Restore(arg),
                "policies" => Repairs.RestorePolicies(arg),
                "power" => Guid.TryParse(arg, out var g) && PowerPlan.Activate(g),
                "powertime" => arg.Split(':') is [var id, var ac, var dc] && PowerTimeouts.All.FirstOrDefault(t => t.Id == id) is { } pt
                    && uint.TryParse(ac, out var a) && uint.TryParse(dc, out var d) && Do(() => { Store.Settings.PowerTimeouts.Remove(id); Store.Save(); }) && PowerTimeouts.Write(pt, a, d),
                _ => false,
            });
            Ui.Toast(ok ? "Deshecho" : "No se pudo deshacer del todo", ok ? e.Title : "Revisa el registro de actividad en Ajustes.", ok ? ControlAppearance.Success : ControlAppearance.Caution);
        }
        catch (Exception ex) { Ui.Toast("No se pudo deshacer", ex.Message, ControlAppearance.Danger); }
        await Refresh();
    }

    static bool Do(Action a) { a(); return true; }
}
