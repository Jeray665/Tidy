using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public partial class PerformancePage : Page
{
    record PlanInfo(Guid Id, string Name, string Icon, string Description);

    static readonly PlanInfo[] Standard =
    [
        new(PowerPlan.Saver, "Economizador", "BatterySaver24", "Menos consumo y calor. Para cuando no haces nada pesado."),
        new(PowerPlan.Balanced, "Equilibrado", "DataUsage24", "Lo que recomienda Windows: sube la potencia solo cuando hace falta."),
        new(PowerPlan.High, "Alto rendimiento", "Flash24", "La CPU baja menos de frecuencia. Algo más de consumo en reposo."),
        new(PowerPlan.Ultimate, "Máximo rendimiento", "TopSpeed24", "Sin ningún ahorro de energía. Para jugar o renderizar."),
    ];

    bool _loading;

    public PerformancePage()
    {
        InitializeComponent();
        Ui.LiveBind(this, Update);
        _loading = true;
        AutoRam.IsChecked = Store.Settings.AutoRam;
        Threshold.Value = Store.Settings.FreeThresholdMb;
        ThresholdText.Text = $"{Store.Settings.FreeThresholdMb / 1024.0:0.#} GB";
        _loading = false;
        Loaded += async (_, _) => { ShowPlans(); ShowTimeouts(); ShowLists(); await ShowGameMode(); };
        GameMode.Changed += OnGameChanged;
        Unloaded += (_, _) => GameMode.Changed -= OnGameChanged;
    }

    // ---------- game mode ----------
    void OnGameChanged(string? game) => Dispatcher.BeginInvoke(() => { ShowGameStatus(); ShowPlans(); });

    async Task ShowGameMode()
    {
        var s = Store.Settings;
        _loading = true;
        GameAuto.IsChecked = s.GameModeAuto;
        GamePlan.IsChecked = s.GameModePlan;
        GameRam.IsChecked = s.GameModeRam;
        GamePriority.IsChecked = s.GameModePriority;
        GamePriority.Content = new TextBlock { Text = $"Dar prioridad al juego y bajar la de apps de fondo ({GameMode.BackgroundList}). No se cierra nada.", TextWrapping = TextWrapping.Wrap };
        _loading = false;
        ShowGameStatus();
        ShowGameExes();
        var libs = await Task.Run(GameMode.Libraries);
        GameLibraries.Text = libs.Count == 0
            ? "No se ha encontrado ninguna biblioteca de juegos. Añade tus juegos a mano."
            : string.Join("   ·   ", libs.GroupBy(l => l.Launcher).Select(g => g.Count() == 1 ? $"{g.Key}: {g.First().Root.TrimEnd('\\')}" : $"{g.Key}: {g.Count()} carpetas"));
    }

    void ShowGameStatus()
    {
        var on = Store.Settings.GameModeAuto;
        GameStatus.Text = !on ? "Desactivado." : GameMode.Current is { } g ? $"Jugando a {g} desde las {GameMode.Since:HH:mm}: modo juego activo." : "Activado. Esperando a que abras un juego.";
        GameStatus.Foreground = Ui.Res(GameMode.Current != null ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        GameBadge.Background = Ui.Res(GameMode.Current != null ? "SystemFillColorSuccessBackgroundBrush" : "ControlFillColorSecondaryBrush");
    }

    void ShowGameExes()
    {
        GameExes.Children.Clear();
        foreach (var exe in Store.Settings.GameExes)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = exe, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) });
            var del = new Wpf.Ui.Controls.Button { Content = "Quitar", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 10, 4) };
            del.Click += (_, _) => { Store.Settings.GameExes.Remove(exe); Store.Save(); ShowGameExes(); };
            row.Children.Add(del);
            GameExes.Children.Add(row);
        }
    }

    void GameAuto_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.GameModeAuto = GameAuto.IsChecked == true;
        Store.Save();
        Store.Log($"Automatic game mode: {(Store.Settings.GameModeAuto ? "on" : "off")}");
        if (!Store.Settings.GameModeAuto && GameMode.Current != null) Task.Run(GameMode.Stop);
        ShowGameStatus();
    }

    void GameOption_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Store.Settings;
        s.GameModePlan = GamePlan.IsChecked == true;
        s.GameModeRam = GameRam.IsChecked == true;
        s.GameModePriority = GamePriority.IsChecked == true;
        Store.Save();
    }

    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Juegos (*.exe)|*.exe", Title = "Elige el .exe del juego" };
        if (dlg.ShowDialog() != true) return;
        if (!Store.Settings.GameExes.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase)) Store.Settings.GameExes.Add(dlg.FileName);
        Store.Save();
        ShowGameExes();
    }

    int _tick;

    void Update(Snapshot s)
    {
        CpuChart.Push(s.Cpu, $"{s.Cpu:0} %", $"{Environment.ProcessorCount} hilos");
        RamChart.Push(s.RamPct, $"{s.RamPct} %", $"{s.RamUsedGb:0.0} de {s.RamTotalGb:0.0} GB en uso");
        if (s.Gpu is { } g)
            GpuChart.Push(g.UsagePct, $"{g.UsagePct} %",
                $"{g.TempC} °C · VRAM {g.VramUsedGb:0.0}/{g.VramTotalGb:0} GB" + (g.PowerW is { } w ? $" · {w:0} W" : ""));
        else GpuChart.Push(0, "—", "Sin datos de la gráfica (solo NVIDIA)");
        if (++_tick % 3 == 0) ShowLists();
    }

    void ShowLists()
    {
        var (standby, free) = Memory.Lists();
        ListsText.Text = $"En espera: {standby / 1024.0:0.0} GB   ·   Libre de verdad: {free / 1024.0:0.0} GB";
    }

    async void Purge_Click(object sender, RoutedEventArgs e)
    {
        PurgeBtn.IsEnabled = false;
        var freed = await Task.Run(() => Memory.PurgeStandby("botón Liberar ahora"));
        PurgeBtn.IsEnabled = true;
        ShowLists();
        if (freed >= 0) Ui.Toast("Memoria liberada", $"{freed:N0} MB de caché devueltos como memoria libre.");
        else Ui.Toast("No se pudo liberar", "Windows lo ha rechazado. Tidy necesita ejecutarse como administrador.", ControlAppearance.Danger);
    }

    void AutoRam_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutoRam = AutoRam.IsChecked == true;
        Store.Save();
        Store.Log($"Automatic RAM release: {(Store.Settings.AutoRam ? "on" : "off")}");
    }

    void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || ThresholdText == null) return;
        Store.Settings.FreeThresholdMb = (int)e.NewValue;
        ThresholdText.Text = $"{e.NewValue / 1024:0.#} GB";
        Store.Save();
    }

    void ShowPlans()
    {
        var all = PowerPlan.All();
        var active = all.FirstOrDefault(p => p.Active);
        Plans.Children.Clear();
        for (int i = 0; i < Standard.Length; i++)
        {
            var info = Standard[i];
            bool isActive = active != null && (info.Id == PowerPlan.Ultimate ? PowerPlan.IsUltimate(active) : active.Id == info.Id);
            var content = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new SymbolIcon(Enum.Parse<SymbolRegular>(info.Icon)) { FontSize = 22, Foreground = Ui.Res("AccentTextFillColorPrimaryBrush") });
            if (isActive) head.Children.Add(new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 18, Margin = new Thickness(8, 0, 0, 0), Foreground = Ui.Res("SystemFillColorSuccessBrush") });
            content.Children.Add(head);
            content.Children.Add(new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            content.Children.Add(Ui.Text(info.Description, "Muted", 0));
            var card = new CardAction
            {
                Content = content, IsChevronVisible = false, Padding = new Thickness(16), VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == Standard.Length - 1 ? 0 : 6, 0),
                BorderThickness = new Thickness(isActive ? 2 : 1),
            };
            if (isActive) card.BorderBrush = Ui.Res("AccentFillColorDefaultBrush");
            card.Click += async (_, _) =>
            {
                var ok = await Task.Run(() => PowerPlan.Activate(info.Id));
                if (ok) Ui.Toast("Plan de energía cambiado", $"Ahora usas \"{info.Name}\".");
                else Ui.Toast("No se pudo cambiar el plan", "Revisa el registro en Ajustes.", ControlAppearance.Danger);
                ShowPlans();
                ShowTimeouts();
            };
            Plans.Children.Add(card);
        }
        var custom = active != null && !Standard.Any(s => s.Id == active.Id) && !PowerPlan.IsUltimate(active);
        OtherPlansNote.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        OtherPlansNote.Text = custom ? $"Ahora mismo usas un plan personalizado: \"{active!.Name}\". Elegir uno de arriba no lo borra." : "";
    }

    // ---------- screen off / sleep / hibernate ----------
    static readonly uint[] Choices = [0, 60, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000];

    void ShowTimeouts()
    {
        Timeouts.Children.Clear();
        foreach (var t in PowerTimeouts.All)
        {
            if (t.Id == "hibernate" && !PowerTimeouts.HibernateAvailable) continue;
            var (ac, dc) = PowerTimeouts.Read(t);
            var box = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in Choices.Append(ac).Distinct().Order())
                box.Items.Add(new ComboBoxItem { Content = c == 0 ? "Nunca" : PowerTimeouts.Describe(c), Tag = c, IsSelected = c == ac });
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = t.Label, FontWeight = FontWeights.SemiBold });
            header.Children.Add(Ui.Text(ac != dc && PowerTimeouts.HasBattery ? $"{t.Description} Con batería ahora: {PowerTimeouts.Describe(dc)}; al cambiarlo se usa lo mismo en los dos." : t.Description, "Muted", 0));
            box.SelectionChanged += async (_, _) =>
            {
                if (box.SelectedItem is not ComboBoxItem { Tag: uint secs }) return;
                var ok = await Task.Run(() => PowerTimeouts.Write(t, secs, secs));
                if (ok)
                {
                    Store.Settings.PowerTimeouts[t.Id] = (int)secs;
                    Store.Save();
                    Ui.Toast(t.Label, secs == 0 ? "Nunca, en el plan actual y en los que active Tidy." : $"A los {PowerTimeouts.Describe(secs)} sin usar el PC.");
                }
                else Ui.Toast("No se pudo cambiar", "Windows no ha aceptado el valor. Revisa el registro de actividad.", ControlAppearance.Danger);
                ShowTimeouts();
            };
            Timeouts.Children.Add(new CardControl
            {
                Header = header, Content = box, Margin = new Thickness(0, 0, 0, 6),
                Icon = new SymbolIcon(t.Id switch { "display" => SymbolRegular.Desktop24, "sleep" => SymbolRegular.WeatherMoon24, _ => SymbolRegular.Storage24 }),
            });
        }
    }
}
