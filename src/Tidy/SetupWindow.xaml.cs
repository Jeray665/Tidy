using System.Diagnostics;
using System.Windows;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>What Tidy-Setup.exe shows: install, update an older install, or uninstall (from "Installed apps").</summary>
public partial class SetupWindow : FluentWindow
{
    enum Mode { Install, Update, Installed, Uninstall, Done }
    Mode _mode;
    readonly string _version = Updater.Current.ToString();

    public SetupWindow(bool uninstall)
    {
        InitializeComponent();
        Theme.Attach(this);
        Closed += (_, _) => Application.Current.Shutdown();
        var installed = Install.InstalledVersion;
        Show(uninstall ? Mode.Uninstall
            : installed == null ? Mode.Install
            : installed < Updater.Current ? Mode.Update : Mode.Installed);
    }

    void Show(Mode mode)
    {
        _mode = mode;
        var installed = Install.InstalledVersion?.ToString(3) ?? "?";
        Error.IsOpen = false;
        Options.Visibility = mode is Mode.Install or Mode.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        Desktop.Visibility = Startup.Visibility = mode == Mode.Install ? Visibility.Visible : Visibility.Collapsed;
        DeleteData.Visibility = mode == Mode.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        Primary.Appearance = mode == Mode.Uninstall ? ControlAppearance.Danger : ControlAppearance.Primary;
        Secondary.Visibility = mode == Mode.Done ? Visibility.Collapsed : Visibility.Visible;
        Secondary.Content = mode == Mode.Installed ? "Reinstalar" : "Cancelar";
        (Title, Heading.Text, Sub.Text, Body.Text, Primary.Content) = mode switch
        {
            Mode.Install => ("Instalar Tidy", $"Instalar Tidy {_version}", "Optimizador seguro para Windows 11",
                $"Se instala en {Install.Dir}, una carpeta protegida que solo pueden cambiar los administradores. Aparecerá en el menú Inicio y en Configuración > Aplicaciones, desde donde se desinstala.\n\n¿Prefieres no instalar nada? Usa Tidy-Portable.exe: guarda todo junto al .exe y no deja nada más en el PC.",
                "Instalar"),
            Mode.Update => ("Actualizar Tidy", "Actualizar Tidy", $"De la versión {installed} a la {_version}",
                "Se conservan tus ajustes, el historial y las copias de seguridad. Si Tidy está abierto se cerrará un momento.", "Actualizar"),
            Mode.Installed => ("Tidy", "Tidy ya está instalado", $"Versión {installed}",
                "Puedes abrirlo, o volver a instalar esta versión. Tus ajustes y copias de seguridad se conservan.", "Abrir Tidy"),
            Mode.Uninstall => ("Desinstalar Tidy", "Desinstalar Tidy", $"Versión {installed}",
                "Se quitan el programa, sus accesos directos y su inicio automático.\n\nLas optimizaciones y demás cambios que hiciste se quedan como están. Si quieres deshacerlos, hazlo antes desde Historial. Si conservas los datos, al volver a instalar Tidy podrás deshacerlos igual.",
                "Desinstalar"),
            _ => ("Tidy", "Tidy se ha desinstalado", "", "Se ha quitado todo lo indicado. Gracias por usar Tidy.", "Cerrar"),
        };
    }

    async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Install or Mode.Update:
                bool desktop = Desktop.IsChecked == true, startup = Startup.IsChecked == true, update = _mode == Mode.Update;
                if (await Work(() => { if (update) { if (!Install.CloseRunning()) throw new System.IO.IOException("Tidy sigue abierto."); Install.ReplaceExe(Install.Current, Install.Exe); Install.Register(); }
                                       else Install.Run(desktop, startup); }))
                    Launch();
                break;
            case Mode.Installed: Launch(); break;
            case Mode.Uninstall:
                var delete = DeleteData.IsChecked == true;
                if (await Work(() => Install.Uninstall(delete))) Show(Mode.Done);
                break;
            default: Close(); break;
        }
    }

    async void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Installed) { Close(); return; }
        if (await Work(() => { if (!Install.CloseRunning()) throw new System.IO.IOException("Tidy sigue abierto."); Install.ReplaceExe(Install.Current, Install.Exe); Install.Register(); }))
            Launch();
    }

    async Task<bool> Work(Action action)
    {
        Primary.IsEnabled = Secondary.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try { await Task.Run(action); return true; }
        catch (Exception ex)
        {
            Store.Log($"Setup failed: {ex}");
            Error.Title = "No se pudo completar";
            Error.Message = ex.Message;
            Error.IsOpen = true;
            return false;
        }
        finally { Primary.IsEnabled = Secondary.IsEnabled = true; Busy.Visibility = Visibility.Collapsed; }
    }

    void Launch()
    {
        Process.Start(new ProcessStartInfo(Install.Exe) { UseShellExecute = false, WorkingDirectory = Install.Dir });
        Close();
    }
}
