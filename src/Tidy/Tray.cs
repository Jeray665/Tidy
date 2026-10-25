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
        _icon = new Forms.NotifyIcon
        {
            Icon = Theme.TrayIcon(),
            Text = "Tidy",
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleFlyout(); };
        Updater.Found += r => _app.Dispatcher.BeginInvoke(() =>
        {
            if (Store.Settings.LastNotifiedVersion == r.Version.ToString()) return;
            Store.Settings.LastNotifiedVersion = r.Version.ToString();
            Store.Save();
            Balloon(typeof(Views.SettingsPage), $"Hay una versión nueva de Tidy: {r.Version}. Haz clic para ver qué trae y actualizar.");
        });
        Theme.Changed += () => { var old = _icon.Icon; _icon.Icon = Theme.TrayIcon(); old?.Dispose(); };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(I18n.T("Abrir Tidy"), null, (_, _) => _app.ShowMain());
        menu.Items.Add(I18n.T("Liberar memoria ahora"), null, async (_, _) =>
        {
            var freed = await Task.Run(() => Memory.PurgeStandby("menú contextual de la bandeja"));
            Balloon(null, freed >= 0 ? $"{freed:N0} MB de memoria liberados" : "No se pudo liberar memoria");
        });
        var plans = new Forms.ToolStripMenuItem(I18n.T("Plan de energía"));
        foreach (var (id, name) in TrayFlyout.QuickPlans)
            plans.DropDownItems.Add(new Forms.ToolStripMenuItem(I18n.T(name), null, async (_, _) => await Task.Run(() => PowerPlan.Activate(id))) { Tag = id });
        plans.DropDownOpening += (_, _) =>
        {
            var active = PowerPlan.All().FirstOrDefault(p => p.Active);
            foreach (Forms.ToolStripMenuItem i in plans.DropDownItems)
                i.Checked = active != null && ((Guid)i.Tag! == PowerPlan.Ultimate ? PowerPlan.IsUltimate(active) : active.Id == (Guid)i.Tag!);
        };
        menu.Items.Add(plans);
        var game = new Forms.ToolStripMenuItem(I18n.T("Modo juego automático")) { CheckOnClick = true };
        game.Click += (_, _) =>
        {
            Store.Settings.GameModeAuto = game.Checked;
            Store.Save();
            Store.Log($"Automatic game mode: {(game.Checked ? "on" : "off")} (tray menu)");
            if (!game.Checked && GameMode.Current != null) Task.Run(GameMode.Stop);
        };
        menu.Items.Add(game);
        menu.Items.Add(I18n.T("Limpieza…"), null, (_, _) => _app.ShowMain(typeof(Views.CleanerPage)));
        menu.Opening += (_, _) => game.Checked = Store.Settings.GameModeAuto;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(I18n.T("Salir"), null, (_, _) => _app.Quit());
        _icon.ContextMenuStrip = menu;

        _icon.BalloonTipClicked += (_, _) => { if (_balloonPage != null) _app.ShowMain(_balloonPage); };
        GameMode.Changed += game => _app.Dispatcher.BeginInvoke(() => Balloon(null,
            game != null ? $"Modo juego activado: {game}" : "Modo juego terminado: todo vuelve a como estaba."));
        CleanReminder.Due += bytes => _app.Dispatcher.BeginInvoke(() => Balloon(typeof(Views.CleanerPage),
            $"Puedes liberar {Store.FormatBytes(bytes)} de archivos temporales. Haz clic para ver la lista (no se borra nada sin preguntarte)."));
        CleanReminder.Cleaned += r => _app.Dispatcher.BeginInvoke(() => Balloon(typeof(Views.BackupsPage),
            $"Limpieza automática: {Store.FormatBytes(r.Bytes)} liberados. Haz clic para ver la lista exacta de lo borrado."));

        Live.Updated += s => _app.Dispatcher.BeginInvoke(() =>
        {
            var text = $"Tidy · CPU {s.Cpu:0}% · RAM {s.RamPct}%" + (s.Gpu is { } g ? $" · GPU {g.TempC}°C" : "");
            _icon.Text = text.Length > 127 ? text[..127] : text;
        });
    }

    Type? _balloonPage;
    string? _lastBalloon;
    DateTime _lastBalloonAt;
    int _balloonCount;

    /// <summary>Windows notification. The same text again within 10 minutes says "(×N)" instead of reading as new.</summary>
    public void Balloon(Type? page, string text)
    {
        _balloonPage = page;
        text = I18n.T(text);
        _balloonCount = text == _lastBalloon && DateTime.Now - _lastBalloonAt < TimeSpan.FromMinutes(10) ? _balloonCount + 1 : 1;
        (_lastBalloon, _lastBalloonAt) = (text, DateTime.Now);
        _icon.ShowBalloonTip(5000, "Tidy", _balloonCount > 1 ? $"{text} (×{_balloonCount})" : text, Forms.ToolTipIcon.None);
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
