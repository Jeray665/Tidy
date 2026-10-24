using System.Windows;
using Tidy.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>The small Windows-11-style panel that pops up from the tray icon.</summary>
public partial class TrayFlyout : FluentWindow
{
    static readonly (Guid Id, string Name)[] QuickPlans =
        [(PowerPlan.Balanced, "Equilibrado"), (PowerPlan.High, "Alto"), (PowerPlan.Ultimate, "Máximo")];

    public DateTime HiddenAt { get; private set; }

    public TrayFlyout()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Ui.LiveBind(this, Update);
        Deactivated += (_, _) => { HiddenAt = DateTime.Now; Hide(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) OnShown(); };
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
        BuildPlans();
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
        Store.Log($"Liberación automática de RAM: {(Store.Settings.AutoRam ? "activada" : "desactivada")} (bandeja)");
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ((App)Application.Current).ShowMain();
    }
}
