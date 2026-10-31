using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>The small Windows-11-style panel that pops up from the tray icon: sections on the left, quick controls on the right.</summary>
public partial class TrayFlyout : FluentWindow
{
    public static readonly (Guid Id, string Name)[] QuickPlans =
        [(PowerPlan.Balanced, "Equilibrado"), (PowerPlan.High, "Alto"), (PowerPlan.Ultimate, "Máximo")];

    static readonly string[] Titles = ["Resumen", "Rendimiento", "Programas", "Limpieza", "Estado"];
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    public DateTime HiddenAt { get; private set; }
    int _section;
    List<ScanResult>? _scans;
    DateTime _scansAt;
    readonly List<(System.Windows.Controls.CheckBox Box, ScanResult Scan)> _cleanBoxes = [];
    List<ScanResult> _confirming = [];
    List<SecurityCheck>? _security;
    DateTime _securityAt;
    HistoryEntry? _lastUndo;
    bool _showingSchedule;

    public TrayFlyout()
    {
        InitializeComponent();
        Theme.Attach(this);
        Ui.LiveBind(this, Update);
        Deactivated += (_, _) => { HiddenAt = DateTime.Now; Hide(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) OnShown(); };
        // sections differ in height: stay anchored to the bottom of the screen
        SizeChanged += (_, _) => { if (IsVisible) Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12; };
        GameMode.Changed += _ => Dispatcher.BeginInvoke(ShowGame);
        Select(0);
    }

    public void ShowNearTray()
    {
        Show();
        // bottom-right corner of the work area, like Windows' own flyouts
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
        Activate();
    }

    void OnShown()
    {
        BoostResult.Visibility = CleanResult.Visibility = UndoResult.Visibility = Visibility.Collapsed;
        ShowCleanMain();
        UndoConfirm.Visibility = Visibility.Collapsed;
        AutoRam.IsChecked = Store.Settings.AutoRam;
        AutoNote.Text = $"Libera la caché si quedan menos de {Store.Settings.FreeThresholdMb / 1024.0:0.#} GB libres";
        ShowGame();
        Select(_section);
    }

    void Section_Click(object sender, RoutedEventArgs e) => Select(int.Parse((string)((FrameworkElement)sender).Tag));

    void Select(int i)
    {
        _section = i;
        SectionTitle.Text = Titles[i];
        StackPanel[] panels = [SummaryPanel, PerfPanel, AppsPanel, CleanPanel, StatusPanel];
        for (int p = 0; p < panels.Length; p++) panels[p].Visibility = p == i ? Visibility.Visible : Visibility.Collapsed;
        foreach (Wpf.Ui.Controls.Button b in Rail.Children)
        {
            bool on = (string)b.Tag == i.ToString();
            b.Appearance = on ? ControlAppearance.Secondary : ControlAppearance.Transparent;
            b.Foreground = on ? Ui.Res("AccentTextFillColorPrimaryBrush") : Ui.Res("TextFillColorPrimaryBrush");
        }
        if (!IsVisible) return;
        if (i == 1) BuildPlans();
        if (i == 2) { ShowTop(); ShowStartup(); }
        if (i == 3) ShowCleanup();
        if (i == 4) ShowStatus();
    }

    void Update(Snapshot s)
    {
        Cpu.Text = $"{s.Cpu:0} %";
        CpuBar.Value = s.Cpu;
        Ram.Text = $"{s.RamPct} %";
        RamBar.Value = s.RamPct;
        RamBar.ToolTip = $"{s.RamUsedGb:0.0} de {s.RamTotalGb:0.0} GB";
        if (s.Gpu is { } g)
        {
            Gpu.Text = $"{g.UsagePct} %";
            GpuBar.Value = g.UsagePct;
            Temp.Text = $"{g.TempC} °C";
            TempBar.Value = g.TempC;
            TempBar.Foreground = Ui.Health(g.TempC + 5); // 70 °C+ caution, 85 °C+ red
        }
    }

    // ---------- performance ----------
    void ShowGame()
    {
        GameAuto.IsChecked = Store.Settings.GameModeAuto;
        var text = GameMode.Manual ? $"Activado a mano desde las {GameMode.Since:HH:mm}"
            : !Store.Settings.GameModeAuto ? "Desactivado"
            : GameMode.Current is { } g ? $"Jugando a {g}: modo juego activo" : "Esperando a que abras un juego";
        GameNote.Text = GameMode.Manual && Store.Settings.GameModeAuto ? "Activado" : text;
        SummaryGame.Text = $"Modo juego: {text}";
        GameNowBtn.Content = GameMode.Current != null ? "Desactivar el modo juego" : "Activar el modo juego ahora";
        GameNowBtn.Appearance = GameMode.Current != null ? ControlAppearance.Primary : ControlAppearance.Secondary;
        GameNowBtn.ToolTip = GameMode.Current != null ? "Todo vuelve a como estaba" : "Plan de máximo rendimiento y RAM liberada hasta que lo desactives";
    }

    async void GameNow_Click(object sender, RoutedEventArgs e)
    {
        GameNowBtn.IsEnabled = false;
        if (GameMode.Current != null) await Task.Run(GameMode.Stop);
        else await Task.Run(GameMode.StartManual);
        GameNowBtn.IsEnabled = true;
        ShowGame();
    }

    void BuildPlans()
    {
        var active = PowerPlan.All().FirstOrDefault(p => p.Active);
        PlanButtons.Children.Clear();
        for (int i = 0; i < QuickPlans.Length; i++)
        {
            var (id, name) = QuickPlans[i];
            bool on = active != null && (id == PowerPlan.Ultimate ? PowerPlan.IsUltimate(active) : active.Id == id);
            var b = new Wpf.Ui.Controls.Button
            {
                Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 7, 0, 7),
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == QuickPlans.Length - 1 ? 0 : 3, 0),
                Appearance = on ? ControlAppearance.Primary : ControlAppearance.Secondary,
            };
            b.Click += async (_, _) => { await Task.Run(() => PowerPlan.Activate(id)); BuildPlans(); };
            PlanButtons.Children.Add(b);
        }
    }

    // ---------- programs ----------
    async void ShowTop()
    {
        TopRefresh.IsEnabled = false;
        TopNote.Text = "Midiendo…";
        var top = await Task.Run(() => Core.Background.Top(TimeSpan.FromSeconds(1.5), 5));
        TopList.Children.Clear();
        foreach (var app in top) TopList.Children.Add(TopRow(app));
        TopNote.Text = top.Count == 0 ? "No hay programas tuyos gastando ahora mismo." : "Los procesos de Windows no salen aquí: no se deben cerrar.";
        TopRefresh.IsEnabled = true;
    }

    void TopRefresh_Click(object sender, RoutedEventArgs e) => ShowTop();

    UIElement TopRow(BackgroundApp app)
    {
        var grid = new Grid { Margin = new Thickness(2, 0, 0, 6), ToolTip = app.Exes };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new System.Windows.Controls.TextBlock { Text = app.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Procesador {app.Cpu:0} % · {Store.FormatBytes(app.Memory)}", FontSize = 11, Style = (Style)FindResource("Muted"),
        });
        grid.Children.Add(texts);
        // two clicks: the first one only asks
        var close = new Wpf.Ui.Controls.Button { Content = "Cerrar", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 4, 10, 4) };
        close.Click += async (_, _) =>
        {
            if (close.Appearance != ControlAppearance.Danger)
            {
                close.Content = "¿Cerrar?";
                close.Appearance = ControlAppearance.Danger;
                close.ToolTip = "Si tiene algo sin guardar, te preguntará antes de cerrarse";
                return;
            }
            close.IsEnabled = false;
            var (ok, msg) = await Task.Run(() => Core.Background.Close(app));
            TopNote.Text = ok ? $"Se ha pedido a {app.Name} que se cierre." : $"No se pudo cerrar {app.Name}: {msg}";
            await Task.Delay(1500);
            ShowTop();
        };
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        return grid;
    }

    async void ShowStartup()
    {
        var apps = await Task.Run(StartupApps.List);
        StartupList.Children.Clear();
        if (apps.Count == 0) StartupList.Children.Add(Ui.Text("Ninguna app arranca con Windows.", "Muted", 0));
        foreach (var app in apps.OrderByDescending(a => a.Enabled).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var grid = new Grid { Margin = new Thickness(2, 0, 0, 6), ToolTip = app.Command };
            var name = new System.Windows.Controls.TextBlock { Text = app.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 56, 0) };
            var toggle = new ToggleSwitch { IsChecked = app.Enabled, HorizontalAlignment = HorizontalAlignment.Right };
            toggle.Click += (_, _) =>
            {
                try { StartupApps.SetEnabled(app, toggle.IsChecked == true); }
                catch (Exception ex) { toggle.IsChecked = app.Enabled; Store.Log($"Windows startup (tray): {app.Name}: {ex.Message}"); }
            };
            grid.Children.Add(name);
            grid.Children.Add(toggle);
            StartupList.Children.Add(grid);
        }
    }

    // ---------- cleanup ----------
    async void ShowCleanup()
    {
        ShowDisks();
        ShowSchedule();
        CleanLast.Text = Store.Settings.LastClean is { } c ? $"Última limpieza: {c:M}" : "Todavía no has limpiado con Tidy";
        if (_scans != null && DateTime.Now - _scansAt < Fresh) { ShowCategories(); return; }
        CleanSize.Text = "Calculando…";
        RecycleSize.Text = "Calculando…";
        CleanBtn.IsEnabled = RecycleBtn.IsEnabled = false;
        CleanCats.Children.Clear();
        _scans = await Task.Run(() => Cleaner.Categories.Select(Cleaner.Scan).ToList());
        _scansAt = DateTime.Now;
        ShowCategories();
    }

    void ShowDisks()
    {
        Disks.Children.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var used = (d.TotalSize - d.AvailableFreeSpace) * 100.0 / d.TotalSize;
            var row = new StackPanel { Margin = new Thickness(2, 0, 0, 8) };
            var line = new Grid();
            line.Children.Add(new System.Windows.Controls.TextBlock { Text = $"Disco {d.Name.TrimEnd('\\')}", FontSize = 12 });
            line.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{Store.FormatBytes(d.AvailableFreeSpace)} libres de {Store.FormatBytes(d.TotalSize)}", FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("Muted"),
            });
            row.Children.Add(line);
            row.Children.Add(new ProgressBar { Height = 3, Maximum = 100, Value = used, Margin = new Thickness(0, 5, 0, 0), Foreground = Ui.Health(used) });
            Disks.Children.Add(row);
        }
    }

    void ShowCategories()
    {
        CleanCats.Children.Clear();
        _cleanBoxes.Clear();
        foreach (var scan in _scans!.Where(s => !s.Category.RecycleBin))
        {
            var box = new System.Windows.Controls.CheckBox
            {
                IsChecked = scan.Category.DefaultOn && scan.Items.Count > 0, IsEnabled = scan.Items.Count > 0,
                ToolTip = scan.Note ?? scan.Category.Description, Margin = new Thickness(0, 0, 0, 2),
                Content = new System.Windows.Controls.TextBlock { Text = $"{scan.Category.Name} · {Store.FormatBytes(scan.Bytes)}", FontSize = 12 },
            };
            box.Checked += (_, _) => UpdateCleanTotal();
            box.Unchecked += (_, _) => UpdateCleanTotal();
            _cleanBoxes.Add((box, scan));
            CleanCats.Children.Add(box);
        }
        UpdateCleanTotal();
        var recycle = _scans!.First(s => s.Category.RecycleBin);
        RecycleSize.Text = recycle.Items.Count == 0 ? "Vacía" : $"{Views.CategoryRow.Files(recycle.Items.Count)} · {Store.FormatBytes(recycle.Bytes)}";
        RecycleBtn.IsEnabled = recycle.Items.Count > 0;
    }

    void UpdateCleanTotal()
    {
        var bytes = _cleanBoxes.Where(b => b.Box.IsChecked == true).Sum(b => b.Scan.Bytes);
        CleanSize.Text = Store.FormatBytes(_cleanBoxes.Sum(b => b.Scan.Bytes));
        CleanBtn.Content = bytes > 0 ? $"Revisar y limpiar {Store.FormatBytes(bytes)}" : "Revisar y limpiar";
        CleanBtn.IsEnabled = bytes > 0;
    }

    void CleanReview_Click(object sender, RoutedEventArgs e) =>
        ShowConfirm(_cleanBoxes.Where(b => b.Box.IsChecked == true && b.Scan.Items.Count > 0).Select(b => b.Scan).ToList());

    void Recycle_Click(object sender, RoutedEventArgs e) => ShowConfirm(_scans!.Where(s => s.Category.RecycleBin && s.Items.Count > 0).ToList());

    /// <summary>The same gate as the main window: every file on screen, and nothing happens until "Borrar definitivamente".</summary>
    void ShowConfirm(List<ScanResult> results)
    {
        if (results.Count == 0) return;
        _confirming = results;
        ConfirmTitle.Text = $"¿Borrar {Views.CategoryRow.Files(results.Sum(r => r.Items.Count))} ({Store.FormatBytes(results.Sum(r => r.Bytes))})?";
        ConfirmRecycle.Visibility = results.Any(r => r.Category.RecycleBin) ? Visibility.Visible : Visibility.Collapsed;
        ConfirmList.ItemsSource = results.SelectMany(r => r.Items).OrderByDescending(i => i.Size).ToList();
        ConfirmCopy.Text = $"Se guardará una copia de esta lista en {Store.CleanupsDir}.";
        ConfirmBtn.IsEnabled = true;
        CleanMain.Visibility = Visibility.Collapsed;
        CleanConfirm.Visibility = Visibility.Visible;
    }

    void ShowCleanMain()
    {
        CleanConfirm.Visibility = Visibility.Collapsed;
        CleanMain.Visibility = Visibility.Visible;
        ConfirmList.ItemsSource = null;
    }

    void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ShowCleanMain();

    async void ConfirmClean_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBtn.IsEnabled = false;
        var report = await Views.CleanerPage.CleanConfirmed(_confirming);
        CleanResult.Text = $"Recuperados {Store.FormatBytes(report.Bytes)}: {report.Deleted:N0} archivos borrados" +
                           (report.Skipped > 0 ? $", {report.Skipped:N0} omitidos porque estaban en uso." : ".");
        CleanResult.Visibility = Visibility.Visible;
        ShowCleanMain();
        _scans = null;
        ShowCleanup();
    }

    void ShowSchedule()
    {
        _showingSchedule = true;
        var s = Store.Settings;
        ScheduleBox.SelectedIndex = s.CleanReminderDays <= 0 ? 0 : s.AutoClean ? 2 : 1;
        ScheduleNote.Text = s.CleanReminderDays <= 0 ? "No te avisa ni limpia por su cuenta." : Views.SettingsPage.NextCleanText();
        _showingSchedule = false;
    }

    void Schedule_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingSchedule) return;
        var s = Store.Settings;
        var mode = ScheduleBox.SelectedIndex;
        // cleaning by itself is agreed to once, with what it will delete in front of you: that's in Ajustes
        if (mode == 2 && !s.AutoClean) { ShowSchedule(); Open(typeof(Views.SettingsPage)); return; }
        s.AutoClean = mode == 2;
        s.CleanReminderDays = mode == 0 ? 0 : s.CleanReminderDays > 0 ? s.CleanReminderDays : 30;
        Store.Save();
        Store.Log($"Scheduled cleanup: {(mode == 0 ? "off" : mode == 1 ? "remind" : "automatic")}, every {s.CleanReminderDays} days (tray)");
        ShowSchedule();
    }

    // ---------- status ----------
    async void ShowStatus()
    {
        ShowUpdates();
        var bootTask = Task.Run(() => Benchmark.Boots(2));
        var undoTask = Task.Run(() =>
        {
            var undoable = Views.HistoryPage.Undoable();
            return History.All().FirstOrDefault(e => e.Undo != null && undoable.Contains(e.Undo));
        });
        if (_security == null || DateTime.Now - _securityAt > Fresh)
        {
            SecText.Text = "Revisando…";
            SecIcon.Foreground = Ui.Res("TextFillColorSecondaryBrush");
            _security = await Task.Run(SecurityStatus.Run);
            _securityAt = DateTime.Now;
        }
        ShowSecurity(_security);

        var boots = await bootTask;
        BootText.Text = boots.Count == 0 ? Benchmark.BootError ?? "Windows todavía no tiene registrado ningún tiempo de arranque. Aparecerán tras el próximo reinicio."
            : boots.Count == 1 ? $"{boots[0].Seconds:0.0} s"
            : boots[0].Seconds <= boots[1].Seconds ? $"{boots[0].Seconds:0.0} s · {boots[1].Seconds - boots[0].Seconds:0.0} s menos que el anterior"
            : $"{boots[0].Seconds:0.0} s · {boots[0].Seconds - boots[1].Seconds:0.0} s más que el anterior";

        _lastUndo = await undoTask;
        LastTitle.Text = _lastUndo == null ? "Nada pendiente" : $"{_lastUndo.Title} · {_lastUndo.When:g}";
        UndoBtn.Visibility = _lastUndo == null ? Visibility.Collapsed : Visibility.Visible;
        if (_lastUndo != null) UndoBtn.Content = Views.HistoryPage.UndoLabel(_lastUndo.Undo!);
    }

    void ShowSecurity(List<SecurityCheck> checks)
    {
        var bad = checks.Where(c => c.State == CheckState.Bad).ToList();
        var warn = checks.Where(c => c.State == CheckState.Warning).ToList();
        var issues = bad.Concat(warn).Select(c => c.Title).ToList();
        SecText.Text = issues.Count == 0 ? "Todo en orden" : string.Join(", ", issues.Take(3)) + (issues.Count > 3 ? "…" : "");
        SecIcon.Symbol = issues.Count == 0 ? SymbolRegular.ShieldCheckmark24 : SymbolRegular.ShieldError24;
        SecIcon.Foreground = Ui.Res(bad.Count > 0 ? "SystemFillColorCriticalBrush" : warn.Count > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
    }

    void ShowUpdates()
    {
        var s = Store.Settings;
        UpdApps.Visibility = UpdCheckBtn.Visibility = Winget.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdApps.Text = s.LastAppScan is not { } a ? "Apps: sin comprobar"
            : s.LastAppUpdates == 0 ? $"Apps: todas al día ({a:M})" : $"Apps: {s.LastAppUpdates} con versión nueva ({a:M})";
        UpdDrivers.Text = s.LastDriverScan is not { } d ? "Drivers: sin buscar"
            : s.LastDriverUpdates == 0 ? $"Drivers: al día ({d:M})" : $"Drivers: {s.LastDriverUpdates} con versión nueva ({d:M})";
        UpdTidy.Visibility = Updater.Available != null ? Visibility.Visible : Visibility.Collapsed;
        if (Updater.Available is { } r) UpdTidy.Text = $"Tidy {r.Version} disponible";
    }

    async void UpdCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdCheckBtn.IsEnabled = false;
        UpdApps.Text = "Apps: comprobando…";
        await Task.Run(Winget.Upgrades);
        UpdCheckBtn.IsEnabled = true;
        ShowUpdates();
    }

    void Updates_Click(object sender, RoutedEventArgs e) =>
        Open(Updater.Available != null ? typeof(Views.SettingsPage) : Store.Settings.LastDriverUpdates > 0 && Store.Settings.LastAppUpdates == 0 ? typeof(Views.DriversPage) : typeof(Views.AppsPage));

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUndo?.Undo is not { } undo) return;
        var kind = undo.Split(':')[0];
        // these need the main window: the recycle bin, the startup page, or the full hosts comparison
        if (kind is "recycle" or "hosts" || undo == "startup") { Open(typeof(Views.HistoryPage)); return; }
        UndoText.Text = $"{_lastUndo.Title}\n\n{Views.HistoryPage.Explain(kind, undo.Contains(':') ? undo[(undo.IndexOf(':') + 1)..] : "")}";
        UndoYes.Content = Views.HistoryPage.UndoLabel(undo);
        UndoYes.IsEnabled = true;
        UndoConfirm.Visibility = Visibility.Visible;
    }

    void UndoCancel_Click(object sender, RoutedEventArgs e) => UndoConfirm.Visibility = Visibility.Collapsed;

    async void UndoYes_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUndo?.Undo is not { } undo) return;
        UndoYes.IsEnabled = false;
        var (kind, arg) = (undo.Split(':')[0], undo.Contains(':') ? undo[(undo.IndexOf(':') + 1)..] : "");
        bool ok;
        try { ok = await Task.Run(() => Views.HistoryPage.Apply(kind, arg)); }
        catch (Exception ex) { Store.Log($"Undo (tray) failed: {ex.Message}"); ok = false; }
        UndoConfirm.Visibility = Visibility.Collapsed;
        UndoResult.Text = ok ? $"✓ Deshecho: {_lastUndo.Title}" : "No se pudo deshacer del todo. Revisa el registro de actividad en Ajustes.";
        UndoResult.Visibility = Visibility.Visible;
        ShowStatus();
    }

    void Security_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.SecurityPage));
    void History_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.HistoryPage));

    // ---------- summary ----------
    async void Boost_Click(object sender, RoutedEventArgs e)
    {
        BoostBtn.IsEnabled = false;
        var freed = await Task.Run(() => Memory.PurgeStandby("menú de la bandeja"));
        BoostBtn.IsEnabled = true;
        BoostResult.Text = freed >= 0 ? $"✓ {freed:N0} MB de memoria liberados" : "No se pudo: Tidy necesita permisos de administrador";
        BoostResult.Visibility = Visibility.Visible;
    }

    void AutoRam_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutoRam = AutoRam.IsChecked == true;
        Store.Save();
        Store.Log($"Automatic RAM release: {(Store.Settings.AutoRam ? "on" : "off")} (tray)");
    }

    void GameAuto_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.GameModeAuto = GameAuto.IsChecked == true;
        Store.Save();
        Store.Log($"Automatic game mode: {(Store.Settings.GameModeAuto ? "on" : "off")} (tray)");
        if (!Store.Settings.GameModeAuto && GameMode.Current != null && !GameMode.Manual) Task.Run(GameMode.Stop);
        ShowGame();
    }

    void Open(Type? page)
    {
        Hide();
        ((App)Application.Current).ShowMain(page);
    }

    void Open_Click(object sender, RoutedEventArgs e) => Open(null);
    void MorePerf_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.PerformancePage));
    void MoreStartup_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.StartupPage));
}
