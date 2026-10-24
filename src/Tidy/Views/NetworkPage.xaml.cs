using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public class AdapterRow(Adapter a)
{
    public SymbolRegular Icon => a.Wireless ? SymbolRegular.Wifi124 : SymbolRegular.PlugConnected24;
    public string Title => $"{a.Name} · {a.Description}";
    public string Detail => $"DNS: {(a.Dns.Length == 0 ? "ninguno" : string.Join(", ", a.Dns.Take(4)))} {(a.Static ? "(puesto a mano)" : "(automático, del router)")}";
}

public class ProviderRow(DnsProvider p, bool current) : Observable
{
    public DnsProvider Provider => p;
    public string Name => p.Name;
    public string Description => p.Servers.Length == 0 ? p.Description : $"{p.Description}  ({p.Servers[0]}, {p.Servers[1]})";
    public Visibility CurrentVisibility => current ? Visibility.Visible : Visibility.Collapsed;
    long? _ms; bool _measured;
    public string LatencyText => !_measured ? "" : _ms is { } m ? $"{m} ms" : "sin respuesta";
    public Brush LatencyBrush => Ui.Res(_ms is null ? "TextFillColorTertiaryBrush" : _ms < 20 ? "SystemFillColorSuccessBrush" : _ms < 50 ? "TextFillColorPrimaryBrush" : "SystemFillColorCautionBrush");
    public void SetLatency(long? ms) { _ms = ms; _measured = true; Changed(); }
}

public partial class NetworkPage : Page
{
    List<Adapter> _adapters = [];
    List<ProviderRow> _providers = [];

    public NetworkPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
        foreach (var f in Repairs.Fixes.Where(f => f.Id is "flushdns" or "netreset"))
        {
            var btn = new Wpf.Ui.Controls.Button { Content = "Ejecutar", Tag = f };
            btn.Click += async (_, _) => await ToolsPage.RunFix(f);
            var header = new StackPanel();
            header.Children.Add(new System.Windows.Controls.TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold });
            header.Children.Add(Ui.Text(f.Description, "Muted", 0));
            NetFixes.Children.Add(new CardControl { Icon = Ui.Icon(f.Icon, 22), Header = header, Content = btn, Margin = new Thickness(0, 0, 0, 6) });
        }
    }

    async Task Refresh()
    {
        _adapters = await Task.Run(Network.Adapters);
        AdapterList.ItemsSource = _adapters.Select(a => new AdapterRow(a)).ToList();
        var vpns = await Task.Run(Network.ActiveVpns);
        VpnBar.IsOpen = vpns.Count > 0;
        VpnBar.Message = $"Tienes activa: {string.Join(", ", vpns)}. Tidy no toca el DNS de las VPN (las rompería). Mientras estén conectadas suelen imponer su propio DNS, así que un cambio aquí puede no notarse hasta que las desconectes.";
        var current = _adapters.Select(Network.CurrentProvider).Distinct().ToList();
        _providers = Network.Providers.Select(p => new ProviderRow(p, current.Count == 1 && current[0] == p.Id)).ToList();
        ProviderList.ItemsSource = _providers;
        RestoreDnsBtn.IsEnabled = await Task.Run(() => Network.HasDnsBackup);
        BlockToggle.IsChecked = await Task.Run(() => Network.HasBlockList);
        LockToggle.IsChecked = Network.HostsReadOnly;
    }

    async void Measure_Click(object sender, RoutedEventArgs e)
    {
        MeasureBtn.IsEnabled = false;
        await Task.WhenAll(_providers.Where(p => p.Provider.Servers.Length > 0)
            .Select(async p => p.SetLatency(await Network.Latency(p.Provider.Servers[0]))));
        MeasureBtn.IsEnabled = true;
    }

    async void Provider_Click(object sender, RoutedEventArgs e)
    {
        var p = ((ProviderRow)((FrameworkElement)sender).Tag).Provider;
        if (_adapters.Count == 0) { Ui.Toast("Sin conexión", "No hay ninguna conexión de red activa.", ControlAppearance.Caution); return; }
        var msg = p.Servers.Length == 0
            ? "Tus conexiones volverán a usar el DNS que da tu router."
            : $"Servidores: {string.Join(", ", p.Servers)}\n\nSe aplicará a: {string.Join(", ", _adapters.Select(a => a.Name))}.\n\nTidy guarda el DNS que tienes ahora para poder volver con \"Restaurar DNS original\". Si alguna web no carga, vuelve a \"Automático\".";
        if (!await Ui.Confirm($"¿Usar {p.Name}?", msg, "Cambiar DNS")) return;
        var errors = await Task.Run(() => _adapters.Select(a => (a.Name, Network.SetDns(a, p.Servers))).Where(r => !r.Item2.Ok).Select(r => $"{r.Name}: {r.Item2.Message}").ToList());
        Ui.Toast(errors.Count == 0 ? "DNS cambiado" : "No se pudo cambiar en todas", errors.Count == 0 ? $"Usando {p.Name}." : string.Join("\n", errors),
            errors.Count == 0 ? ControlAppearance.Success : ControlAppearance.Caution);
        await Refresh();
    }

    async void RestoreDns_Click(object sender, RoutedEventArgs e)
    {
        var n = await Task.Run(Network.RestoreDns);
        Ui.Toast("DNS restaurado", $"{n} conexiones vuelven al DNS que tenían antes de Tidy.");
        await Refresh();
    }

    async void Block_Click(object sender, RoutedEventArgs e)
    {
        var on = BlockToggle.IsChecked == true;
        BlockToggle.IsChecked = !on;
        BlockToggle.IsEnabled = false;
        try
        {
            if (on)
            {
                if (!await Ui.Confirm("¿Bloquear anuncios en todo el PC?",
                        "Tidy descargará la lista de StevenBlack (github.com/StevenBlack/hosts) y añadirá solo líneas del tipo \"0.0.0.0 dominio\", que únicamente bloquean: no pueden redirigirte a ningún sitio.\n\n" +
                        "Antes se guarda una copia de tu archivo hosts actual. Si alguna web o app deja de funcionar, apaga este interruptor.", "Descargar y bloquear")) return;
                var (ok, msg) = await Network.AddBlockList();
                Ui.Toast(ok ? "Bloqueo activado" : "No se pudo activar", msg, ok ? ControlAppearance.Success : ControlAppearance.Danger);
            }
            else
            {
                await Task.Run(Network.RemoveBlockList);
                Ui.Toast("Bloqueo desactivado", "Lista de bloqueo quitada del archivo hosts. Lo demás que tuvieras se queda igual.");
            }
        }
        catch (Exception ex) { Ui.Toast("No se pudo cambiar el archivo hosts", ex.Message, ControlAppearance.Danger); }
        finally { BlockToggle.IsEnabled = true; await Refresh(); }
    }

    async void Lock_Click(object sender, RoutedEventArgs e)
    {
        try { Network.SetHostsReadOnly(LockToggle.IsChecked == true); }
        catch (Exception ex) { Ui.Toast("No se pudo cambiar", ex.Message, ControlAppearance.Danger); }
        await Refresh();
    }

    async void EditHosts_Click(object sender, RoutedEventArgs e)
    {
        var text = Network.ReadHosts();
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, Height = 380, Width = 740,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("Formato: IP espacio dominio, una por línea. Ej.: 0.0.0.0 anuncios.ejemplo.com para bloquear. Las líneas con # son comentarios.", "Muted", 8));
        panel.Children.Add(box);
        if (!await Ui.Confirm("Archivo hosts", panel, "Guardar")) return;
        if (box.Text == text) return;
        try
        {
            var copy = await Task.Run(() => Network.WriteHosts(box.Text));
            Ui.Toast("Archivo hosts guardado", $"La versión anterior está guardada en Copias de seguridad.");
        }
        catch (Exception ex) { Ui.Toast("No se pudo guardar", ex.Message, ControlAppearance.Danger); }
        await Refresh();
    }

    async void Ping_Click(object sender, RoutedEventArgs e)
    {
        var host = PingHost.Text.Trim();
        if (host == "") return;
        PingResult.Text = "Midiendo…";
        var ms = await Network.Latency(host);
        PingResult.Text = ms is { } m ? $"{host}: {m} ms de media. {(m < 20 ? "Excelente." : m < 50 ? "Bien." : m < 100 ? "Normal." : "Alta: puede notarse en juegos online.")}" : $"{host} no responde (puede que bloquee el ping o que no haya conexión).";
    }
}
