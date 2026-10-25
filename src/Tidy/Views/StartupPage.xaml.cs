using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class StartupRow(StartupApp app) : Observable
{
    public StartupApp App => app;
    public string Name => app.Name;
    public string Command => app.Command;
    public string Detail => string.IsNullOrEmpty(app.Publisher) ? app.Location : $"{app.Publisher} · {app.Location}";
    public bool Enabled => app.Enabled;
    public ImageSource? Icon => Ui.FileIcon(app.ExePath);
}

public partial class StartupPage : Page
{
    static readonly string[] PresetIcons = ["LeafOne24", "Gauge24", "TopSpeed24"];
    List<StartupRow> _rows = [];
    List<PresetItem> _candidates = [];

    public StartupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Reload();
    }

    async Task Reload()
    {
        _rows = (await Task.Run(StartupApps.List)).Select(a => new StartupRow(a)).ToList();
        List.ItemsSource = _rows;
        UpdateSummary();
        Presets.Children.Clear();
        Presets.Children.Add(Ui.Text("Buscando lo que arranca solo…", "Muted"));
        _candidates = await Task.Run(StartupPresets.Candidates);
        ShowPresets();
    }

    void UpdateSummary()
    {
        var on = _rows.Count(r => r.Enabled);
        Summary.Text = $"{on} activadas · {_rows.Count - on} desactivadas";
    }

    void ShowPresets()
    {
        Presets.Children.Clear();
        for (int level = 1; level <= 3; level++)
        {
            var items = _candidates.Where(c => c.Level <= level).ToList();
            var panel = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(Ui.Icon(PresetIcons[level - 1], 20));
            head.Children.Add(new System.Windows.Controls.TextBlock { Text = StartupPresets.Names[level - 1], FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(8, 0, 0, 0) });
            panel.Children.Add(head);
            panel.Children.Add(Ui.Text(StartupPresets.Descriptions[level - 1], "Muted", 10));
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = items.Count == 0 ? "Nada que cambiar en tu PC" : $"{items.Count} {(items.Count == 1 ? "cambio" : "cambios")}",
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
            });
            var btn = new Wpf.Ui.Controls.Button { Content = "Ver la lista…", IsEnabled = items.Count > 0, Appearance = level == 2 ? ControlAppearance.Primary : ControlAppearance.Secondary };
            var lvl = level;
            btn.Click += async (_, _) => await Apply(lvl, items);
            var card = new Border { Style = (Style)FindResource("Tile"), Margin = new Thickness(level == 1 ? 0 : 6, 0, level == 3 ? 0 : 6, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(btn, Dock.Bottom);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            dock.Children.Add(btn);
            dock.Children.Add(panel);
            card.Child = dock;
            Presets.Children.Add(card);
        }
    }

    async void Slow_Click(object sender, RoutedEventArgs e)
    {
        SlowBtn.IsEnabled = false;
        List<PresetItem> items;
        try { items = await Task.Run(() => StartupPresets.BootItems(Benchmark.Slowdowns(), StartupApps.List())); }
        finally { SlowBtn.IsEnabled = true; }
        if (Benchmark.BootError is { } err) { Ui.Toast("No se pudo leer", err, ControlAppearance.Caution); return; }
        if (items.Count == 0) { Ui.Toast("Nada lo alarga", "En el último mes Windows no ha apuntado ningún retraso al arrancar."); return; }
        if (await Review("Qué alarga el arranque",
                "Lo que Windows ha visto retrasar tus arranques del último mes, de lo que más tiempo cuesta a lo que menos. Las apps de inicio que se pueden quitar vienen marcadas: siguen instaladas y se deshace en Historial.",
                items, "Tidy: arranque"))
            await Reload();
    }

    async Task Apply(int level, List<PresetItem> items)
    {
        var name = StartupPresets.Names[level - 1];
        if (await Review($"Preconfiguración {name}", $"{StartupPresets.Descriptions[level - 1]} Desmarca lo que quieras conservar. Cada cambio se puede deshacer en Historial.",
                items, $"Tidy: preconfiguración de inicio {name}"))
            await Reload();
    }

    /// <summary>The list grouped by kind, each item with its reason. Info items can't be ticked: they only explain.</summary>
    static (StackPanel Content, List<(CheckBox Box, PresetItem Item)> Boxes) ReviewList(string intro, List<PresetItem> items)
    {
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, PresetItem Item)>();
        foreach (var group in items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            list.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = group.Key switch
                {
                    PresetKind.App => "Apps de inicio (se desactivan, no se desinstalan)", PresetKind.Service => "Servicios",
                    PresetKind.Task => "Tareas programadas (se desactivan)", PresetKind.Process => "Cerrar ahora (sin guardar; la abres cuando quieras)",
                    _ => "También lo retrasan, pero Tidy no los toca",
                },
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 6),
            });
            foreach (var i in group)
            {
                var text = new StackPanel();
                text.Children.Add(new System.Windows.Controls.TextBlock { Text = i.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                // translated piece by piece: the joined sentence isn't a known text
                text.Children.Add(Ui.Text($"{I18n.T(i.Detail).TrimEnd('.')}. {I18n.T(i.Why)}", "Muted", 0));
                if (i.Kind == PresetKind.Info) { text.Margin = new Thickness(0, 0, 0, 8); list.Children.Add(text); continue; }
                var box = new CheckBox { IsChecked = i.On, Content = text, Margin = new Thickness(0, 0, 0, 6) };
                boxes.Add((box, i));
                list.Children.Add(box);
            }
        }
        var content = new StackPanel { Width = 640 };
        content.Children.Add(Ui.Text(intro, null, 12));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) });
        return (content, boxes);
    }

    /// <summary>Shows the full list with the reason for each item; only what stays ticked is changed. True if anything was applied.</summary>
    internal static async Task<bool> Review(string title, string intro, List<PresetItem> items, string restorePoint)
    {
        var (content, boxes) = ReviewList(intro, items);
        // nothing that can be changed: just the explanation, no "apply" button that would do nothing
        if (items.All(i => i.Kind == PresetKind.Info)) { await Ui.Info(title, content); return false; }
        if (!await Ui.Confirm(title, content, "Aplicar lo marcado")) return false;

        var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item).ToList();
        if (chosen.Count == 0) return false;
        if (chosen.Any(c => c.Kind is PresetKind.Service or PresetKind.Task) && !await Ui.EnsureRestorePoint(restorePoint)) return false;
        var failed = await Task.Run(() => chosen.Where(c =>
        {
            try { return !c.Apply().Ok; }
            catch (Exception ex) { Store.Log($"{title}: {c.Name} failed: {ex.Message}"); return true; }
        }).Select(c => c.Name).ToList());
        Store.Log($"{title}: {chosen.Count - failed.Count} changed, {failed.Count} failed");
        if (failed.Count == 0 && chosen.All(c => c.Kind == PresetKind.Process)) Ui.Toast("Cambios aplicados", $"{chosen.Count} cerradas. Se vuelven a abrir normal cuando las uses.");
        else if (failed.Count == 0) Ui.Toast("Cambios aplicados", $"{chosen.Count} cambios. Se notará en el próximo arranque y se pueden deshacer en Historial.");
        else Ui.Toast("Aplicado en parte", $"No se pudo cambiar: {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}", ControlAppearance.Caution);
        return true;
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var row = (StartupRow)toggle.Tag;
        var enable = toggle.IsChecked == true;
        try
        {
            StartupApps.SetEnabled(row.App, enable);
            Ui.Toast(enable ? "Activada" : "Desactivada", $"\"{row.Name}\" {(enable ? "arrancará" : "ya no arrancará")} con Windows.");
        }
        catch (Exception ex)
        {
            toggle.IsChecked = row.Enabled;
            Ui.Toast("No se pudo cambiar", ex.Message, ControlAppearance.Danger);
        }
        UpdateSummary();
    }
}
