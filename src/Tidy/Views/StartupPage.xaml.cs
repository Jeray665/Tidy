using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class StartupRow(StartupApp app) : Observable
{
    public StartupApp App => app;
    public string Name => app.Name;
    public string Command => app.Command;
    public string Detail => string.IsNullOrEmpty(app.Publisher) ? app.Location : $"{app.Publisher} · {app.Location}";
    public bool Enabled => app.Enabled;
    public ImageSource? Icon => Ui.FileIcon(app.ExePath);
}

public partial class StartupPage : Page
{
    List<StartupRow> _rows = [];

    public StartupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _rows = (await Task.Run(StartupApps.List)).Select(a => new StartupRow(a)).ToList();
            List.ItemsSource = _rows;
            UpdateSummary();
        };
    }

    void UpdateSummary()
    {
        var on = _rows.Count(r => r.Enabled);
        Summary.Text = $"{on} activadas · {_rows.Count - on} desactivadas";
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var row = (StartupRow)toggle.Tag;
        var enable = toggle.IsChecked == true;
        try
        {
            StartupApps.SetEnabled(row.App, enable);
            Ui.Toast(enable ? "Activada" : "Desactivada", $"\"{row.Name}\" {(enable ? "arrancará" : "ya no arrancará")} con Windows.");
        }
        catch (Exception ex)
        {
            toggle.IsChecked = row.Enabled;
            Ui.Toast("No se pudo cambiar", ex.Message, ControlAppearance.Danger);
        }
        UpdateSummary();
    }
}
