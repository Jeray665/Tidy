using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public class AppRow(Tweaks.BloatApp app, string fullName)
{
    public Tweaks.BloatApp App => app;
    public string FullName => fullName;
    public string Name => app.Name;
    public string Description => app.Description;
    public bool Selected { get; set; }
}

public partial class TweaksPage : Page
{
    readonly Dictionary<string, ToggleSwitch> _toggles = [];
    readonly Dictionary<string, CardControl> _cards = [];
    readonly Dictionary<string, (TextBlock Header, FrameworkElement? Warning)> _headers = [];
    readonly Dictionary<Tweaks.Preset, TextBlock> _presetInfo = [];
    Dictionary<string, bool> _applied = [];
    bool _busy, _built;

    public TweaksPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
    }

    async Task Refresh()
    {
        _applied = await Task.Run(() => Tweaks.All.ToDictionary(t => t.Id, Tweaks.IsApplied));
        var available = await Task.Run(() => Tweaks.All.Where(Tweaks.Available).Select(t => t.Id).ToHashSet());
        if (!_built) Build(available);
        foreach (var t in Tweaks.All) _toggles[t.Id].IsChecked = _applied[t.Id];
        var count = Tweaks.All.Count(t => available.Contains(t.Id));
        Summary.Text = $"{_applied.Count(a => a.Value)} de {count} optimizaciones activas";
        var recPending = Tweaks.All.Count(t => t.Recommended && !_applied[t.Id] && available.Contains(t.Id));
        SummarySub.Text = recPending > 0 ? $"{recPending} recomendadas sin aplicar. Empieza por el perfil \"Recomendado\"." : "Todas las recomendadas están aplicadas.";
        foreach (var (p, info) in _presetInfo)
        {
            var n = Tweaks.All.Count(t => p.Includes(t) && available.Contains(t.Id) && !_applied[t.Id]);
            info.Text = n == 0 ? "✓ Todo aplicado" : $"{n} cambios pendientes";
        }
        ApplyFilter();
    }

    void Build(HashSet<string> available)
    {
        _built = true;
        foreach (var p in Tweaks.Presets)
        {
            var info = new TextBlock { FontSize = 12, Foreground = Ui.Accent, Margin = new Thickness(0, 6, 0, 0) };
            _presetInfo[p] = info;
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            body.Children.Add(Ui.Text(p.Description, "Muted", 0));
            body.Children.Add(info);
            var card = new CardAction { Icon = Ui.Icon(p.Icon, 24), Content = body, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16), Tag = p, VerticalAlignment = VerticalAlignment.Stretch };
            card.Click += Preset_Click;
            Presets.Children.Add(card);
        }

        GroupFilter.Items.Add("Todas las categorías");
        foreach (var group in Tweaks.All.Where(t => available.Contains(t.Id)).GroupBy(t => t.Group))
        {
            GroupFilter.Items.Add(group.Key);
            var header = new TextBlock { Text = group.Key, Style = (Style)FindResource("Section") };
            Groups.Children.Add(header);
            FrameworkElement? warning = null;
            if (group.All(t => t.Risk == Risk.Advanced))
            {
                warning = new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 10),
                    Title = "Avanzado", Message = "Cambian cómo funciona Windows. Son reversibles, pero lee la descripción: te pediremos confirmación una a una.",
                };
                Groups.Children.Add(warning);
            }
            _headers[group.Key] = (header, warning);
            foreach (var t in group) Groups.Children.Add(Card(t));
        }
        GroupFilter.SelectedIndex = 0;

        foreach (var (what, why) in Tweaks.NotIncluded)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = "✕  " + what, FontWeight = FontWeights.SemiBold });
            row.Children.Add(Ui.Text(why, "Muted", 0));
            NotIncluded.Children.Add(row);
        }
    }

    CardControl Card(Tweak t)
    {
        var toggle = new ToggleSwitch { Tag = t, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += Toggle_Click;
        _toggles[t.Id] = toggle;

        var titleRow = new WrapPanel();
        titleRow.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (t.Recommended) titleRow.Children.Add(Ui.Pill("Recomendada", accent: true));
        if (t.Risk == Risk.Advanced) titleRow.Children.Add(Ui.Pill("Avanzado", danger: true));
        if (t.Restart != null) titleRow.Children.Add(Ui.Pill(t.Restart));
        var header = new StackPanel();
        header.Children.Add(titleRow);
        header.Children.Add(Ui.Text(t.Description, "Muted", 0));

        var info = new Wpf.Ui.Controls.Button
        {
            Icon = Ui.Icon("Info24", 18), Appearance = ControlAppearance.Transparent, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "¿Qué cambia exactamente?", VerticalAlignment = VerticalAlignment.Center,
        };
        info.Click += async (_, _) => await Ui.Info(t.Title, DetailsPanel(t));
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(info);
        right.Children.Add(toggle);

        var card = new CardControl { Icon = Ui.Icon(t.Icon, 22), Header = header, Content = right, Margin = new Thickness(0, 0, 0, 6), Tag = t };
        _cards[t.Id] = card;
        return card;
    }

    static StackPanel DetailsPanel(Tweak t)
    {
        var p = new StackPanel { Width = 700 };
        p.Children.Add(Ui.Text(t.Description, null, 12));
        p.Children.Add(Ui.Text("Qué escribe en Windows:", "Muted", 4));
        p.Children.Add(new System.Windows.Controls.TextBox
        {
            Text = Tweaks.Details(t), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        p.Children.Add(Ui.Text($"\nCómo se deshace: apaga el interruptor (o \"Deshacer todo\"). Tidy guarda el valor que tenías antes en su copia de seguridad y lo vuelve a poner tal cual." +
                               (t.Restart != null ? $"\nSurte efecto tras: {t.Restart.ToLowerInvariant()}." : ""), "Muted", 0));
        return p;
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    void ApplyFilter()
    {
        if (!_built || StateFilter == null) return;
        var q = SearchBox.Text?.Trim() ?? "";
        var group = GroupFilter.SelectedIndex > 0 ? GroupFilter.SelectedItem as string : null;
        var visibleGroups = new HashSet<string>();
        foreach (var (id, card) in _cards)
        {
            var t = (Tweak)card.Tag;
            var show = (group == null || t.Group == group)
                       && (q == "" || t.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                       && StateFilter.SelectedIndex switch { 1 => _applied.GetValueOrDefault(id), 2 => !_applied.GetValueOrDefault(id), _ => true };
            card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) visibleGroups.Add(t.Group);
        }
        foreach (var (g, (header, warning)) in _headers)
        {
            var v = visibleGroups.Contains(g) ? Visibility.Visible : Visibility.Collapsed;
            header.Visibility = v;
            if (warning != null) warning.Visibility = v;
        }
        NoResults.Visibility = visibleGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var t = (Tweak)toggle.Tag;
        var apply = toggle.IsChecked == true;
        toggle.IsChecked = !apply; // stays where it was until the change really happened
        await Change([t], apply);
    }

    async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var p = (Tweaks.Preset)((FrameworkElement)sender).Tag;
        var pending = await Task.Run(() => Tweaks.Pending(p));
        if (pending.Count == 0 && !p.UltimatePlan) { Ui.Toast("Nada que aplicar", $"Todo lo del perfil \"{p.Name}\" ya está aplicado."); return; }

        var panel = new StackPanel { Width = 720 };
        panel.Children.Add(Ui.Text(p.Description + " Desmarca lo que no quieras: solo se aplica lo marcado.", null, 8));
        var boxes = new List<System.Windows.Controls.CheckBox>();
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void Count() => count.Text = $"{boxes.Count(b => b.IsChecked == true)} de {boxes.Count} marcados";

        var stack = new StackPanel();
        foreach (var g in pending.GroupBy(t => t.Group))
        {
            stack.Children.Add(new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (var t in g)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = $"{(t.Risk == Risk.Advanced ? "⚠ " : "")}{t.Title}{(t.Restart != null ? $"   [{t.Restart}]" : "")}", FontSize = 13 });
                text.Children.Add(new TextBlock { Text = t.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorTertiaryBrush") });
                var cb = new System.Windows.Controls.CheckBox { Content = text, Tag = t, IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
                cb.Click += (_, _) => Count();
                boxes.Add(cb);
                stack.Children.Add(cb);
            }
        }
        System.Windows.Controls.CheckBox? plan = null;
        if (p.UltimatePlan)
        {
            stack.Children.Add(new TextBlock { Text = "Energía", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            plan = new System.Windows.Controls.CheckBox { Content = "Activar el plan de energía \"Máximo rendimiento\"", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            stack.Children.Add(plan);
        }

        if (boxes.Count > 1)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var all = new Wpf.Ui.Controls.Button { Content = "Marcar todo", Margin = new Thickness(0, 0, 6, 0) };
            var none = new Wpf.Ui.Controls.Button { Content = "Desmarcar todo" };
            all.Click += (_, _) => { boxes.ForEach(b => b.IsChecked = true); Count(); };
            none.Click += (_, _) => { boxes.ForEach(b => b.IsChecked = false); Count(); };
            buttons.Children.Add(all);
            buttons.Children.Add(none);
            buttons.Children.Add(count);
            panel.Children.Add(buttons);
            Count();
        }
        panel.Children.Add(new ScrollViewer { Content = stack, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) });
        if (pending.Any(t => t.Risk == Risk.Advanced))
            panel.Children.Add(Ui.Text("⚠ = avanzado: lee su descripción antes de dejarlo marcado.", "Muted", 4));
        panel.Children.Add(Ui.Text("Todo se puede deshacer uno a uno o con \"Deshacer todo\". Se crea un punto de restauración antes.", "Muted", 0));
        if (!await Ui.Confirm($"¿Aplicar el perfil \"{p.Name}\"?", panel, "Aplicar seleccionados")) return;

        var chosen = boxes.Where(b => b.IsChecked == true).Select(b => (Tweak)b.Tag).ToList();
        if (chosen.Count == 0 && plan?.IsChecked != true) { Ui.Toast("Nada aplicado", "No había nada marcado."); return; }
        // snapshot first, so Historial can show the profile's real effect
        if (chosen.Count > 0) await Task.Run(() => Benchmark.Measure($"Antes del perfil {p.Name}"));
        if (chosen.Count > 0) await Change(chosen, true, confirmed: true);
        if (plan?.IsChecked == true)
        {
            await Task.Run(() => PowerPlan.Activate(PowerPlan.Ultimate));
        }
    }

    async void RevertAll_Click(object sender, RoutedEventArgs e)
    {
        var changed = await Task.Run(Tweaks.Changed);
        if (changed.Count == 0) { Ui.Toast("Nada que deshacer", "Tidy no ha cambiado ninguna optimización todavía."); return; }
        var panel = new StackPanel { Width = 640 };
        panel.Children.Add(Ui.Text("Se devolverá cada valor a lo que tenías antes de que Tidy lo cambiara:", null, 4));
        panel.Children.Add(Ui.Lines(changed.Select(t => "•  " + t.Title)));
        panel.Children.Add(Ui.Text("Lo que ya tenías activado antes de usar Tidy (por ejemplo con otro optimizador) no se toca.", "Muted", 0));
        if (await Ui.Confirm($"¿Deshacer {changed.Count} optimizaciones?", panel, "Deshacer todo"))
            await Change(changed, false);
    }

    async Task Change(List<Tweak> tweaks, bool apply, bool confirmed = false)
    {
        if (_busy) return;
        if (apply && !confirmed)
        {
            var t = tweaks[0];
            if (t.Risk == Risk.Advanced &&
                !await Ui.Confirm($"¿Aplicar \"{t.Title}\"?", $"{t.Description}\n\nSe puede deshacer en cualquier momento apagando el interruptor.", "Aplicar"))
                return;
        }
        if (apply && !await Ui.EnsureRestorePoint("Optimizaciones")) return;

        _busy = true;
        try
        {
            var failed = await Task.Run(() =>
            {
                var errors = new List<string>();
                foreach (var t in tweaks)
                    try { if (apply) Tweaks.Apply(t); else Tweaks.Revert(t); }
                    catch (Exception ex) { errors.Add(ex.Message); }
                return errors;
            });
            var restart = tweaks.Select(t => t.Restart).FirstOrDefault(r => r != null);
            var ok = tweaks.Count - failed.Count;
            if (failed.Count > 0)
                Ui.Toast(ok > 0 ? $"{ok} hechas, {failed.Count} no" : "No se pudo cambiar", string.Join("\n", failed.Take(3)) + "\nNo se ha quedado nada a medias.", ControlAppearance.Caution);
            else
            {
                // whole message is one template, so every language translates it (title and restart get translated inside the holes)
                var what = tweaks.Count == 1 ? tweaks[0].Title : $"{tweaks.Count} optimizaciones";
                Ui.Toast(apply ? "Aplicado" : "Valor original restaurado",
                    restart != null ? $"{what}\nSurte efecto tras: {restart.ToLowerInvariant()}." : what);
            }
            if (tweaks.Any(t => t.Restart == "Reiniciar Explorador") && failed.Count == 0 &&
                await Ui.Confirm("¿Reiniciar el Explorador ahora?", "Algunos cambios se ven al reiniciar el Explorador. Se cerrarán las ventanas de carpetas abiertas (tus archivos y apps no).", "Reiniciar Explorador"))
                Repairs.RestartExplorer();
        }
        finally
        {
            _busy = false;
            await Refresh();
        }
    }

    async void FindApps_Click(object sender, RoutedEventArgs e)
    {
        FindAppsBtn.IsEnabled = false;
        FindAppsBtn.Content = "Buscando…";
        List<(Tweaks.BloatApp App, string FullName)> found;
        try { found = await Task.Run(Tweaks.InstalledBloat); }
        finally { FindAppsBtn.IsEnabled = true; FindAppsBtn.Content = "Volver a buscar"; }
        Apps.ItemsSource = found.Select(f => new AppRow(f.App, f.FullName)).ToList();
        RemoveAppsBtn.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (found.Count == 0) Ui.Toast("Nada que quitar", "No hay apps preinstaladas de la lista en tu PC.");
    }

    async void RemoveApps_Click(object sender, RoutedEventArgs e)
    {
        var sel = ((IEnumerable<AppRow>)Apps.ItemsSource).Where(a => a.Selected).ToList();
        if (sel.Count == 0) { Ui.Toast("Marca alguna app", "Selecciona las apps que quieres quitar.", ControlAppearance.Caution); return; }
        var panel = new StackPanel { Width = 520 };
        foreach (var a in sel) panel.Children.Add(Ui.Text($"•  {a.Name}", null, 2));
        panel.Children.Add(Ui.Text("\nSe pueden reinstalar gratis desde Microsoft Store cuando quieras.", "Muted", 0));
        if (!await Ui.Confirm($"¿Desinstalar {sel.Count} apps?", panel, "Desinstalar", danger: true)) return;
        if (!await Ui.EnsureRestorePoint("Quitar apps preinstaladas")) return;

        RemoveAppsBtn.IsEnabled = false;
        var failed = await Task.Run(() => sel.Where(a => !Tweaks.RemoveApp(a.App, a.FullName)).Select(a => a.Name).ToList());
        RemoveAppsBtn.IsEnabled = true;
        if (failed.Count == 0) Ui.Toast("Apps desinstaladas", $"{sel.Count} apps quitadas.");
        else Ui.Toast("Algunas no se pudieron quitar", string.Join(", ", failed), ControlAppearance.Caution);
        FindApps_Click(sender, e);
    }
}
