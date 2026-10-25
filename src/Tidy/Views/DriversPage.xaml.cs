using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class UpdateRow(DriverUpdate u) : Observable
{
    static readonly Brush Nvidia = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x94, 0x00)));
    static readonly Brush Wu = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public DriverUpdate Update => u;
    public string Device => u.Device;
    public string Source => u.Source;
    public string Class => u.Class;
    public string Size => u.Size;
    public string NewDateText => u.NewDateText;
    public string CurrentVersion => u.CurrentVersion;
    public string NewVersion => u.NewVersion;
    public Brush SourceBrush => u.Source == "NVIDIA" ? Nvidia : Wu;
    public bool Selected { get => u.Selected; set => u.Selected = value; }
}

public partial class DriversPage : Page
{
    List<DriverUpdate> _updates = [];
    bool _busy;

    public DriversPage() => InitializeComponent();

    async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();

    async Task Scan()
    {
        if (_busy) return;
        _busy = true;
        ScanBtn.IsEnabled = UpdateAllBtn.IsEnabled = false;
        Ring.IsIndeterminate = true;
        StatusTitle.Text = "Escaneando…";
        var notes = new List<string>();
        try
        {
            StatusSub.Text = "Detectando tus componentes…";
            var inv = await Task.Run(Drivers.Inventory);
            DevicesTitle.Text = $"Tus dispositivos ({inv.Count})";
            Groups.ItemsSource = inv.GroupBy(d => d.Class).ToList();
            DevicesPanel.Visibility = Visibility.Visible;

            var updates = new List<DriverUpdate>();
            StatusSub.Text = "Consultando NVIDIA…";
            try { if (await Drivers.CheckNvidia() is { } nv) updates.Add(nv); }
            catch (Exception ex) { notes.Add("No se pudo consultar NVIDIA (¿sin conexión?)"); Store.Log($"Drivers: NVIDIA check failed: {ex.Message}"); }

            StatusSub.Text = "Consultando Windows Update… puede tardar un minuto.";
            try { updates.AddRange(await Task.Run(() => Drivers.SearchWindowsUpdate(inv))); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                notes.Add("Windows Update no respondió");
                Store.Log($"Drivers: Windows Update failed: {ex.Message}");
            }

            _updates = updates;
            Store.Settings.LastDriverScan = DateTime.Now;
            Store.Settings.LastDriverUpdates = updates.Count;
            Store.Save();
            Updates.ItemsSource = updates.Select(u => new UpdateRow(u)).ToList();
            UpdatesPanel.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAllBtn.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            Ring.IsIndeterminate = false;
            Ring.Progress = 100;
            if (updates.Count == 0)
            {
                Ring.Foreground = Ui.Res("SystemFillColorSuccessBrush");
                StatusIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                StatusIcon.Foreground = Ui.Res("SystemFillColorSuccessBrush");
                StatusTitle.Text = "Todos tus drivers están al día";
            }
            else
            {
                Ring.Foreground = Ui.Res("SystemFillColorCautionBrush");
                StatusIcon.Symbol = SymbolRegular.Warning24;
                StatusIcon.Foreground = Ui.Res("SystemFillColorCautionBrush");
                StatusTitle.Text = updates.Count == 1 ? "1 driver desactualizado" : $"{updates.Count} drivers desactualizados";
            }
            StatusSub.Text = $"{inv.Count} dispositivos revisados · {DateTime.Now:HH:mm}" + (notes.Count > 0 ? $" · {string.Join(" · ", notes)}" : "");
            ScanBtn.Content = "Volver a escanear";
            ScanBtn.Appearance = ControlAppearance.Secondary;
        }
        finally
        {
            Ring.IsIndeterminate = false;
            ScanBtn.IsEnabled = UpdateAllBtn.IsEnabled = true;
            _busy = false;
        }
    }

    async void UpdateSelected_Click(object sender, RoutedEventArgs e) => await Install(_updates.Where(u => u.Selected).ToList());

    async void UpdateOne_Click(object sender, RoutedEventArgs e) => await Install([(DriverUpdate)((FrameworkElement)sender).Tag]);

    async Task Install(List<DriverUpdate> selected)
    {
        if (_busy || selected.Count == 0) return;
        var panel = new StackPanel { Width = 600 };
        panel.Children.Add(Ui.Text("Se instalarán:", null, 6));
        foreach (var u in selected)
            panel.Children.Add(Ui.Text($"•  {u.Device}: {u.CurrentVersion} → {u.NewVersion}  ({u.Source})", null, 2));
        panel.Children.Add(Ui.Text("\nAntes se crea un punto de restauración. Los drivers de Windows Update se instalan solos. " +
                                   "El de NVIDIA se descarga de nvidia.com, se comprueba su firma digital y se abre su instalador oficial para que lo termines tú.", "Muted", 0));
        if (!await Ui.Confirm(selected.Count == 1 ? "¿Actualizar este driver?" : $"¿Actualizar {selected.Count} drivers?", panel, "Actualizar")) return;
        if (!await Ui.EnsureRestorePoint("Actualizar drivers")) return;

        _busy = true;
        ScanBtn.IsEnabled = UpdateAllBtn.IsEnabled = false;
        InstallBar.Visibility = Visibility.Visible;
        try
        {
            var wu = selected.Where(u => u.Source == "Windows Update").ToList();
            if (wu.Count > 0)
            {
                InstallBar.IsIndeterminate = true;
                var reboot = await Task.Run(() => Drivers.InstallWindowsUpdate(wu, msg => Dispatcher.BeginInvoke(() => StatusSub.Text = msg)));
                RebootBar.IsOpen = reboot;
                Ui.Toast("Drivers instalados", $"{wu.Count} driver(s) de Windows Update instalados.");
            }
            foreach (var nv in selected.Where(u => u.Source == "NVIDIA"))
            {
                InstallBar.IsIndeterminate = false;
                StatusSub.Text = $"Descargando NVIDIA {nv.NewVersion}…";
                var progress = new Progress<double>(p => { InstallBar.Value = p; StatusSub.Text = $"Descargando NVIDIA {nv.NewVersion}… {p:0} %"; });
                var path = await Drivers.DownloadNvidia(nv, progress, CancellationToken.None);
                StatusSub.Text = "Firma de NVIDIA verificada. Abriendo el instalador oficial…";
                Drivers.RunVerifiedInstaller(path, "NVIDIA Corporation");
                Ui.Toast("Instalador de NVIDIA abierto", "Firma verificada. Sigue sus pasos (recomendado: Instalación rápida).");
            }
        }
        catch (Exception ex)
        {
            Store.Log($"Drivers: install error: {ex}");
            Ui.Toast("No se pudo completar", ex.Message, ControlAppearance.Danger);
        }
        finally
        {
            InstallBar.Visibility = Visibility.Collapsed;
            _busy = false;
            ScanBtn.IsEnabled = UpdateAllBtn.IsEnabled = true;
        }
        if (selected.Any(u => u.Source == "Windows Update")) await Scan();
    }
}
