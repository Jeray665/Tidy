using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tidy.Core;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public class ProgramRow(InstalledProgram p)
{
    public InstalledProgram Program => p;
    public string Name => p.Name;
    public string Detail => string.Join(" · ", new[] { p.Publisher, p.Version, p.Source, p.Installed?.ToString("dd/MM/yyyy") }.Where(s => !string.IsNullOrEmpty(s)));
    public string SizeText => p.Size > 0 ? Store.FormatBytes(p.Size) : "";
    public ImageSource? Icon => Ui.FileIcon(p.Icon);
    public Visibility SharedVisibility => p.Shared ? Visibility.Visible : Visibility.Collapsed;
}

public class ProcRow(ProcInfo p) : Observable
{
    public ProcInfo Info { get; private set; } = p;
    public string Key => Info.Path ?? Info.Name;
    public string Title => Info.Description;
    public string? Path => Info.Path;
    public string Detail => $"{Info.Name}.exe" + (Info.Count > 1 ? $" · {Info.Count} procesos" : "") + (Info.Critical ? " · sistema" : "");
    public string CpuText => $"{Info.Cpu:0.0} %";
    public string MemText => Store.FormatBytes(Info.Memory);
    public bool CanClose => !Info.Critical;
    public ImageSource? Icon => Ui.FileIcon(Info.Path);
    public void Update(ProcInfo p) { Info = p; Changed(); }
}

public class LeftoverRow(Leftover l) : Observable
{
    public Leftover Item => l;
    public bool Selected { get => l.Selected; set { l.Selected = value; Changed(); } }
    public string Display => l.Display;
    public string Reason => l.Reason + (l.Size > 0 ? $" · {Store.FormatBytes(l.Size)}" : "");
}

public partial class AppsPage : Page
{
    List<ProgramRow> _programs = [];
    readonly ObservableCollection<ProcRow> _procs = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _busy;

    public AppsPage()
    {
        InitializeComponent();
        ProcList.ItemsSource = _procs;
        _timer.Tick += async (_, _) => await RefreshProcs();
        Loaded += async (_, _) => await LoadPrograms();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    async Task LoadPrograms()
    {
        AppsSummary.Text = "Cargando programas…";
        _programs = (await Task.Run(Uninstaller.List)).Select(p => new ProgramRow(p)).ToList();
        AppsSummary.Text = $"{_programs.Count} programas instalados · {Store.FormatBytes(_programs.Sum(p => p.Program.Size))}";
        AppSearch_Changed(this, new RoutedEventArgs());
    }

    void AppSearch_Changed(object sender, RoutedEventArgs e)
    {
        if (AppList == null || AppSort == null) return;
        var q = AppSearch.Text?.Trim() ?? "";
        IEnumerable<ProgramRow> rows = _programs.Where(p => q == "" || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Program.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        rows = AppSort.SelectedIndex switch
        {
            1 => rows.OrderByDescending(p => p.Program.Size),
            2 => rows.OrderByDescending(p => p.Program.Installed ?? DateTime.MinValue),
            _ => rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        AppList.ItemsSource = rows.ToList();
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var p = ((ProgramRow)((FrameworkElement)sender).Tag).Program;
        var panel = new StackPanel { Width = 640 };
        panel.Children.Add(Ui.Text($"{p.Name} {p.Version}\n{p.Publisher}", null, 10));
        if (p.Shared)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 10),
                Title = "Componente compartido", Message = "Juegos y programas lo usan para funcionar (por ejemplo Visual C++ o .NET). Si lo quitas, alguno podría dejar de abrir." });
        panel.Children.Add(Ui.Text("1.  Se crea un punto de restauración.\n2.  Se abre el desinstalador del propio programa: síguelo como siempre.\n3.  Tidy busca lo que haya dejado y te enseña la lista para que elijas qué quitar.", null, 10));
        panel.Children.Add(Ui.Text("\"Solo quitar restos\" es para programas rotos cuyo desinstalador ya no funciona: no lo ejecuta y va directo a la lista de restos (incluida la carpeta del programa).", "Muted", 0));
        var answer = await Ui.Ask($"¿Desinstalar {p.Name}?", panel, "Desinstalar", "Solo quitar restos", danger: p.Shared);
        if (answer == ContentDialogResult.None) return;
        if (!await Ui.EnsureRestorePoint($"Desinstalar {p.Name}")) return;

        _busy = true;
        try
        {
            if (answer == ContentDialogResult.Primary)
            {
                AppsSummary.Text = $"Esperando a que termine el desinstalador de {p.Name}…";
                var (ok, msg) = await Uninstaller.RunUninstaller(p);
                if (!ok)
                {
                    if (!await Ui.Confirm("No se pudo abrir el desinstalador", $"{msg}\n\n¿Buscar sus restos igualmente?", "Buscar restos")) return;
                }
                else
                {
                    // many uninstallers hand off to a copy of themselves and exit at once: wait until the program is really gone
                    AppsSummary.Text = $"Comprobando que {p.Name} se ha desinstalado…";
                    var gone = false;
                    for (var i = 0; i < 90 && !gone; i++) { gone = !await Task.Run(() => Uninstaller.StillInstalled(p)); if (!gone) await Task.Delay(2000); }
                    if (!gone && !await Ui.Confirm($"{p.Name} sigue instalado", "Puede que hayas cancelado el desinstalador o que todavía esté abierto.\n\nSi buscas restos ahora, la lista incluirá los archivos del propio programa.", "Buscar restos igualmente"))
                        return;
                }
            }
            AppsSummary.Text = $"Buscando restos de {p.Name}…";
            var left = await Task.Run(() => Uninstaller.FindLeftovers(p));
            if (left.Count == 0) { Ui.Toast("Sin restos", $"{p.Name} no ha dejado nada. Desinstalación limpia."); return; }
            var rows = left.Select(l => new LeftoverRow(l)).ToList();
            if (!await Ui.Confirm($"{p.Name} ha dejado {left.Count} restos", LeftoversPanel(rows), "Quitar seleccionados")) return;
            var chosen = rows.Where(r => r.Selected).Select(r => r.Item).ToList();
            if (chosen.Count == 0) return;
            AppsSummary.Text = "Moviendo restos a la cuarentena…";
            var q = await Task.Run(() => Uninstaller.Remove(p.Name, chosen));
            var missed = chosen.Count - q.Items.Count;
            Ui.Toast("Desinstalación profunda completada",
                $"{q.Items.Count} restos quitados ({Store.FormatBytes(q.Bytes)}). Están en Copias de seguridad › Cuarentena por si necesitas recuperarlos." +
                (missed > 0 ? $" {missed} no se pudieron quitar (en uso o protegidos)." : ""), missed > 0 ? ControlAppearance.Caution : ControlAppearance.Success);
        }
        finally
        {
            _busy = false;
            await LoadPrograms();
        }
    }

    static StackPanel LeftoversPanel(List<LeftoverRow> rows)
    {
        var panel = new StackPanel { Width = 760 };
        panel.Children.Add(Ui.Text("Está todo marcado excepto partidas guardadas y documentos. Desmarca lo que quieras conservar. Nada se borra: se mueve a la cuarentena de Tidy y se puede restaurar desde Copias de seguridad.", "Muted", 8));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var all = new Wpf.Ui.Controls.Button { Content = "Marcar todo", Margin = new Thickness(0, 0, 6, 0) };
        var none = new Wpf.Ui.Controls.Button { Content = "Desmarcar todo" };
        all.Click += (_, _) => rows.ForEach(r => r.Selected = true);
        none.Click += (_, _) => rows.ForEach(r => r.Selected = false);
        buttons.Children.Add(all);
        buttons.Children.Add(none);
        panel.Children.Add(buttons);

        var stack = new StackPanel();
        foreach (var g in rows.GroupBy(r => r.Item.Category))
        {
            stack.Children.Add(new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (var r in g)
            {
                var cb = new System.Windows.Controls.CheckBox { DataContext = r, Margin = new Thickness(0, 2, 0, 2) };
                cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(LeftoverRow.Selected)) { Mode = System.Windows.Data.BindingMode.TwoWay });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = r.Display, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = r.Display, FontSize = 12 });
                text.Children.Add(new TextBlock { Text = r.Reason, FontSize = 11, Foreground = Ui.Res("TextFillColorTertiaryBrush") });
                cb.Content = text;
                stack.Children.Add(cb);
            }
        }
        panel.Children.Add(new ScrollViewer { Content = stack, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return panel;
    }

    // ---------------- processes ----------------
    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        UpdateTimer();
        if (Tabs.SelectedItem == WingetTab && !_wingetLoaded) { _wingetLoaded = true; _ = LoadWinget(); }
    }

    void UpdateTimer()
    {
        var on = IsVisible && Tabs.SelectedIndex == 1;
        if (on && !_timer.IsEnabled) { _timer.Start(); _ = RefreshProcs(resort: true); }
        else if (!on) _timer.Stop();
    }

    async Task RefreshProcs(bool resort = false)
    {
        var list = await Task.Run(Processes.List);
        var q = ProcSearch.Text?.Trim() ?? "";
        list = list.Where(p => q == "" || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var byKey = list.ToDictionary(p => p.Path ?? p.Name, StringComparer.OrdinalIgnoreCase);
        // update in place so the list doesn't jump while you're looking at it
        foreach (var row in _procs.ToList())
            if (byKey.Remove(row.Key, out var info)) row.Update(info); else _procs.Remove(row);
        foreach (var info in byKey.Values) _procs.Add(new ProcRow(info));
        if (resort)
        {
            var sorted = ProcSort.SelectedIndex switch
            {
                1 => _procs.OrderByDescending(r => r.Info.Cpu),
                2 => _procs.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => _procs.OrderByDescending(r => r.Info.Memory),
            };
            var tmp = sorted.ToList();
            _procs.Clear();
            foreach (var r in tmp) _procs.Add(r);
        }
        var ram = SysMon.Ram();
        ProcSummary.Text = $"{list.Sum(p => p.Count)} procesos · RAM {ram.UsedGb:0.0} de {ram.TotalGb:0} GB · CPU {list.Sum(p => p.Cpu):0} %";
    }

    async void ProcSort_Changed(object sender, RoutedEventArgs e)
    {
        if (ProcList == null || !IsLoaded) return;
        await RefreshProcs(resort: true);
    }

    async void ProcClose_Click(object sender, RoutedEventArgs e)
    {
        var row = (ProcRow)((FrameworkElement)sender).Tag;
        if (!await Ui.Confirm($"¿Cerrar {row.Title}?", $"Se cierra sin guardar{(row.Info.Count > 1 ? $" (sus {row.Info.Count} procesos)" : "")}: si tiene trabajo sin guardar, se perderá. Es como \"Finalizar tarea\" del Administrador de tareas.", "Cerrar", danger: true)) return;
        var (n, err) = await Task.Run(() => Processes.Close(row.Info));
        Ui.Toast(n > 0 ? "Cerrado" : "No se pudo cerrar", n > 0 ? $"{row.Title} cerrado." : err, n > 0 ? ControlAppearance.Success : ControlAppearance.Danger);
        await RefreshProcs();
    }

    void ProcMore_Click(object sender, RoutedEventArgs e)
    {
        var btn = (FrameworkElement)sender;
        var row = (ProcRow)btn.Tag;
        var menu = new ContextMenu();
        var trim = new MenuItem { Header = "Liberar memoria que no usa", Icon = Ui.Icon("ArrowMinimize24", 16) };
        trim.Click += async (_, _) => { var n = await Task.Run(() => Processes.Trim(row.Info)); Ui.Toast("Memoria liberada", $"{row.Title}: Windows ha recuperado la memoria que no estaba usando ({n} procesos)."); };
        menu.Items.Add(trim);
        var prio = new MenuItem { Header = "Prioridad", Icon = Ui.Icon("TopSpeed24", 16), IsEnabled = !row.Info.Critical };
        foreach (var (label, cls) in new[] { ("Alta", ProcessPriorityClass.High), ("Por encima de lo normal", ProcessPriorityClass.AboveNormal), ("Normal", ProcessPriorityClass.Normal),
                                             ("Por debajo de lo normal", ProcessPriorityClass.BelowNormal), ("Baja", ProcessPriorityClass.Idle) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Ui.Toast(Processes.SetPriority(row.Info, cls) ? "Prioridad cambiada" : "No se pudo cambiar del todo",
                $"{row.Title}: {label.ToLowerInvariant()}. Vuelve a normal al cerrar el programa.");
            prio.Items.Add(item);
        }
        menu.Items.Add(prio);
        if (row.Path != null)
        {
            var loc = new MenuItem { Header = "Abrir ubicación del archivo", Icon = Ui.Icon("FolderOpen24", 16) };
            loc.Click += (_, _) => Ui.ShowInFolder(row.Path);
            menu.Items.Add(loc);
        }
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }
}
