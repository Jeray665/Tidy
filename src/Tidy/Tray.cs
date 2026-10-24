using System.Windows;
using Tidy.Core;
using Forms = System.Windows.Forms;

namespace Tidy;

/// <summary>Notification-area icon: left click = flyout, right click = menu, tooltip = live summary.</summary>
public sealed class Tray : IDisposable
{
    readonly App _app;
    readonly Forms.NotifyIcon _icon;
    TrayFlyout? _flyout;

    public Tray(App app)
    {
        _app = app;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tidy.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Tidy",
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleFlyout(); };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Tidy", null, (_, _) => _app.ShowMain());
        menu.Items.Add("Liberar memoria ahora", null, async (_, _) =>
        {
            var freed = await Task.Run(() => Memory.PurgeStandby("menú contextual de la bandeja"));
            _icon.ShowBalloonTip(3000, "Tidy", freed >= 0 ? $"{freed:N0} MB de memoria liberados" : "No se pudo liberar memoria", Forms.ToolTipIcon.None);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => _app.Quit());
        _icon.ContextMenuStrip = menu;

        _icon.BalloonTipClicked += (_, _) => { if (_balloonPage != null) _app.ShowMain(_balloonPage); };
        GameMode.Changed += game => _app.Dispatcher.BeginInvoke(() => Balloon(null,
            game != null ? $"Modo juego activado: {game}" : "Modo juego terminado: todo vuelve a como estaba."));
        CleanReminder.Due += bytes => _app.Dispatcher.BeginInvoke(() => Balloon(typeof(Views.CleanerPage),
            $"Puedes liberar {Store.FormatBytes(bytes)} de archivos temporales. Haz clic para ver la lista (no se borra nada sin preguntarte)."));

        Live.Updated += s => _app.Dispatcher.BeginInvoke(() =>
        {
            var text = $"Tidy · CPU {s.Cpu:0}% · RAM {s.RamPct}%" + (s.Gpu is { } g ? $" · GPU {g.TempC}°C" : "");
            _icon.Text = text.Length > 127 ? text[..127] : text;
        });
    }

    Type? _balloonPage;

    void Balloon(Type? page, string text)
    {
        _balloonPage = page;
        _icon.ShowBalloonTip(5000, "Tidy", text, Forms.ToolTipIcon.None);
    }

    void ToggleFlyout()
    {
        _flyout ??= new TrayFlyout();
        if (_flyout.IsVisible) { _flyout.Hide(); return; }
        // the click on the icon itself deactivates (hides) the flyout first; don't instantly reopen it
        if (DateTime.Now - _flyout.HiddenAt < TimeSpan.FromMilliseconds(300)) return;
        _flyout.ShowNearTray();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _flyout?.Close();
    }
}
