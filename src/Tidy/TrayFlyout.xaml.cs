using System.Windows;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>The small Windows-11-style panel that pops up from the tray icon: sections on the left, quick controls on the right.</summary>
public partial class TrayFlyout : FluentWindow
{
    public static readonly (Guid Id, string Name)[] QuickPlans =
        [(PowerPlan.Balanced, "Equilibrado"), (PowerPlan.High, "Alto"), (PowerPlan.Ultimate, "Máximo")];

    static readonly string[] Titles = ["Resumen", "Rendimiento", "Limpieza"];

    public DateTime HiddenAt { get; private set; }
    int _section;
    long? _cleanBytes;
    DateTime _cleanAt;

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
        BoostResult.Visibility = Visibility.Collapsed;
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
        SummaryPanel.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        PerfPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        CleanPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        foreach (Wpf.Ui.Controls.Button b in Rail.Children)
        {
            bool on = (string)b.Tag == i.ToString();
            b.Appearance = on ? ControlAppearance.Secondary : ControlAppearance.Transparent;
            b.Foreground = on ? Ui.Res("AccentTextFillColorPrimaryBrush") : Ui.Res("TextFillColorPrimaryBrush");
        }
        if (!IsVisible) return;
        if (i == 1) BuildPlans();
        if (i == 2) ShowCleanup();
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

    void ShowGame()
    {
        GameAuto.IsChecked = Store.Settings.GameModeAuto;
        var text = !Store.Settings.GameModeAuto ? "Desactivado"
            : GameMode.Current is { } g ? $"Jugando a {g}: modo juego activo" : "Esperando a que abras un juego";
        GameNote.Text = text;
        SummaryGame.Text = $"Modo juego: {text}";
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

    /// <summary>Only measures (same scan as the cleanup reminder); deleting happens in the main window after the list is shown.</summary>
    async void ShowCleanup()
    {
        CleanLast.Text = Store.Settings.LastClean is { } c ? $"Última limpieza: {c:M}" : "Todavía no has limpiado con Tidy";
        if (_cleanBytes != null && DateTime.Now - _cleanAt < TimeSpan.FromMinutes(5)) { CleanSize.Text = Store.FormatBytes(_cleanBytes.Value); return; }
        CleanSize.Text = "Calculando…";
        _cleanBytes = await Task.Run(() => Cleaner.Categories.Where(x => x.DefaultOn).Sum(x => Cleaner.Scan(x).Bytes));
        _cleanAt = DateTime.Now;
        CleanSize.Text = Store.FormatBytes(_cleanBytes.Value);
    }

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
        if (!Store.Settings.GameModeAuto && GameMode.Current != null) Task.Run(GameMode.Stop);
        ShowGame();
    }

    void Open(Type? page)
    {
        Hide();
        ((App)Application.Current).ShowMain(page);
    }

    void Open_Click(object sender, RoutedEventArgs e) => Open(null);
    void MorePerf_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.PerformancePage));
    void Clean_Click(object sender, RoutedEventArgs e) => Open(typeof(Views.CleanerPage));
}
