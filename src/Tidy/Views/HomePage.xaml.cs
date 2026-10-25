using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tidy.Core;

namespace Tidy.Views;

public partial class HomePage : Page
{
    public record Suggestion(string Icon, string Title, string Detail, string Action, string Page);

    static string? _machine; // WMI is slow-ish; the hardware doesn't change while we run

    public HomePage()
    {
        InitializeComponent();
        var h = DateTime.Now.Hour;
        var hello = h < 6 ? "Buenas noches" : h < 13 ? "Buenos días" : h < 21 ? "Buenas tardes" : "Buenas noches";
        var user = Environment.UserName;
        Greeting.Text = $"{hello}, {char.ToUpper(user[0])}{user[1..]}";
        Ui.LiveBind(this, Update);
        Loaded += async (_, _) => await Analyze();
    }

    void Update(Snapshot s)
    {
        CpuText.Text = $"{s.Cpu:0} %";
        CpuBar.Value = s.Cpu;
        RamText.Text = $"{s.RamPct} %";
        RamBar.Value = s.RamPct;
        RamBar.ToolTip = $"{s.RamUsedGb:0.0} de {s.RamTotalGb:0.0} GB en uso";
        if (s.Gpu is { } g)
        {
            GpuText.Text = $"{g.TempC} °C · {g.UsagePct} %";
            GpuBar.Value = g.UsagePct;
        }
    }

    async Task Analyze()
    {
        var a = await Task.Run(() =>
        {
            var junk = Cleaner.Categories.Where(c => c.DefaultOn).Select(Cleaner.Scan).Sum(r => r.Bytes);
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return new
            {
                Ram = SysMon.Ram(), Junk = junk, Drive = drive,
                StartupOn = StartupApps.List().Count(x => x.Enabled),
                PendingTweaks = Tweaks.All.Where(t => t.Recommended && Tweaks.Available(t) && !Tweaks.IsApplied(t)).ToList(),
                AppliedTweaks = Tweaks.All.Count(Tweaks.IsApplied),
                Autostart = Autostart.IsEnabled,
                Plan = PowerPlan.All().FirstOrDefault(p => p.Active)?.Name,
                Machine = _machine ??= MachineLine(),
            };
        });

        Machine.Text = a.Machine;
        AutostartCard.Visibility = !a.Autostart && !Store.Settings.AutostartDismissed && !Install.IsPortable ? Visibility.Visible : Visibility.Collapsed;
        var usedPct = 100.0 - a.Drive.AvailableFreeSpace * 100.0 / a.Drive.TotalSize;
        DiskLabel.Text = $"  Disco {a.Drive.Name.TrimEnd('\\')} · {a.Drive.AvailableFreeSpace / 1e9:0} GB libres";
        DiskText.Text = $"{usedPct:0} %";
        DiskBar.Value = usedPct;
        DiskBar.Foreground = Ui.Health(usedPct);
        PlanSub.Text = a.Plan != null ? $"Plan: {a.Plan}" : "Plan de energía y RAM";
        TweaksSub.Text = $"{a.AppliedTweaks} de {Tweaks.All.Count} aplicadas";
        StartupSub.Text = $"{a.StartupOn} apps al arrancar";

        int score = 100;
        var list = new List<Suggestion>();
        if (a.Junk > 200L << 20)
        {
            score -= a.Junk > 2L << 30 ? 12 : 6;
            list.Add(new("Broom24", $"{Store.FormatBytes(a.Junk)} de archivos innecesarios", "Temporales, informes de errores y caché de Windows Update. Verás la lista antes de borrar.", "Limpiar", "CleanerPage"));
        }
        if (a.StartupOn > 8)
        {
            score -= a.StartupOn > 14 ? 12 : 6;
            list.Add(new("Rocket24", $"{a.StartupOn} apps arrancan con Windows", "Desactiva las que no necesitas nada más encender y el PC arrancará antes.", "Revisar", "StartupPage"));
        }
        if (a.Ram.Pct > 80)
        {
            score -= 10;
            list.Add(new("Board24", $"Memoria al {a.Ram.Pct} %", "Libera la memoria en espera o cierra lo que no estés usando.", "Liberar", "ram"));
        }
        if (usedPct > 85)
        {
            score -= usedPct > 92 ? 15 : 8;
            list.Add(new("Storage24", $"Queda poco espacio en {a.Drive.Name.TrimEnd('\\')}", $"{Store.FormatBytes(a.Drive.AvailableFreeSpace)} libres. Windows necesita espacio para ir fluido.", "Liberar espacio", "CleanerPage"));
        }
        if (a.PendingTweaks.Count > 0)
        {
            score -= Math.Min(10, a.PendingTweaks.Count * 2);
            list.Add(new("Wand24", $"{a.PendingTweaks.Count} optimizaciones recomendadas sin aplicar",
                string.Join(" · ", a.PendingTweaks.Take(3).Select(t => t.Title)), "Ver", "TweaksPage"));
        }
        var st = Store.Settings;
        if (st.LastDriverScan == null || DateTime.Now - st.LastDriverScan > TimeSpan.FromDays(7))
        {
            score -= 5;
            list.Add(new("ArrowSync24", "Comprueba tus drivers", "Busca versiones nuevas en Windows Update y NVIDIA.", "Escanear", "DriversPage"));
        }
        else if (st.LastDriverUpdates > 0)
        {
            score -= Math.Min(15, st.LastDriverUpdates * 5);
            list.Add(new("ArrowSync24", $"{st.LastDriverUpdates} drivers desactualizados", "Hay versiones oficiales más nuevas para tu hardware.", "Actualizar", "DriversPage"));
        }
        DriversSub.Text = st.LastDriverScan == null ? "Busca actualizaciones oficiales"
            : st.LastDriverUpdates > 0 ? $"{st.LastDriverUpdates} actualizaciones disponibles" : "Todo al día";

        score = Math.Clamp(score, 0, 100);
        ScoreRing.IsIndeterminate = false;
        ScoreRing.Progress = score;
        ScoreRing.Foreground = Ui.Res(score >= 85 ? "SystemFillColorSuccessBrush" : score >= 65 ? "AccentFillColorDefaultBrush" : "SystemFillColorCautionBrush");
        ScoreText.Text = score.ToString();
        ScoreOf.Visibility = Visibility.Visible;
        ScoreTitle.Text = score >= 85 ? "Tu PC está en plena forma" : score >= 65 ? "Tu PC está bien, pero puede ir mejor" : "Tu PC necesita un repaso";
        ScoreSub.Text = list.Count == 0 ? "No hemos encontrado nada que mejorar." : $"Hemos encontrado {list.Count} {(list.Count == 1 ? "cosa" : "cosas")} que puedes mejorar. Las tienes abajo.";
        Suggestions.ItemsSource = list;
        AllGood.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string MachineLine()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpu = (k?.GetValue("ProcessorNameString") as string)?.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Trim();
        using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        var gpu = s.Get().Cast<ManagementObject>().Select(o => o["Name"] as string).FirstOrDefault(n => n != null && !n.Contains("Virtual"));
        return $"{cpu} · {gpu?.Replace("NVIDIA ", "")} · {Math.Round(SysMon.Ram().TotalGb)} GB RAM";
    }

    async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        OptimizeBtn.IsEnabled = false;
        try
        {
            var freed = await Task.Run(() => Memory.PurgeStandby("botón Optimizar ahora"));
            var results = await Task.Run(() => Cleaner.Categories.Where(c => c.DefaultOn).Select(Cleaner.Scan).ToList());
            var report = await CleanerPage.ConfirmAndClean(results);
            var ram = freed >= 0 ? $"{freed:N0} MB de RAM liberados" : "No se pudo liberar RAM (faltan permisos)";
            Ui.Toast("Optimización completada", report != null ? $"{ram} · {Store.FormatBytes(report.Bytes)} de disco recuperados" : ram);
        }
        finally
        {
            OptimizeBtn.IsEnabled = true;
            await Analyze();
        }
    }

    async void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        if (tag == "ram")
        {
            var freed = await Task.Run(() => Memory.PurgeStandby("recomendación de Inicio"));
            Ui.Toast("Memoria liberada", $"{freed:N0} MB devueltos a Windows.");
            await Analyze();
            return;
        }
        MainWindow.Current?.Navigate(Type.GetType($"Tidy.Views.{tag}")!);
    }

    void Tile_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Current?.Navigate(Type.GetType($"Tidy.Views.{((FrameworkElement)sender).Tag}")!);

    async void EnableAutostart_Click(object sender, RoutedEventArgs e)
    {
        var ok = await Task.Run(Autostart.Enable);
        if (ok) Ui.Toast("Listo", "Tidy arrancará con Windows en la bandeja del sistema.");
        else Ui.Toast("No se pudo activar", "Revisa el registro en Ajustes.", Wpf.Ui.Controls.ControlAppearance.Danger);
        AutostartCard.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    void DismissAutostart_Click(object sender, RoutedEventArgs e)
    {
        Store.Settings.AutostartDismissed = true;
        Store.Save();
        AutostartCard.Visibility = Visibility.Collapsed;
    }
}
