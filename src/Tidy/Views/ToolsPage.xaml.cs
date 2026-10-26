using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        foreach (var f in Repairs.Fixes)
        {
            var btn = new Wpf.Ui.Controls.Button { Content = f.Console ? "Abrir" : "Ejecutar" };
            btn.Click += async (_, _) => await RunFix(f);
            var header = new StackPanel();
            var title = new WrapPanel();
            title.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold });
            if (f.NeedsRestart) title.Children.Add(Ui.Pill("Reinicio"));
            if (f.Console) title.Children.Add(Ui.Pill("Ventana aparte"));
            header.Children.Add(title);
            header.Children.Add(Ui.Text(f.Description, "Muted", 0));
            Fixes.Children.Add(new CardControl { Icon = Ui.Icon(f.Icon, 22), Header = header, Content = btn, Margin = new Thickness(0, 0, 0, 6) });
        }
        foreach (var t in Repairs.Tools.Where(t => File.Exists(t.Exe) && (t.Args == "" || !t.Args.EndsWith(".msc") || File.Exists(t.Args))))
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = t.Description, Style = (Style)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            var card = new CardAction { Icon = Ui.Icon(t.Icon, 22), Content = body, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 12, 14, 12), ToolTip = t.Description };
            card.Click += (_, _) =>
            {
                try { Repairs.Launch(t); Store.Log($"Tool opened: {t.Name}"); }
                catch (Exception ex) { Ui.Toast("No se pudo abrir", ex.Message, ControlAppearance.Danger); }
            };
            ToolGrid.Children.Add(card);
        }
    }

    public static async Task RunFix(Repairs.Fix f)
    {
        var confirm = f.Id switch
        {
            "netreset" => "Se restablecerá la configuración de red de Windows. Tendrás que reiniciar y, si usas VPN, quizá reinstalarla. Las contraseñas Wi-Fi se conservan.",
            "advboot" => "El PC se reiniciará en 5 segundos en el menú de opciones avanzadas. Guarda tu trabajo antes. Desde ese menú puedes volver a Windows normalmente.",
            "explorer" => "Se cerrarán las ventanas de carpetas abiertas y la barra de tareas se recargará. Tus programas no se cierran.",
            "policies" => "Se quitarán las directivas que bloquean el Editor del Registro, el Administrador de tareas y el Símbolo del sistema. Si tu PC es de una empresa, pregunta antes a tu departamento de IT.",
            _ => null,
        };
        if (confirm != null && !await Ui.Confirm($"¿{f.Name}?", confirm, "Continuar", danger: f.Id == "advboot")) return;
        var (ok, msg) = await Task.Run(() => Repairs.Run(f));
        if (f.Console) Ui.Toast("Abierto en otra ventana", $"{f.Name}: sigue el progreso en la ventana negra. No la cierres hasta que termine.");
        else Ui.Toast(ok ? "Hecho" : "No se pudo completar", ok ? f.Name + (f.NeedsRestart ? ". Reinicia el PC para terminar." : ".") : msg,
            ok ? ControlAppearance.Success : ControlAppearance.Danger);
    }

    async void WhoLocks_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = I18n.T("Archivo que está en uso") };
        if (dlg.ShowDialog() != true) return;
        LockResult.Children.Clear();
        var asked = DateTime.Now;
        var who = await Task.Run(() => Processes.WhoLocks(dlg.FileName));
        if (who.Count == 0)
        {
            LockResult.Children.Add(Ui.Text($"Nadie tiene abierto {Path.GetFileName(dlg.FileName)}. Ya deberías poder borrarlo o moverlo.", null, 0));
            return;
        }
        LockResult.Children.Add(Ui.Text($"{Path.GetFileName(dlg.FileName)} lo tiene abierto:", null, 6));
        foreach (var (pid, app) in who)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = $"{app}  (proceso {pid})", VerticalAlignment = VerticalAlignment.Center });
            var close = new Wpf.Ui.Controls.Button { Content = "Cerrar este programa", HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += async (_, _) =>
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    // the process ids get reused: one that started after the search is another program
                    if (p.StartTime > asked) { Ui.Toast("Ya se cerró", "Ese programa ya no está abierto.", ControlAppearance.Caution); return; }
                    if (Processes.IsCritical(p)) { Ui.Toast("Proceso de Windows", "Es un proceso del sistema: reinicia el PC para liberar el archivo.", ControlAppearance.Caution); return; }
                    if (!await Ui.Confirm($"¿Cerrar {app}?", "Se cerrará sin guardar. Si tiene trabajo pendiente, guárdalo antes desde el propio programa.", "Cerrar", danger: true)) return;
                    p.Kill();
                    Store.Log($"Tools: closed {app} ({pid}) that was locking {dlg.FileName}");
                    Ui.Toast("Programa cerrado", "El archivo ya debería estar libre.");
                }
                catch (Exception ex) { Ui.Toast("No se pudo cerrar", ex.Message, ControlAppearance.Danger); }
            };
            row.Children.Add(close);
            LockResult.Children.Add(row);
        }
    }
}
