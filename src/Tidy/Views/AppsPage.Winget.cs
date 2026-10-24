using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

/// <summary>One package in the updates list or the install catalog, with its progress.</summary>
public class PackageRow(string id, string name, string detail, string source) : Observable
{
    public string Id => id;
    public string Name => name;
    public string Detail => detail;
    public string Source => source;
    bool _selected, _installed;
    string _status = "";
    public bool Selected { get => _selected; set { _selected = value; Changed(); SelectionChanged?.Invoke(); } }
    public bool Installed { get => _installed; set { _installed = value; if (value && _status == "") _status = "Instalada"; Changed(); } }
    public bool CanSelect => !_installed && _status is "" or "Error";
    public string Status { get => _status; set { _status = value; Changed(); } }
    public Brush StatusBrush => Ui.Res(_status switch { "Hecho" or "Instalada" => "SystemFillColorSuccessBrush", "Error" => "SystemFillColorCriticalBrush", _ => "TextFillColorSecondaryBrush" });
    public event Action? SelectionChanged;
}

public partial class AppsPage
{
    bool _wingetLoaded, _wingetBusy;
    List<PackageRow> _updates = [];
    readonly Dictionary<string, PackageRow> _catalogRows = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _installedIds = new(StringComparer.OrdinalIgnoreCase);

    async Task LoadWinget()
    {
        WingetMissing.IsOpen = !Winget.Available;
        if (!Winget.Available) { UpdatesSummary.Text = "winget no está disponible"; UpdateBtn.IsEnabled = false; return; }
        ShowCatalog();
        await RefreshUpdates();
        var installed = await Task.Run(Winget.Installed);
        _installedIds = new(installed.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var r in _catalogRows.Values) if (_installedIds.Contains(r.Id)) r.Installed = true;
    }

    async Task RefreshUpdates()
    {
        UpdatesSummary.Text = "Buscando actualizaciones…";
        RecheckUpdatesBtn.IsEnabled = UpdateBtn.IsEnabled = false;
        var list = await Task.Run(Winget.Upgrades);
        _updates = list.Select(p => new PackageRow(p.Id, p.Name, $"{p.Id} · {p.Version} → {p.Available}", p.Source) { Selected = true }).ToList();
        UpdateList.ItemsSource = _updates;
        UpdateTools.Visibility = UpdateList.Visibility = _updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSummary.Text = _updates.Count == 0 ? "Todas tus apps están al día" : $"{_updates.Count} apps tienen una versión nueva";
        RecheckUpdatesBtn.IsEnabled = true;
        UpdateBtn.IsEnabled = _updates.Count > 0;
    }

    async void RecheckUpdates_Click(object sender, RoutedEventArgs e) { if (!_wingetBusy) await RefreshUpdates(); }
    void UpdatesAll_Click(object sender, RoutedEventArgs e) => _updates.Where(r => r.CanSelect).ToList().ForEach(r => r.Selected = true);
    void UpdatesNone_Click(object sender, RoutedEventArgs e) => _updates.ForEach(r => r.Selected = false);

    async void UpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _updates.Where(r => r.Selected && r.CanSelect).ToList();
        if (chosen.Count == 0) { Ui.Toast("Nada marcado", "Marca las apps que quieras actualizar.", ControlAppearance.Caution); return; }
        var panel = new StackPanel { Width = 640 };
        panel.Children.Add(Ui.Text("Se instalará la versión nueva de:", null, 0));
        panel.Children.Add(Ui.Lines(chosen.Select(r => $"•  {r.Name}   ({r.Detail})"), 240));
        panel.Children.Add(Ui.Text("Cierra esas apps antes si las tienes abiertas. Algunas pueden pedir reiniciar. Tus datos y ajustes de cada app se conservan.", "Muted", 0));
        if (!await Ui.Confirm($"¿Actualizar {chosen.Count} apps?", panel, "Actualizar")) return;
        await RunPackages(chosen, r => Winget.Upgrade(r.Id), install: false);
        await RefreshUpdates();
    }

    // ---------- install ----------
    PackageRow CatalogRow(string id, string name, string detail, string source)
    {
        if (_catalogRows.TryGetValue(id, out var row)) return row;
        row = _catalogRows[id] = new PackageRow(id, name, detail, source);
        if (_installedIds.Contains(id)) row.Installed = true;
        row.SelectionChanged += UpdateInstallButton;
        return row;
    }

    void ShowCatalog()
    {
        CatalogPanel.Children.Clear();
        foreach (var g in Winget.Catalog.GroupBy(a => a.Category))
        {
            CatalogPanel.Children.Add(new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 6) });
            var grid = new UniformGrid { Columns = 3 };
            foreach (var a in g) grid.Children.Add(Tile(CatalogRow(a.Id, a.Name, a.Description, a.Source)));
            CatalogPanel.Children.Add(grid);
        }
    }

    static Border Tile(PackageRow row)
    {
        var cb = new System.Windows.Controls.CheckBox { DataContext = row, VerticalAlignment = VerticalAlignment.Top, MinWidth = 0 };
        cb.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PackageRow.Selected)) { Mode = BindingMode.TwoWay });
        cb.SetBinding(IsEnabledProperty, new Binding(nameof(PackageRow.CanSelect)));
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(PackageRow.Status)));
        status.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PackageRow.StatusBrush)));
        title.Children.Add(status);
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = row.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") });
        cb.Content = text;
        return new Border { Style = (Style)Application.Current.FindResource("Tile"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8), Child = cb };
    }

    void UpdateInstallButton()
    {
        var n = _catalogRows.Values.Count(r => r.Selected && r.CanSelect);
        InstallBtn.IsEnabled = n > 0 && !_wingetBusy;
        InstallBtn.Content = n > 0 ? $"Instalar seleccionadas ({n})" : "Instalar seleccionadas";
    }

    void WingetQuery_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) WingetSearch_Click(sender, e); }

    async void WingetSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!Winget.Available) return;
        var q = WingetQuery.Text.Trim();
        if (q == "") { ShowCatalog(); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(q, @"^[\w .+-]{2,60}$"))
        {
            Ui.Toast("Búsqueda no válida", "Usa solo letras, números, espacios, puntos o guiones.", ControlAppearance.Caution);
            return;
        }
        CatalogPanel.Children.Clear();
        CatalogPanel.Children.Add(Ui.Text($"Buscando \"{q}\"…", "Muted"));
        var found = await Task.Run(() => Winget.Search(q));
        CatalogPanel.Children.Clear();
        var head = new Grid { Margin = new Thickness(2, 4, 0, 8) };
        head.Children.Add(new TextBlock { Text = found.Count == 0 ? $"Nada encontrado para \"{q}\"" : $"Resultados para \"{q}\"", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var back = new Wpf.Ui.Controls.Button { Content = "Volver al catálogo", HorizontalAlignment = HorizontalAlignment.Right };
        back.Click += (_, _) => { WingetQuery.Text = ""; ShowCatalog(); };
        head.Children.Add(back);
        CatalogPanel.Children.Add(head);
        var grid = new UniformGrid { Columns = 3 };
        foreach (var p in found)
            grid.Children.Add(Tile(CatalogRow(p.Id, p.Name, $"{p.Id} · {p.Version}{(p.Source == "msstore" ? " · Microsoft Store" : "")}", p.Source == "msstore" ? "msstore" : "winget")));
        CatalogPanel.Children.Add(grid);
        if (found.Count > 0)
            CatalogPanel.Children.Add(Ui.Text("Son resultados de winget: comprueba que el nombre y el fabricante (la primera parte del Id) son los que esperas antes de instalar.", "Muted", 0));
    }

    async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _catalogRows.Values.Where(r => r.Selected && r.CanSelect).ToList();
        if (chosen.Count == 0) return;
        var panel = new StackPanel { Width = 640 };
        panel.Children.Add(Ui.Lines(chosen.Select(r => $"•  {r.Name}   ({r.Id}{(r.Source == "msstore" ? ", Microsoft Store" : "")})"), 200));
        panel.Children.Add(Ui.Text("Se descargan de la web oficial de cada fabricante (o de Microsoft Store). Al instalar aceptas la licencia de cada programa. Se desinstalan como cualquier otro, desde \"Desinstalar en profundidad\".", "Muted", 0));
        if (!await Ui.Confirm($"¿Instalar {chosen.Count} apps?", panel, "Instalar")) return;
        await RunPackages(chosen, r => Winget.Install(r.Id, r.Source), install: true);
        foreach (var r in chosen.Where(r => r.Status == "Hecho")) { _installedIds.Add(r.Id); r.Status = ""; r.Installed = true; }
        UpdateInstallButton();
    }

    /// <summary>One package at a time, each with its own status; a failure never stops the rest.</summary>
    async Task RunPackages(List<PackageRow> rows, Func<PackageRow, (bool Ok, string Message)> action, bool install)
    {
        if (_wingetBusy) return;
        if (!await Ui.EnsureRestorePoint(install ? "Instalar apps" : "Actualizar apps")) return;
        _wingetBusy = true;
        UpdateBtn.IsEnabled = InstallBtn.IsEnabled = RecheckUpdatesBtn.IsEnabled = false;
        foreach (var r in rows) r.Status = "En cola";
        var errors = new List<string>();
        try
        {
            foreach (var r in rows)
            {
                r.Status = install ? "Instalando…" : "Actualizando…";
                var (ok, msg) = await Task.Run(() => action(r));
                r.Status = ok ? "Hecho" : "Error";
                r.Selected = false;
                if (!ok) errors.Add($"{r.Name}: {msg}");
                else History.Add(HistoryKind.App, $"{(install ? "Instalada" : "Actualizada")}: {r.Name}", r.Id, install ? $"winget:{r.Id}" : null);
            }
        }
        finally
        {
            _wingetBusy = false;
            RecheckUpdatesBtn.IsEnabled = true;
            UpdateBtn.IsEnabled = _updates.Count > 0;
            UpdateInstallButton();
        }
        var done = install ? "instaladas" : "actualizadas";
        var okCount = rows.Count - errors.Count;
        if (errors.Count == 0) Ui.Toast("Listo", $"{okCount} apps {done}.");
        else Ui.Toast($"{okCount} {done}, {errors.Count} con error", string.Join("\n", errors.Take(3)), ControlAppearance.Caution);
    }
}
