using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class CategoryRow(CleanCategory c, Action selectionChanged) : Observable
{
    public CleanCategory Category => c;
    public ScanResult? Result { get; private set; }
    public string Name => c.Name;
    public string Description => c.Description;
    public string Icon => c.Icon;
    public string? Note => Result?.Note;
    public List<CleanItem> Items => Result?.Items ?? [];
    public string SizeText => Result == null ? "—" : Store.FormatBytes(Result.Bytes);
    public string CountText => Result == null ? "" : Files(Result.Items.Count);
    public string ExpandText => Items.Count == 1 ? "Ver el archivo" : $"Ver los {Items.Count:N0} archivos";
    public static string Files(int n) => n == 1 ? "1 archivo" : $"{n:N0} archivos";
    public Visibility ListVisibility => Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    bool _selected = c.DefaultOn;
    public bool Selected { get => _selected; set { _selected = value; selectionChanged(); } }

    public void SetResult(ScanResult? r) { Result = r; Changed(); }
}

public partial class CleanerPage : Page
{
    readonly List<CategoryRow> _rows;
    string? _lastList;

    public CleanerPage()
    {
        InitializeComponent();
        _rows = Cleaner.Categories.Select(c => new CategoryRow(c, UpdateTotals)).ToList();
        Cats.ItemsSource = _rows;
        Loaded += (_, _) => ShowDisk();
    }

    void ShowDisk()
    {
        var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var used = d.TotalSize - d.AvailableFreeSpace;
        DiskLabel.Text = $"Disco {d.Name.TrimEnd('\\')}";
        DiskFree.Text = $"{Store.FormatBytes(d.AvailableFreeSpace)} libres de {Store.FormatBytes(d.TotalSize)}";
        DiskBar.Value = used * 100.0 / d.TotalSize;
        DiskBar.Foreground = Ui.Health(DiskBar.Value);
    }

    void UpdateTotals()
    {
        if (_rows.All(r => r.Result == null)) return;
        var sel = _rows.Where(r => r.Selected && r.Result != null).ToList();
        long bytes = sel.Sum(r => r.Result!.Bytes);
        TotalText.Text = $"{Store.FormatBytes(bytes)} para limpiar";
        TotalSub.Text = $"{CategoryRow.Files(sel.Sum(r => r.Items.Count))} en lo que tienes marcado. Marca o desmarca abajo.";
        CleanBtn.Content = bytes > 0 ? $"Limpiar {Store.FormatBytes(bytes)}" : "Limpiar";
        CleanBtn.IsEnabled = bytes > 0;
    }

    async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();

    async Task Scan()
    {
        ScanBtn.IsEnabled = false;
        CleanBtn.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        TotalText.Text = "Analizando…";
        foreach (var row in _rows)
        {
            TotalSub.Text = $"Revisando {row.Name.ToLowerInvariant()}…";
            row.SetResult(await Task.Run(() => Cleaner.Scan(row.Category)));
        }
        Busy.Visibility = Visibility.Collapsed;
        ScanBtn.IsEnabled = true;
        ScanBtn.Content = "Volver a analizar";
        ScanBtn.Appearance = ControlAppearance.Secondary;
        CleanBtn.Visibility = Visibility.Visible;
        UpdateTotals();
        ShowDisk();
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var report = await ConfirmAndClean(_rows.Where(r => r.Selected && r.Result != null).Select(r => r.Result!).ToList());
        if (report == null) return;
        _lastList = report.ListFile;
        ResultText.Text = $"Recuperados {Store.FormatBytes(report.Bytes)}: {report.Deleted:N0} archivos borrados" +
                          (report.Skipped > 0 ? $", {report.Skipped:N0} omitidos porque estaban en uso." : ".");
        ResultCard.Visibility = Visibility.Visible;
        await Scan();
    }

    void OpenList_Click(object sender, RoutedEventArgs e) { if (_lastList != null) Ui.OpenText(_lastList); }

    /// <summary>The one gate before anything is deleted: shows every single file and needs an explicit click.</summary>
    public static async Task<Cleaner.CleanReport?> ConfirmAndClean(IList<ScanResult> results)
    {
        var withItems = results.Where(r => r.Items.Count > 0).ToList();
        if (withItems.Count == 0)
        {
            Ui.Toast("Nada que limpiar", "No hay archivos innecesarios en lo seleccionado.");
            return null;
        }
        var total = withItems.Sum(r => r.Bytes);
        var count = withItems.Sum(r => r.Items.Count);

        var panel = new StackPanel { Width = 740 };
        panel.Children.Add(Ui.Text("Se borrarán de forma definitiva estos archivos. Revisa la lista; si algo no te cuadra, cancela.", "Muted", 12));
        foreach (var r in withItems)
            panel.Children.Add(Ui.Text($"•  {r.Category.Name}: {CategoryRow.Files(r.Items.Count)} · {Store.FormatBytes(r.Bytes)}", null, 2));
        if (withItems.Any(r => r.Category.RecycleBin))
        {
            var warn = Ui.Text("La papelera se vaciará: esos archivos ya no se podrán recuperar.", null, 0);
            warn.Margin = new Thickness(0, 8, 0, 0);
            warn.Foreground = Ui.Res("SystemFillColorCriticalBrush");
            panel.Children.Add(warn);
        }
        panel.Children.Add(new ListBox
        {
            Height = 300, Margin = new Thickness(0, 14, 0, 8),
            Style = (Style)Application.Current.FindResource("FileList"),
            ItemsSource = withItems.SelectMany(r => r.Items).OrderByDescending(i => i.Size).ToList(),
        });
        panel.Children.Add(Ui.Text($"Se guardará una copia de esta lista en {Store.CleanupsDir}.", "Muted", 0));

        if (!await Ui.Confirm($"¿Borrar {CategoryRow.Files(count)} ({Store.FormatBytes(total)})?", panel, "Borrar definitivamente", danger: true))
            return null;

        return await CleanConfirmed(withItems);
    }

    /// <summary>Deletes what the user has just seen and confirmed, and remembers when.</summary>
    public static async Task<Cleaner.CleanReport> CleanConfirmed(List<ScanResult> confirmed)
    {
        var report = await Task.Run(() => Cleaner.Clean(confirmed));
        Store.Settings.LastClean = DateTime.Now;
        Store.Settings.LastCleanBytes = report.Bytes;
        Store.Save();
        return report;
    }
}
