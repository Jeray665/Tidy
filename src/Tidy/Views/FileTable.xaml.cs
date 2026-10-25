using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public partial class FileTable : UserControl
{
    static readonly (FileKind Kind, string Name)[] KindNames =
        [(FileKind.Document, "Documentos"), (FileKind.Image, "Imágenes"), (FileKind.Archive, "Comprimidos"), (FileKind.Video, "Vídeos"), (FileKind.Audio, "Audio"), (FileKind.Other, "Otros")];

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(FileTable));
    /// <summary>The tab's own buttons, at the right of the "N marcados" line; usable only while something is ticked.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <summary>Index of the size filter to start on (0 = any size).</summary>
    public int StartSize { set => MinSize.SelectedIndex = value; }
    /// <summary>What the line under the list says while nothing is ticked.</summary>
    public string Hint { get; set; } = "";
    /// <summary>Progress of a long action (moving to another disk) on the line under the list.</summary>
    public string Status { set => Sel.Text = value; }

    List<FileRow> _rows = [];
    FileKind? _kind;
    string _sort = "size";
    bool _desc = true;

    public FileTable() => InitializeComponent();

    /// <summary>Every file of the tab. Duplicates come as header + copies per group, and filters and sorting move whole groups.</summary>
    public List<FileRow> Rows
    {
        get => _rows;
        set
        {
            foreach (var r in _rows) r.PropertyChanged -= OnRowChanged;
            _rows = value;
            foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
            Show();
        }
    }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ShowSelection();

    /// <summary>Ticked files, also those a filter hides right now: the confirmation lists them all anyway.</summary>
    public List<FileRow> Chosen => _rows.Where(r => r.Selected && !r.IsHeader).ToList();

    /// <summary>Drops the rows whose file is gone (after sending to the Recycle Bin or moving), and groups left with one copy.</summary>
    public void Prune()
    {
        var left = _rows.Where(r => r.IsHeader || System.IO.File.Exists(r.Path)).ToList();
        // a group with one copy left (or none) has nothing duplicated any more
        var dup = left.Where(r => !r.IsHeader && r.Group > 0).GroupBy(r => r.Group).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet();
        Rows = left.Where(r => r.Group == 0 || dup.Contains(r.Group)).ToList();
    }

    void Show()
    {
        var min = long.Parse((string)((ComboBoxItem)MinSize.SelectedItem).Tag);
        // one entry per file, or per group of copies (header + copies, its first copy stands for the group)
        var all = new List<(FileRow Lead, List<FileRow> Rows)>();
        var groups = new Dictionary<int, List<FileRow>>();
        foreach (var r in _rows)
        {
            if (r.Group == 0) { all.Add((r, [r])); continue; }
            if (!groups.TryGetValue(r.Group, out var g)) groups[r.Group] = g = [];
            g.Add(r);
        }
        all.AddRange(groups.Values.Select(g => (g.First(r => !r.IsHeader), g)));
        var units = all.Where(u => u.Lead.File.Size >= min).ToList();

        KindChips.Children.Clear();
        KindChips.Children.Add(Chip("Todo", units.Count, null));
        foreach (var (kind, name) in KindNames)
            if (units.Count(u => u.Lead.Kind == kind) is > 0 and var n) KindChips.Children.Add(Chip(name, n, kind));

        var shown = units.Where(u => _kind == null || u.Lead.Kind == _kind);
        Func<(FileRow Lead, List<FileRow> Rows), object> key = _sort switch
        {
            "name" => u => u.Lead.Name.ToLowerInvariant(),
            "date" => u => u.Lead.File.Modified,
            "path" => u => u.Lead.Path.ToLowerInvariant(),
            _ => u => u.Lead.File.Size,
        };
        shown = _desc ? shown.OrderByDescending(key) : shown.OrderBy(key);
        List.ItemsSource = shown.SelectMany(u => u.Rows).ToList();
        foreach (Wpf.Ui.Controls.Button b in Headers.Children)
            b.Icon = (string)b.Tag == _sort ? new SymbolIcon(_desc ? SymbolRegular.ArrowDown16 : SymbolRegular.ArrowUp16) : null;
        ShowSelection();
    }

    UIElement Chip(string name, int count, FileKind? kind)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new System.Windows.Controls.TextBlock { Text = name });
        label.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{count:N0}", Margin = new Thickness(6, 0, 0, 0), Opacity = 0.7 });
        var b = new Wpf.Ui.Controls.Button
        {
            Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
            Appearance = _kind == kind ? ControlAppearance.Primary : ControlAppearance.Secondary,
        };
        b.Click += (_, _) => { _kind = kind; Show(); };
        return b;
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Show(); }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        var by = (string)((FrameworkElement)sender).Tag;
        _desc = by == _sort ? !_desc : by is "size" or "date";
        _sort = by;
        Show();
    }

    void ShowSelection()
    {
        var chosen = Chosen;
        Sel.Text = chosen.Count == 0 ? Hint : $"{CategoryRow.Files(chosen.Count)} marcados · {Store.FormatBytes(chosen.Sum(r => r.File.Size))} en total";
        ActionsHost.IsEnabled = chosen.Count > 0;
    }

    void Locate_Click(object sender, RoutedEventArgs e) => Ui.ShowInFolder((string)((FrameworkElement)sender).Tag);
}
