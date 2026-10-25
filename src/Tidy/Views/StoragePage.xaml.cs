using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class DriveRow(DriveUsage d)
{
    public string Title => string.IsNullOrEmpty(d.Label) ? $"Disco ({d.Name})" : $"{d.Label} ({d.Name})";
    public string UsageText => $"Usado {Store.FormatBytes(d.Used)} de {Store.FormatBytes(d.Total)} · {Store.FormatBytes(d.Free)} libres";
    public double Percent => d.Percent;
    public Brush Brush => Ui.Health(d.Percent);
}

public class FolderRow(FolderSize f, long total)
{
    public string Path => f.Path;
    public string Name => System.IO.Path.GetFileName(f.Path);
    public string Parent => System.IO.Path.GetDirectoryName(f.Path) ?? "";
    public string SizeText => Store.FormatBytes(f.Size);
    public double Percent => total > 0 ? f.Size * 100.0 / total : 0;
}

public class FileRow(FoundFile f, string? header = null) : Observable
{
    public FoundFile File => f;
    public bool IsHeader => header != null;
    public int Group { get; init; }
    public string Name => header ?? f.Name;
    public string Folder => f.Folder;
    public string Path => f.Path;
    public string SizeText => Store.FormatBytes(f.Size);
    public string DateText => f.Modified.ToString("dd/MM/yyyy");
    public bool CanSelect => !f.Protected;
    public string? LockReason => f.Protected ? "Archivo de Windows o de un programa: no se puede borrar desde aquí." : null;
    bool _selected;
    public bool Selected { get => _selected; set { _selected = value && CanSelect; Changed(); } }
}

public partial class StoragePage : Page
{
    CancellationTokenSource? _cts;
    readonly List<string> _dupFolders = [.. Storage.PersonalFolders()];

    public StoragePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Init();
    }

    async Task Init()
    {
        var drives = Storage.Drives();
        Drives.ItemsSource = drives.Select(d => new DriveRow(d)).ToList();
        if (ScanDrive.Items.Count == 0)
        {
            foreach (var d in drives) ScanDrive.Items.Add(d.Name + "\\");
            ScanDrive.Items.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            ScanDrive.SelectedIndex = 0;
        }
        UpdateDupFolders();
        _ = ShowDiskHealth();
        var dl = await Task.Run(Storage.DownloadedFiles);
        DownloadsCardText.Text = $"{Store.FormatBytes(dl.Sum(f => f.Size))} en {CategoryRow.Files(dl.Count)}";
        DownloadsInfo.Text = $"{CategoryRow.Files(dl.Count)} · {Store.FormatBytes(dl.Sum(f => f.Size))} en {Storage.Downloads}";
        DownloadList.ItemsSource = dl.Select(f => new FileRow(f)).ToList();
    }

    async Task ShowDiskHealth()
    {
        var disks = await Task.Run(() => SecurityStatus.DiskStatus());
        DiskHealthList.Children.Clear();
        if (disks.Count == 0) { DiskHealthList.Children.Add(Ui.Text("Windows no ha devuelto el estado de los discos.", "Muted")); return; }
        foreach (var d in disks)
        {
            var (icon, brush) = SecurityPage.StateIcon(d.State);
            var header = new StackPanel();
            header.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{d.Name}{(d.Letters != "" ? $"  ({d.Letters}:)" : "")}", FontWeight = FontWeights.SemiBold });
            var facts = new List<string> { $"{(d.Bus == "NVMe" ? "SSD NVMe" : $"{d.Media} {d.Bus}")} · {Store.FormatBytes(d.Size)}" };
            if (d.TempC is { } t) facts.Add($"{t} °C{(t >= 70 ? " (caliente)" : "")}");
            if (d.WearPct is { } w) facts.Add($"vida útil gastada {w} %");
            if (d.PowerOnHours is { } h) facts.Add($"{h:N0} h encendido");
            if (d.ReadErrors is { } re && d.WriteErrors is { } we) facts.Add(re + we == 0 ? "sin errores" : $"{re + we} errores de lectura/escritura");
            header.Children.Add(Ui.Text(string.Join(" · ", facts), "Muted", 0));
            if (d.State is CheckState.Warning or CheckState.Bad)
                header.Children.Add(Ui.Text("Haz copia de los archivos importantes de este disco cuanto antes.", null, 0));
            var label = d.State switch { CheckState.Ok => "Sano", CheckState.Warning => "Atención", CheckState.Bad => "Fallando", _ => "Desconocido" };
            DiskHealthList.Children.Add(new CardControl
            {
                Icon = new SymbolIcon(icon) { FontSize = 22, Foreground = Ui.Res(brush) }, Header = header, Margin = new Thickness(0, 0, 0, 6),
                Content = Ui.Pill(label, danger: d.State is CheckState.Bad or CheckState.Warning, accent: d.State == CheckState.Ok),
            });
        }
    }

    void DeepClean_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.Navigate(typeof(CleanerPage));
    void ShowDownloads_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = DownloadsTab;
    void StorageSense_Click(object sender, RoutedEventArgs e) => Ui.OpenSettings("ms-settings:storagesense");
    void Locate_Click(object sender, RoutedEventArgs e) => Ui.ShowInFolder((string)((FrameworkElement)sender).Tag);

    void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is FolderRow f) Ui.OpenFolder(f.Path);
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var root = (string)ScanDrive.SelectedItem;
        _cts = new CancellationTokenSource();
        ScanBtn.Content = "Cancelar";
        ScanBtn.Appearance = ControlAppearance.Secondary;
        var progress = new Progress<string>(s => ScanStatus.Text = s);
        try
        {
            var r = await Task.Run(() => Storage.Scan(root, 100L << 20, progress, _cts.Token));
            FolderList.ItemsSource = r.Folders.Take(80).Select(f => new FolderRow(f, r.Bytes)).ToList();
            LargeList.ItemsSource = r.Large.Select(f => new FileRow(f)).ToList();
            ScanStatus.Text = $"{r.Files:N0} archivos analizados · {Store.FormatBytes(r.Bytes)} en {root}. Las carpetas del sistema a las que no hay acceso no se cuentan.";
            ScanResults.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Análisis cancelado."; }
        catch (Exception ex) { ScanStatus.Text = $"Error: {ex.Message}"; }
        finally
        {
            _cts = null;
            ScanBtn.Content = "Analizar";
            ScanBtn.Appearance = ControlAppearance.Primary;
        }
    }

    void SelectOldInstallers_Click(object sender, RoutedEventArgs e)
    {
        string[] kinds = [".exe", ".msi", ".zip", ".rar", ".7z", ".iso"];
        foreach (FileRow r in DownloadList.Items)
            r.Selected = kinds.Contains(System.IO.Path.GetExtension(r.Path).ToLowerInvariant()) && r.File.Modified < DateTime.Now.AddDays(-30);
    }

    void UnselectDownloads_Click(object sender, RoutedEventArgs e)
    {
        foreach (FileRow r in DownloadList.Items) r.Selected = false;
    }

    // ---------- duplicates ----------
    void UpdateDupFolders() => DupFolders.Text = "Carpetas: " + string.Join("  ·  ", _dupFolders);

    void AddDupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta donde buscar duplicados" };
        if (dlg.ShowDialog() != true) return;
        if (Storage.IsProtected(dlg.FolderName + "\\x"))
        { Ui.Toast("Carpeta del sistema", "En carpetas de Windows y de programas no se buscan duplicados: muchos son necesarios a propósito.", ControlAppearance.Caution); return; }
        _dupFolders.Add(dlg.FolderName);
        UpdateDupFolders();
    }

    async void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        DupBtn.Content = "Cancelar";
        var min = long.Parse((string)((ComboBoxItem)DupMin.SelectedItem).Tag);
        var progress = new Progress<string>(s => DupStatus.Text = s);
        try
        {
            DupStatus.Text = "Buscando…";
            var groups = await Task.Run(() => Storage.Duplicates(_dupFolders, min, progress, _cts.Token));
            var rows = new List<FileRow>();
            var g = 0;
            foreach (var grp in groups)
            {
                g++;
                rows.Add(new FileRow(grp.Files[0], $"{grp.Files.Count} copias de {grp.Files[0].Name} · {Store.FormatBytes(grp.Size)} cada una") { Group = g });
                // keep the oldest, tick the rest
                rows.AddRange(grp.Files.Select((f, i) => new FileRow(f) { Group = g, Selected = i > 0 }));
            }
            DupList.ItemsSource = rows;
            DupList.Visibility = DupRecycle.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var waste = groups.Sum(x => x.Size * (x.Files.Count - 1));
            DupStatus.Text = groups.Count == 0 ? "No hay duplicados." : $"{groups.Count} grupos · {Store.FormatBytes(waste)} en copias repetidas";
        }
        catch (OperationCanceledException) { DupStatus.Text = "Búsqueda cancelada."; }
        catch (Exception ex) { DupStatus.Text = $"Error: {ex.Message}"; } // e.g. a chosen folder that was removed meanwhile
        finally { _cts = null; DupBtn.Content = "Buscar duplicados"; }
    }

    // ---------- recycle ----------
    async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var list = (ListBox)FindName((string)((FrameworkElement)sender).Tag);
        var rows = list.Items.Cast<FileRow>().ToList();
        var chosen = rows.Where(r => r.Selected && !r.IsHeader).ToList();
        if (chosen.Count == 0) { Ui.Toast("Marca algún archivo", "Selecciona los archivos que quieres enviar a la Papelera.", ControlAppearance.Caution); return; }
        if (list == DupList)
        {
            // never every copy of a group
            var all = rows.Where(r => !r.IsHeader).GroupBy(r => r.Group).FirstOrDefault(gr => gr.All(r => r.Selected));
            if (all != null) { Ui.Toast("Deja al menos una copia", $"Has marcado todas las copias de \"{all.First().Name}\". Desmarca la que quieras conservar.", ControlAppearance.Caution); return; }
        }
        var total = chosen.Sum(r => r.File.Size);
        var panel = new StackPanel { Width = 720 };
        panel.Children.Add(Ui.Text("Irán a la Papelera de reciclaje: podrás recuperarlos desde allí mientras no la vacíes.", null, 4));
        panel.Children.Add(new ListBox { Height = 300, Margin = new Thickness(0, 8, 0, 0), Style = (Style)Application.Current.FindResource("FileList"),
            ItemsSource = chosen.Select(r => new CleanItem { Path = r.Path, Display = r.Path, Size = r.File.Size }).ToList() });
        if (!await Ui.Confirm($"¿Enviar {CategoryRow.Files(chosen.Count)} ({Store.FormatBytes(total)}) a la Papelera?", panel, "Enviar a la Papelera", danger: true)) return;
        var (recycled, skipped) = await Task.Run(() => Storage.Recycle(chosen.Select(r => r.File).ToList()));
        Ui.Toast("Enviado a la Papelera", $"{CategoryRow.Files(recycled)} en la Papelera." + (skipped > 0 ? $" {skipped} no (en uso, protegidos o ya no estaban)." : ""),
            skipped > 0 ? ControlAppearance.Caution : ControlAppearance.Success);
        list.ItemsSource = rows.Where(r => r.IsHeader || File.Exists(r.Path)).ToList();
        await Init();
    }
}
