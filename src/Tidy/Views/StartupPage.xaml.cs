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

    /// <summary>Shows the full list with the reason for each item; only what stays ticked is changed.</summary>
    async Task Apply(int level, List<PresetItem> items)
    {
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, PresetItem Item)>();
        foreach (var group in items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            list.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = group.Key switch { PresetKind.App => "Apps de inicio (se desactivan, no se desinstalan)", PresetKind.Service => "Servicios (pasan a manual)", _ => "Tareas programadas (se desactivan)" },
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, boxes.Count == 0 ? 0 : 12, 0, 6),
            });
            foreach (var i in group)
            {
                var text = new StackPanel();
                text.Children.Add(new System.Windows.Controls.TextBlock { Text = i.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(Ui.Text($"{i.Detail.TrimEnd('.')}. {i.Why}", "Muted", 0));
                var box = new CheckBox { IsChecked = true, Content = text, Margin = new Thickness(0, 0, 0, 6) };
                boxes.Add((box, i));
                list.Children.Add(box);
            }
        }
        var content = new StackPanel { Width = 640 };
        content.Children.Add(Ui.Text($"{StartupPresets.Descriptions[level - 1]} Desmarca lo que quieras conservar. Cada cambio se puede deshacer en Historial.", null, 12));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) });
        if (!await Ui.Confirm($"Preconfiguración {StartupPresets.Names[level - 1]}", content, "Aplicar lo marcado")) return;

        var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item).ToList();
        if (chosen.Count == 0) return;
        if (chosen.Any(c => c.Kind != PresetKind.App) && !await Ui.EnsureRestorePoint($"Tidy: preconfiguración de inicio {StartupPresets.Names[level - 1]}")) return;
        var failed = await Task.Run(() => chosen.Where(c =>
        {
            try { return !c.Apply().Ok; }
            catch (Exception ex) { Store.Log($"Startup preset: {c.Name} failed: {ex.Message}"); return true; }
        }).Select(c => c.Name).ToList());
        Store.Log($"Startup preset {StartupPresets.Names[level - 1]}: {chosen.Count - failed.Count} changed, {failed.Count} failed");
        if (failed.Count == 0) Ui.Toast("Preconfiguración aplicada", $"{chosen.Count} cambios. Se notará en el próximo arranque y se pueden deshacer en Historial.");
        else Ui.Toast("Aplicada en parte", $"No se pudo cambiar: {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}", ControlAppearance.Caution);
        await Reload();
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
