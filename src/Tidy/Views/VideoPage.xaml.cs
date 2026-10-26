using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public partial class VideoPage : Page
{
    const int MaxFiles = 500;
    readonly List<CopyRow> _rows = [];
    string? _dest;
    CancellationTokenSource? _cts;
    List<CopyResult> _last = [];

    public VideoPage() => InitializeComponent();

    string Mode => (string)((ComboBoxItem)ModeBox.SelectedItem).Tag;

    // ---------- choosing files ----------
    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = I18n.T("Vídeos"), Multiselect = true,
            Filter = $"{I18n.T("Vídeos y audio")}|{string.Join(";", Video.Readable.Select(x => "*" + x))}",
        };
        if (dlg.ShowDialog() == true) Add(dlg.FileNames);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Carpeta con vídeos") };
        if (dlg.ShowDialog() != true) return;
        // only that folder, not its subfolders: no surprises with thousands of files from somewhere else
        try { Add(Directory.EnumerateFiles(dlg.FolderName).Where(Video.IsMedia).Order()); }
        catch (Exception ex) { Ui.Toast("No se pudo leer la carpeta", ex.Message, ControlAppearance.Caution); }
    }

    void Add(IEnumerable<string> paths)
    {
        var known = _rows.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var p in paths)
        {
            if (!Video.IsMedia(p) || !File.Exists(p) || known.Contains(p)) { skipped++; continue; }
            if (_rows.Count >= MaxFiles) { Ui.Toast("Demasiados archivos", $"Como mucho {MaxFiles} de una vez.", ControlAppearance.Caution); break; }
            _rows.Add(new CopyRow(p));
            known.Add(p);
        }
        if (skipped > 0 && _rows.Count == 0) Ui.Toast("Ningún vídeo", "Esa carpeta no tiene vídeos ni audios que Tidy sepa abrir.", ControlAppearance.Caution);
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
        FilesInfo.Text = _rows.Count == 0 ? "MP4, MOV, MKV, AVI, WMV y más. También audio: MP3, WAV, FLAC, M4A y WMA."
            : $"{_rows.Count} archivos · {Store.FormatBytes(_rows.Sum(r => r.Size))}";
        GoBtn.IsEnabled = _rows.Count > 0 && _cts == null;
        GoBtn.Content = _rows.Count > 0 ? $"Crear {_rows.Count} copias" : "Crear copias";
    }

    // ---------- options ----------
    void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QualityBox == null) return; // still being built
        // quality and size only matter when a video comes out
        QualityBox.IsEnabled = SizeBox.IsEnabled = Mode == "mp4";
        if (NextTo != null) NextTo.Content = $"La misma carpeta que cada original, como \"nombre (Tidy).{Mode}\"";
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
        var job = new VideoJob(Mode, int.Parse((string)((ComboBoxItem)QualityBox.SelectedItem).Tag), int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag),
            Elsewhere.IsChecked == true ? _dest : null);
        var files = _rows.Select(r => r.Path).ToList();

        var panel = new StackPanel { Width = 680 };
        var what = new List<string>();
        if (job.Mode == "mp4")
        {
            what.Add("vídeo MP4");
            what.Add(job.Quality switch { 0 => "calidad alta", 1 => "calidad equilibrada", _ => "calidad pequeña" });
            if (job.MaxShortSide > 0) what.Add($"como mucho {job.MaxShortSide}p");
        }
        else what.Add($"solo el sonido, en {job.Mode.ToUpperInvariant()}");
        panel.Children.Add(Ui.Text(string.Join(" · ", what), null, 4));
        panel.Children.Add(Ui.Text(job.OutDir == null ? "Cada copia se guarda junto a su original, con \"(Tidy)\" en el nombre." : $"Las copias se guardan en {job.OutDir}.", "Muted", 0));
        panel.Children.Add(Ui.Lines(files, 220));
        panel.Children.Add(Ui.Text("Los originales no se tocan ni se borran, y nunca se sobrescribe ningún archivo. Desde Historial puedes mandar todas las copias a la Papelera.", "Muted", 0));
        if (!await Ui.Confirm($"¿Crear {files.Count} copias?", panel, "Crear copias")) return;

        _cts = new CancellationTokenSource();
        GoBtn.IsEnabled = false;
        OpenBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Status.Text = "Preparando…";
        foreach (var r in _rows) r.Show("En cola");
        // made here, so they report on this (UI) thread
        var progress = new Progress<double>(p => { Progress.Value = p; Status.Text = $"{p * 100:0} %"; });
        var each = new Progress<(int Index, double Done)>(p => { if (p.Index < _rows.Count && p.Done < 1) _rows[p.Index].Show($"Convirtiendo… {p.Done * 100:0} %"); });
        var token = _cts.Token;
        try
        {
            _last = await Task.Run(() => Video.Process(files, job, progress, each, token));
            ShowResults();
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Cancelado. Las copias ya terminadas se quedan (puedes mandarlas a la Papelera desde Historial); la que estaba a medias no se guarda.";
            foreach (var r in _rows.Where(r => r.Status == "En cola" || r.Status.StartsWith("Convirtiendo"))) r.Show("");
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
        Status.Text = made.Count == 0 ? "No se ha podido crear ninguna copia: mira el motivo en cada archivo."
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
