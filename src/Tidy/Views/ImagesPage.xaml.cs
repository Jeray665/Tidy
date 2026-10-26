using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class CopyRow(string path) : Observable
{
    public string Path => path;
    public string Name => System.IO.Path.GetFileName(path);
    public long Size { get; } = new FileInfo(path).Length;
    public string SizeText => Store.FormatBytes(Size);
    string _status = "";
    bool _problem;
    public string Status => _status;
    public Brush StatusBrush => Ui.Res(_problem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
    public void Show(string status, bool problem = false) { _status = status; _problem = problem; Changed(); }
}

public partial class ImagesPage : Page
{
    const int MaxFiles = 5000;
    readonly List<CopyRow> _rows = [];
    string? _dest;
    CancellationTokenSource? _cts;
    List<CopyResult> _last = [];

    public ImagesPage()
    {
        InitializeComponent();
        Quality_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, Quality.Value));
    }

    string Format => (string)((ComboBoxItem)FormatBox.SelectedItem).Tag;

    // ---------- choosing images ----------
    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = I18n.T("Imágenes"), Multiselect = true,
            Filter = $"{I18n.T("Imágenes")}|{string.Join(";", Images.Readable.Select(x => "*" + x))}",
        };
        if (dlg.ShowDialog() == true) Add(dlg.FileNames);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Carpeta con imágenes") };
        if (dlg.ShowDialog() != true) return;
        // only that folder, not its subfolders: no surprises with thousands of files from somewhere else
        try { Add(Directory.EnumerateFiles(dlg.FolderName).Where(Images.IsImage).Order()); }
        catch (Exception ex) { Ui.Toast("No se pudo leer la carpeta", ex.Message, ControlAppearance.Caution); }
    }

    void Add(IEnumerable<string> paths)
    {
        var known = _rows.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var p in paths)
        {
            if (!Images.IsImage(p) || !File.Exists(p) || known.Contains(p)) { skipped++; continue; }
            if (_rows.Count >= MaxFiles) { Ui.Toast("Demasiadas imágenes", $"Como mucho {MaxFiles} de una vez.", ControlAppearance.Caution); break; }
            _rows.Add(new CopyRow(p));
            known.Add(p);
        }
        if (skipped > 0 && _rows.Count == 0) Ui.Toast("Ninguna imagen", "Esa carpeta no tiene imágenes que Tidy sepa abrir.", ControlAppearance.Caution);
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _rows.Remove((CopyRow)((FrameworkElement)sender).Tag);
        Refresh();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _rows.Clear();
        Refresh();
    }

    void Refresh()
    {
        FileList.ItemsSource = null;
        FileList.ItemsSource = _rows;
        FileList.Visibility = ClearBtn.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesInfo.Text = _rows.Count == 0 ? "JPG, PNG, HEIC, WebP, AVIF, BMP, TIFF, GIF e ICO."
            : $"{_rows.Count} imágenes · {Store.FormatBytes(_rows.Sum(r => r.Size))}";
        GoBtn.IsEnabled = _rows.Count > 0 && _cts == null;
        GoBtn.Content = _rows.Count > 0 ? $"Crear {_rows.Count} copias" : "Crear copias";
    }

    // ---------- options ----------
    void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Quality == null) return; // still being built
        // the quality slider only matters when JPGs come out
        Quality.IsEnabled = Format is "" or "jpg";
        Quality_Changed(this, new RoutedPropertyChangedEventArgs<double>(0, Quality.Value));
    }

    void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText == null) return;
        var q = (int)e.NewValue;
        QualityText.Text = $"{q}";
        QualityHint.Text = !Quality.IsEnabled ? "Solo se usa al guardar en JPG."
            : q >= 95 ? "Casi igual que el original, pero ocupa bastante."
            : q >= 80 ? "Se ve igual que el original y ocupa mucho menos. Recomendado."
            : q >= 65 ? "Ocupa muy poco; de cerca se nota un poco."
            : "Lo más pequeño posible; se notan defectos.";
    }

    void Elsewhere_Checked(object sender, RoutedEventArgs e) { if (_dest == null) ChooseDest_Click(sender, e); }

    void ChooseDest_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Carpeta donde guardar las copias") };
        if (dlg.ShowDialog() != true) { if (_dest == null) NextTo.IsChecked = true; return; }
        if (Storage.IsProtected(Path.Combine(dlg.FolderName, "x")))
        {
            Ui.Toast("Carpeta del sistema", "No se guardan archivos en carpetas de Windows ni de programas.", ControlAppearance.Caution);
            if (_dest == null) NextTo.IsChecked = true;
            return;
        }
        _dest = dlg.FolderName;
        DestText.Text = _dest;
        Elsewhere.IsChecked = true;
    }

    // ---------- run ----------
    async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || _cts != null) return;
        var job = new ImageJob(Format, (int)Quality.Value, int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag),
            StripBox.IsChecked == true, Elsewhere.IsChecked == true ? _dest : null);
        var files = _rows.Select(r => r.Path).ToList();

        var panel = new StackPanel { Width = 680 };
        var what = new List<string>();
        what.Add(job.Format == "" ? "mismo formato" : job.Format.ToUpperInvariant());
        if (Images.TargetFormat("x.jpg", job.Format) == "jpg") what.Add($"calidad {job.Quality}");
        if (job.MaxSide > 0) what.Add($"como mucho {job.MaxSide} px");
        if (job.StripData) what.Add("sin datos ocultos");
        panel.Children.Add(Ui.Text(string.Join(" · ", what), null, 4));
        panel.Children.Add(Ui.Text(job.OutDir == null ? "Cada copia se guarda junto a su original, con \"(Tidy)\" en el nombre." : $"Las copias se guardan en {job.OutDir}.", "Muted", 0));
        panel.Children.Add(Ui.Lines(files, 220));
        panel.Children.Add(Ui.Text("Los originales no se tocan ni se borran, y nunca se sobrescribe ningún archivo. Desde Historial puedes mandar todas las copias a la Papelera.", "Muted", 0));
        if (!await Ui.Confirm($"¿Crear {files.Count} copias?", panel, "Crear copias")) return;

        _cts = new CancellationTokenSource();
        GoBtn.IsEnabled = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Progress.Visibility = Visibility.Visible;
        Progress.Maximum = files.Count;
        Progress.Value = 0;
        foreach (var r in _rows) r.Show("En cola");
        var progress = new Progress<int>(n => { Progress.Value = n; Status.Text = $"{n} de {files.Count}…"; });
        try
        {
            _last = await Task.Run(() => Images.Process(files, job, progress, _cts.Token));
            ShowResults();
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Cancelado. Las copias ya hechas se quedan (puedes mandarlas a la Papelera desde Historial).";
            foreach (var r in _rows.Where(r => r.Status == "En cola")) r.Show("");
        }
        catch (Exception ex) { Status.Text = $"Error: {ex.Message}"; }
        finally
        {
            _cts = null;
            CancelBtn.Visibility = Progress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    void ShowResults()
    {
        var byPath = _last.ToDictionary(r => r.From, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            if (!byPath.TryGetValue(row.Path, out var r)) continue;
            if (r.To == null) { row.Show(r.Note ?? "No se pudo", problem: true); continue; }
            var change = r.Before > 0 ? (r.After - r.Before) * 100.0 / r.Before : 0;
            row.Show($"→ {Path.GetFileName(r.To)} · {Store.FormatBytes(r.After)} ({(change <= 0 ? "" : "+")}{change:0} %)" + (r.Note != null ? $" · {r.Note}" : ""), r.Note != null);
        }
        var made = _last.Where(r => r.To != null).ToList();
        var failed = _last.Count - made.Count;
        long before = made.Sum(r => r.Before), after = made.Sum(r => r.After);
        Status.Text = made.Count == 0 ? "No se ha podido crear ninguna copia: mira el motivo en cada imagen."
            : $"{made.Count} copias creadas · {Store.FormatBytes(before)} → {Store.FormatBytes(after)}"
              + (before > after ? " · " + $"{(before - after) * 100.0 / before:0} % menos" : "") // its own text, so it translates on its own
              + (failed > 0 ? $" · {failed} no se pudieron (mira el motivo en cada una)" : "");
        OpenBtn.Visibility = made.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Ui.Toast(made.Count > 0 ? "Copias creadas" : "No se creó ninguna copia", Status.Text, failed > 0 ? ControlAppearance.Caution : ControlAppearance.Success);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_last.FirstOrDefault(r => r.To != null && File.Exists(r.To)) is { } r) Ui.ShowInFolder(r.To!);
    }
}
