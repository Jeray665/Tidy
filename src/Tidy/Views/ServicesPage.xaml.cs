using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Tidy.Views;

public class ServiceRow(ServiceInfo s)
{
    public ServiceInfo Info => s;
    public string Display => s.Display;
    public string Detail => $"{s.Name} · {s.Description}";
    public string? Hint => s.Hint;
    public string ModeText => s.StartMode;
    public bool CanChange => !s.Protected;
    public bool Running => s.State == "Running";
    public string StateText => Running ? "En ejecución" : s.State == "Stopped" ? "Detenido" : s.State;
    public Brush StateBrush => Ui.Res(Running ? "SystemFillColorSuccessBrush" : "ControlStrongFillColorDisabledBrush");
    public IconElement RunIcon => Ui.Icon(Running ? "Stop24" : "Play24", 16);
    public string RunTip => Running ? "Detener ahora" : "Iniciar ahora";
    public Visibility LockVisibility => s.Protected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChangedVisibility => s.Changed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => s.Hint != null ? Visibility.Visible : Visibility.Collapsed;
    public bool ThirdParty => !s.Path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
}

public class TaskRow(TaskInfo t)
{
    public TaskInfo Info => t;
    // each piece is its own known text so the English version translates all of them
    public string Detail => string.Join(" · ", new[] { t.Folder, t.LastRun is { } l ? $"última vez {l:dd/MM HH:mm}" : "nunca se ha ejecutado",
        t.NextRun is { } n ? $"próxima {n:dd/MM HH:mm}" : null }.Where(p => p != null));
    public string Tip => $"{t.Path}\n{t.Description}\n\nEjecuta: {t.Action}\nAutor: {t.Author}";
    public Visibility LockVisibility => t.CanChange ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChangedVisibility => t.Changed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => t.Hint != null ? Visibility.Visible : Visibility.Collapsed;
}

public partial class ServicesPage : Page
{
    List<ServiceRow> _services = [];
    List<TaskRow> _tasks = [];
    List<PresetItem> _unneeded = [];

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await LoadServices(); await LoadTasks(); await LoadUnneeded(); };
    }

    async Task LoadServices()
    {
        SvcSummary.Text = "Cargando servicios…";
        _services = (await Task.Run(Services.List)).Select(s => new ServiceRow(s)).ToList();
        SvcFilter_Changed(this, new RoutedEventArgs());
    }

    async Task LoadTasks()
    {
        TaskSummary.Text = "Cargando tareas…";
        _tasks = (await Task.Run(ScheduledTasks.List)).Select(t => new TaskRow(t)).ToList();
        TaskFilter_Changed(this, new RoutedEventArgs());
    }

    async Task LoadUnneeded()
    {
        UnneededBtn.IsEnabled = false;
        _unneeded = await Task.Run(Unneeded.Candidates);
        var n = _unneeded.Count;
        UnneededTitle.Text = n == 0 ? "No hay nada activo que sobre" : n == 1 ? "Hay 1 cosa activa que no hace falta" : $"Hay {n} cosas activas que no hacen falta";
        UnneededSub.Text = n == 0 ? "Los servicios y tareas que Tidy conoce ya están como deben."
            : "Telemetría, informes de errores y funciones que casi nadie usa. Verás la lista con el porqué de cada una antes de cambiar nada.";
        UnneededBtn.IsEnabled = n > 0;
    }

    async void Unneeded_Click(object sender, RoutedEventArgs e)
    {
        if (!await StartupPage.Review("Servicios y tareas que sobran",
                "Desmarca lo que quieras conservar. Los servicios pasan a manual (arrancan solo si algo los pide) o se deshabilitan donde se indica, y las tareas se desactivan. Todo se puede deshacer en Historial o con \"Restaurar todos\".",
                _unneeded, "Tidy: servicios y tareas que sobran")) return;
        await LoadServices();
        await LoadTasks();
        await LoadUnneeded();
    }

    void SvcFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (SvcList == null || SvcFilter == null) return;
        var q = SvcSearch.Text?.Trim() ?? "";
        var rows = _services.Where(s => (q == "" || s.Display.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.Info.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            && SvcFilter.SelectedIndex switch
            {
                1 => s.Hint != null, 2 => s.Info.Changed, 3 => s.Running, 4 => s.Info.StartMode == "Deshabilitado", 5 => s.ThirdParty, _ => true,
            }).ToList();
        SvcList.ItemsSource = rows;
        var changed = _services.Count(s => s.Info.Changed);
        SvcSummary.Text = $"{rows.Count} de {_services.Count} servicios · {_services.Count(s => s.Running)} en ejecución · {_services.Count(s => s.Info.Protected)} protegidos" +
                          (changed > 0 ? $" · {changed} cambiados por Tidy" : "");
        SvcRestoreAll.IsEnabled = changed > 0;
    }

    void TaskFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (TaskList == null || TaskFilter == null) return;
        var q = TaskSearch.Text?.Trim() ?? "";
        var rows = _tasks.Where(t => (q == "" || t.Info.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.Info.Action.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            && TaskFilter.SelectedIndex switch
            {
                1 => t.Info.CanChange, 2 => t.Info.Hint != null, 3 => t.Info.Changed, 4 => !t.Info.Enabled, _ => true,
            }).ToList();
        TaskList.ItemsSource = rows;
        TaskSummary.Text = $"{rows.Count} de {_tasks.Count} tareas · {_tasks.Count(t => t.Info.CanChange)} se pueden cambiar · las de Windows Update, Defender y mantenimiento son de solo lectura";
    }

    void SvcMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = (FrameworkElement)sender;
        var row = (ServiceRow)btn.Tag;
        var menu = new ContextMenu();
        foreach (var (label, mode, tip) in new[]
        {
            ("Automático", StartMode.Auto, "Arranca con Windows."),
            ("Automático (retrasado)", StartMode.Delayed, "Arranca un poco después de Windows, para que el inicio sea más rápido."),
            ("Manual", StartMode.Manual, "Solo arranca cuando algo lo necesita. La opción más segura para ahorrar recursos."),
            ("Deshabilitado", StartMode.Disabled, "No arranca nunca, ni aunque algo lo necesite."),
        })
        {
            var item = new MenuItem { Header = label, ToolTip = tip, IsChecked = row.ModeText == label };
            item.Click += async (_, _) => await SetMode(row, mode, label);
            menu.Items.Add(item);
        }
        if (row.Info.Changed)
        {
            menu.Items.Add(new Separator());
            var restore = new MenuItem { Header = "Volver a como estaba antes de Tidy", Icon = Ui.Icon("ArrowUndo24", 16) };
            restore.Click += async (_, _) =>
            {
                var ok = await Task.Run(() => Services.Restore(row.Info.Name));
                Ui.Toast(ok ? "Restaurado" : "No se pudo restaurar", ok ? $"{row.Display} vuelve a su configuración original." : "Revisa el registro de actividad.", ok ? ControlAppearance.Success : ControlAppearance.Danger);
                await LoadServices();
            };
            menu.Items.Add(restore);
        }
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    async Task SetMode(ServiceRow row, StartMode mode, string label)
    {
        if (row.ModeText == label) return;
        if (mode == StartMode.Disabled)
        {
            var panel = new StackPanel { Width = 600 };
            panel.Children.Add(Ui.Text(row.Info.Description, "Muted", 10));
            panel.Children.Add(Ui.Text(row.Hint ?? "Tidy no tiene información sobre este servicio. Si no sabes qué hace, mejor ponlo en Manual: así solo arranca cuando haga falta.", null, 10));
            var deps = Services.Dependents(row.Info.Name);
            if (deps.Length > 0) panel.Children.Add(Ui.Text($"Dependen de él: {string.Join(", ", deps)}", "Muted", 10));
            panel.Children.Add(Ui.Text("Se guarda cómo estaba para poder volver atrás (aquí o en Copias de seguridad).", "Muted", 0));
            if (!await Ui.Confirm($"¿Deshabilitar \"{row.Display}\"?", panel, "Deshabilitar", danger: row.Hint == null)) return;
        }
        if (!await Ui.EnsureRestorePoint("Servicios")) return;
        var (ok, msg) = await Task.Run(() => Services.SetMode(row.Info.Name, mode));
        Ui.Toast(ok ? "Tipo de inicio cambiado" : "No se pudo cambiar", ok ? $"{row.Display}: {label.ToLowerInvariant()}. Surte efecto en el próximo arranque." : msg,
            ok ? ControlAppearance.Success : ControlAppearance.Danger);
        await LoadServices();
    }

    async void SvcRun_Click(object sender, RoutedEventArgs e)
    {
        var row = (ServiceRow)((FrameworkElement)sender).Tag;
        if (row.Running && !await Ui.Confirm($"¿Detener \"{row.Display}\"?", "Se detiene ahora mismo hasta que algo lo vuelva a necesitar o reinicies. No cambia su tipo de inicio.", "Detener"))
            return;
        var (ok, msg) = await Task.Run(() => Services.SetRunning(row.Info.Name, !row.Running));
        Ui.Toast(ok ? (row.Running ? "Detenido" : "Iniciado") : "No se pudo", ok ? row.Display : msg, ok ? ControlAppearance.Success : ControlAppearance.Danger);
        await LoadServices();
    }

    async void SvcRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var changed = _services.Where(s => s.Info.Changed).ToList();
        if (!await Ui.Confirm($"¿Restaurar {changed.Count} servicios?", Ui.Lines(changed.Select(s => "•  " + s.Display)), "Restaurar todos")) return;
        var n = await Task.Run(Services.RestoreAll);
        Ui.Toast("Servicios restaurados", $"{n} servicios vuelven a su configuración original.");
        await LoadServices();
    }

    async void TaskToggle_Click(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var row = (TaskRow)toggle.Tag;
        var enable = toggle.IsChecked == true;
        toggle.IsChecked = !enable;
        var (ok, msg) = await Task.Run(() => ScheduledTasks.SetEnabled(row.Info.Path, enable));
        Ui.Toast(ok ? (enable ? "Tarea activada" : "Tarea desactivada") : "No se pudo cambiar", ok ? row.Info.Name : msg, ok ? ControlAppearance.Success : ControlAppearance.Danger);
        await LoadTasks();
    }

    async void TaskRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var n = ScheduledTasks.LoadBackup().Count;
        if (n == 0) { Ui.Toast("Nada que restaurar", "Tidy no ha cambiado ninguna tarea."); return; }
        if (!await Ui.Confirm($"¿Restaurar {n} tareas?", "Cada tarea vuelve a estar activada o desactivada como antes de que Tidy la tocara.", "Restaurar todas")) return;
        var done = await Task.Run(ScheduledTasks.RestoreAll);
        Ui.Toast("Tareas restauradas", $"{done} tareas vuelven a su estado original.");
        await LoadTasks();
    }
}
