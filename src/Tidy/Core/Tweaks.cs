using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tidy.Core;

public enum Risk { Safe, Advanced }

/// <param name="Default">Windows' stock value, used for undo only when there's no backup. null = value doesn't exist by default.</param>
public record RegOp(string Key, string Name, object Value, object? Default);

public class Tweak
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Icon { get; init; }
    public Risk Risk { get; init; }
    /// <summary>Part of "Aplicar recomendadas": only things that are pure upside for almost everyone.</summary>
    public bool Recommended { get; init; }
    public string? Restart { get; init; }
    public RegOp[] Ops { get; init; } = [];
    public Func<bool>? Check { get; init; }
    public Action? CustomApply { get; init; }
    public Action? CustomRevert { get; init; }
}

public class SavedValue
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string Kind { get; set; } = "none"; // none | dword | sz
    public string? Value { get; set; }
}

public static class Tweaks
{
    const string BackupFile = "tweaks-backup.json";

    const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string Mm = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string Svc = @"HKLM\SYSTEM\CurrentControlSet\Services\";
    const string ClassicMenu = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    // Groups, in display order
    public const string GGames = "Juegos y rendimiento", GSmooth = "Fluidez", GExplorer = "Explorador de archivos",
        GTaskbar = "Barra de tareas e Inicio", GAds = "Anuncios y sugerencias", GPrivacy = "Privacidad",
        GUpdate = "Windows Update y red", GSystem = "Sistema", GAdvanced = "Avanzado";

    // Deliberately absent: anything that lowers security (UAC, Defender, SmartScreen, VBS/HVCI, CPU mitigations,
    // firewall, Windows Update off). They're the classic "optimizer" tweaks and the classic way to get a PC compromised.
    public static readonly List<Tweak> All =
    [
        // ---------- Juegos y rendimiento ----------
        new()
        {
            Id = "gamemode", Group = GGames, Icon = "Games24", Title = "Modo de juego de Windows", Recommended = true,
            Description = "Windows da prioridad al juego activo y pausa tareas de fondo (como Windows Update) mientras juegas.",
            Ops = [new(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null)],
        },
        new()
        {
            Id = "hags", Group = GGames, Icon = "Flash24", Title = "Programación de GPU acelerada por hardware", Recommended = true,
            Description = "La GPU gestiona su propia memoria. Reduce latencia y es necesaria para DLSS Frame Generation.", Restart = "Reinicio",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)],
        },
        new()
        {
            Id = "gamedvr", Group = GGames, Icon = "Timer24", Title = "Desactivar grabación en segundo plano", Recommended = true,
            Description = "Apaga la captura continua de Xbox Game Bar, que graba los últimos minutos y consume GPU. Las capturas manuales siguen funcionando.",
            Ops = [new(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                   new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, null)],
        },
        new()
        {
            Id = "mmcss", Group = GGames, Icon = "TopSpeed24", Title = "Priorizar juegos en el planificador", Recommended = true,
            Description = "Reserva menos CPU para tareas de fondo (20% → 10%) y sube la prioridad de los juegos en el programador multimedia.", Restart = "Reinicio",
            Ops = [new(Mm, "SystemResponsiveness", 10, 20),
                   new(Mm + @"\Tasks\Games", "GPU Priority", 8, 8),
                   new(Mm + @"\Tasks\Games", "Priority", 6, 2),
                   new(Mm + @"\Tasks\Games", "Scheduling Category", "High", "Medium")],
        },
        new()
        {
            Id = "netthrottle", Group = GGames, Icon = "Wifi124", Title = "Quitar el límite de red multimedia",
            Description = "Windows frena el tráfico de red mientras suena audio o vídeo. Quitarlo puede evitar picos de ping jugando con música o Discord.", Restart = "Reinicio",
            Ops = [new(Mm, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10)],
        },
        new()
        {
            Id = "powerthrottle", Group = GGames, Icon = "Battery1024", Title = "Desactivar la limitación de energía de procesos",
            Description = "Windows frena apps en segundo plano para ahorrar batería. En un sobremesa no aporta nada; en un portátil gastará algo más de batería.", Restart = "Reinicio",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null)],
        },
        new()
        {
            Id = "fse", Group = GGames, Icon = "FullScreenMaximize24", Title = "Desactivar optimizaciones de pantalla completa",
            Description = "Usa pantalla completa exclusiva real en juegos DirectX 9-11. Puede bajar la latencia en algunos juegos, a cambio de un Alt+Tab más lento.",
            Ops = [new(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0),
                   new(@"HKCU\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0),
                   new(@"HKCU\System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0)],
        },
        new()
        {
            Id = "mouseaccel", Group = GGames, Icon = "Cursor24", Title = "Desactivar aceleración del ratón",
            Description = "El puntero se mueve exactamente lo que mueves el ratón (\"precisión del puntero\" desactivada). Ideal para apuntar en juegos.", Restart = "Cerrar sesión",
            Ops = [new(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", "1"),
                   new(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                   new(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", "10")],
        },
        new()
        {
            Id = "stickykeys", Group = GGames, Icon = "Keyboard24", Title = "Que Shift ×5 no abra las Teclas especiales", Recommended = true,
            Description = "Evita la ventana de \"Teclas especiales\" que salta al pulsar Shift varias veces jugando. La función sigue disponible en Accesibilidad.",
            Ops = [new(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506", "510")],
        },

        // ---------- Fluidez ----------
        new()
        {
            Id = "menudelay", Group = GSmooth, Icon = "Navigation24", Title = "Menús más rápidos", Recommended = true,
            Description = "Los submenús se abren a los 100 ms en vez de 400 ms.", Restart = "Cerrar sesión",
            Ops = [new(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "100", "400")],
        },
        new()
        {
            Id = "startupdelay", Group = GSmooth, Icon = "Rocket24", Title = "Quitar la espera de las apps de inicio", Recommended = true,
            Description = "Windows retrasa unos segundos las apps que arrancan con el PC. Con un SSD no hace falta.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null),
                   new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", 0, null)],
        },
        new()
        {
            Id = "animations", Group = GSmooth, Icon = "WindowNew24", Title = "Quitar animaciones de ventanas",
            Description = "Las ventanas se minimizan y maximizan al instante, sin animación.", Restart = "Cerrar sesión",
            Ops = [new(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"),
                   new(Adv, "TaskbarAnimations", 0, null)],
        },
        new()
        {
            Id = "transparency", Group = GSmooth, Icon = "Blur24", Title = "Quitar transparencias",
            Description = "Menús y barra de tareas opacos. Ahorra un poco de GPU; es lo mismo que el interruptor de Personalización › Colores.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)],
        },

        // ---------- Explorador ----------
        new()
        {
            Id = "ext", Group = GExplorer, Icon = "DocumentText24", Title = "Mostrar extensiones de archivo", Recommended = true,
            Description = "Verás \"factura.pdf.exe\" en vez de \"factura.pdf\": es la forma más fácil de detectar un archivo malicioso disfrazado.",
            Ops = [new(Adv, "HideFileExt", 0, 1)],
        },
        new()
        {
            Id = "hidden", Group = GExplorer, Icon = "Eye24", Title = "Mostrar archivos ocultos",
            Description = "Muestra carpetas como AppData. Los archivos protegidos del sistema siguen ocultos.",
            Ops = [new(Adv, "Hidden", 1, 2)],
        },
        new()
        {
            Id = "thispc", Group = GExplorer, Icon = "Desktop24", Title = "Abrir el Explorador en \"Este equipo\"",
            Description = "Al abrir el Explorador ves tus discos directamente en vez de la página de Inicio.",
            Ops = [new(Adv, "LaunchTo", 1, null)],
        },
        new()
        {
            Id = "classicmenu", Group = GExplorer, Icon = "TextBulletListLtr24", Title = "Menú contextual clásico",
            Description = "El clic derecho muestra el menú completo de siempre, sin tener que pulsar \"Mostrar más opciones\".", Restart = "Reiniciar Explorador",
            Check = () => Read($@"HKCU\{ClassicMenu}\InprocServer32", "") != null,
            CustomApply = () => Write($@"HKCU\{ClassicMenu}\InprocServer32", "", ""),
            CustomRevert = () =>
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID", true);
                k?.DeleteSubKeyTree(ClassicMenu[(ClassicMenu.LastIndexOf('\\') + 1)..], false);
            },
        },
        new()
        {
            Id = "compact", Group = GExplorer, Icon = "TextDensity24", Title = "Vista compacta",
            Description = "Menos espacio entre archivos: caben más en pantalla.",
            Ops = [new(Adv, "UseCompactMode", 1, 0)],
        },
        new()
        {
            Id = "recent", Group = GExplorer, Icon = "History24", Title = "No mostrar archivos recientes en el Explorador",
            Description = "La página de Inicio del Explorador deja de mostrar lo último que abriste y tus carpetas frecuentes.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent", 0, null),
                   new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowFrequent", 0, null)],
        },
        new()
        {
            Id = "fullpath", Group = GExplorer, Icon = "FolderOpen24", Title = "Ruta completa en la barra de título", Restart = "Reiniciar Explorador",
            Description = "La ventana del Explorador muestra la ruta entera (C:\\Users\\…) en lugar de solo el nombre de la carpeta.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", 1, null)],
        },
        new()
        {
            Id = "autoplay", Group = GExplorer, Icon = "UsbStick24", Title = "Desactivar reproducción automática", Recommended = true,
            Description = "Al conectar un USB o disco no se ejecuta nada solo. Es una protección clásica contra USB infectados.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 1, 0)],
        },

        // ---------- Barra de tareas e Inicio ----------
        new()
        {
            Id = "endtask", Group = GTaskbar, Icon = "DismissSquare24", Title = "\"Finalizar tarea\" en la barra de tareas", Recommended = true,
            Description = "Clic derecho en una app colgada › Finalizar tarea, sin abrir el Administrador de tareas.",
            Ops = [new(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null)],
        },
        new()
        {
            Id = "startrecs", Group = GTaskbar, Icon = "Star24", Title = "Inicio sin recomendaciones", Recommended = true,
            Description = "Quita las apps recomendadas y los avisos de cuenta de Microsoft del menú Inicio.",
            Ops = [new(Adv, "Start_IrisRecommendations", 0, null),
                   new(Adv, "Start_AccountNotifications", 0, null)],
        },
        new()
        {
            Id = "startpins", Group = GTaskbar, Icon = "Pin24", Title = "Más apps ancladas en Inicio",
            Description = "Más espacio para tus apps ancladas y menos para recomendaciones.",
            Ops = [new(Adv, "Start_Layout", 1, null)],
        },
        new()
        {
            Id = "taskbarleft", Group = GTaskbar, Icon = "AlignLeft24", Title = "Barra de tareas alineada a la izquierda",
            Description = "Botón de Inicio en la esquina, como en Windows 10.",
            Ops = [new(Adv, "TaskbarAl", 0, null)],
        },
        new()
        {
            Id = "searchicon", Group = GTaskbar, Icon = "Search24", Title = "Búsqueda solo como icono",
            Description = "Sustituye la caja de búsqueda grande por un icono y deja más sitio a tus apps.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1, null)],
        },
        new()
        {
            Id = "taskview", Group = GTaskbar, Icon = "Grid24", Title = "Ocultar el botón Vista de tareas",
            Description = "Win+Tab sigue funcionando.",
            Ops = [new(Adv, "ShowTaskViewButton", 0, null)],
        },
        new()
        {
            Id = "seconds", Group = GTaskbar, Icon = "Clock24", Title = "Segundos en el reloj",
            Description = "El reloj de la barra de tareas muestra también los segundos.",
            Ops = [new(Adv, "ShowSecondsInSystemClock", 1, null)],
        },

        // ---------- Anuncios y sugerencias ----------
        new()
        {
            Id = "ads", Group = GAds, Icon = "MegaphoneOff24", Title = "Quitar anuncios y sugerencias", Recommended = true,
            Description = "Sin apps sugeridas en Inicio, consejos, \"experiencias personalizadas\" ni instalación silenciosa de apps promocionadas.",
            Ops = [new(Cdm, "SubscribedContent-338388Enabled", 0, null),
                   new(Cdm, "SubscribedContent-338389Enabled", 0, null),
                   new(Cdm, "SubscribedContent-353694Enabled", 0, null),
                   new(Cdm, "SubscribedContent-353696Enabled", 0, null),
                   new(Cdm, "SystemPaneSuggestionsEnabled", 0, null),
                   new(Cdm, "SilentInstalledAppsEnabled", 0, null),
                   new(Cdm, "SoftLandingEnabled", 0, null),
                   new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, null)],
        },
        new()
        {
            Id = "bing", Group = GAds, Icon = "Search24", Title = "Buscador sin resultados web de Bing", Recommended = true,
            Description = "La búsqueda de Inicio solo muestra tus apps, archivos y ajustes. Más rápida y sin publicidad.", Restart = "Reinicio",
            Ops = [new(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)],
        },
        new()
        {
            Id = "lockscreen", Group = GAds, Icon = "LockClosed24", Title = "Pantalla de bloqueo sin anuncios", Recommended = true,
            Description = "Quita los \"datos curiosos\", consejos y promociones de la pantalla de bloqueo.",
            Ops = [new(Cdm, "RotatingLockScreenOverlayEnabled", 0, null),
                   new(Cdm, "SubscribedContent-338387Enabled", 0, null)],
        },
        new()
        {
            Id = "settingsads", Group = GAds, Icon = "Alert24", Title = "Sin sugerencias en Configuración ni notificaciones de consejos", Recommended = true,
            Description = "Windows deja de recomendar servicios de Microsoft en Configuración y en notificaciones.",
            Ops = [new(Cdm, "SubscribedContent-338393Enabled", 0, null),
                   new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0, null)],
        },
        new()
        {
            Id = "finishsetup", Group = GAds, Icon = "PersonFeedback24", Title = "Sin \"Terminemos de configurar el dispositivo\"", Recommended = true,
            Description = "Quita la pantalla que aparece tras algunas actualizaciones ofreciendo OneDrive, Microsoft 365 y similares.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null)],
        },
        new()
        {
            Id = "explorerads", Group = GAds, Icon = "Cloud24", Title = "Sin anuncios de OneDrive en el Explorador", Recommended = true,
            Description = "Quita los avisos promocionales de OneDrive y Microsoft 365 dentro del Explorador.",
            Ops = [new(Adv, "ShowSyncProviderNotifications", 0, null)],
        },
        new()
        {
            Id = "edgebg", Group = GAds, Icon = "Globe24", Title = "Que Edge no se quede en segundo plano",
            Description = "Edge deja de precargarse al iniciar Windows y de seguir abierto al cerrarlo (ahorra RAM). Edge mostrará \"administrado por tu organización\": es normal.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null)],
        },

        // ---------- Privacidad ----------
        new()
        {
            Id = "tailored", Group = GPrivacy, Icon = "PersonInfo24", Title = "Sin experiencias personalizadas", Recommended = true,
            Description = "Microsoft no usa tus datos de diagnóstico para mostrarte consejos y anuncios a medida.",
            Ops = [new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, null)],
        },
        new()
        {
            Id = "inking", Group = GPrivacy, Icon = "Pen24", Title = "No enviar lo que escribes ni dibujas", Recommended = true,
            Description = "Windows deja de recopilar tu escritura y tu tinta para \"mejorar\" el teclado y el reconocimiento.",
            Ops = [new(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, null),
                   new(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, null),
                   new(@"HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, null)],
        },
        new()
        {
            Id = "feedback", Group = GPrivacy, Icon = "CommentOff24", Title = "Que Windows no pida opiniones", Recommended = true,
            Description = "Sin ventanas de \"¿Qué te parece Windows?\". Puedes seguir enviando comentarios cuando quieras.",
            Ops = [new(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)],
        },
        new()
        {
            Id = "langlist", Group = GPrivacy, Icon = "LocalLanguage24", Title = "No compartir tu lista de idiomas con webs", Recommended = true,
            Description = "Las webs no pueden leer tus idiomas instalados para mostrarte contenido local o identificarte.",
            Ops = [new(@"HKCU\Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, null)],
        },
        new()
        {
            Id = "apptrack", Group = GPrivacy, Icon = "DataUsage24", Title = "No registrar las apps que abres",
            Description = "Inicio y la búsqueda dejan de ordenar por \"más usadas\".",
            Ops = [new(Adv, "Start_TrackProgs", 0, null)],
        },
        new()
        {
            Id = "activity", Group = GPrivacy, Icon = "History24", Title = "Desactivar historial de actividad",
            Description = "Windows deja de registrar qué apps y archivos usas.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null)],
        },
        new()
        {
            Id = "location", Group = GPrivacy, Risk = Risk.Advanced, Icon = "LocationOff24", Title = "Desactivar la ubicación por completo",
            Description = "Ni Windows ni las apps podrán saber dónde estás. El tiempo local, \"Buscar mi dispositivo\" y la zona horaria automática dejarán de funcionar.", Restart = "Reinicio",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, null)],
        },

        // ---------- Windows Update y red ----------
        new()
        {
            Id = "wurestart", Group = GUpdate, Icon = "ArrowClockwise24", Title = "Que Windows Update no reinicie con la sesión abierta", Recommended = true,
            Description = "Las actualizaciones se siguen instalando, pero el PC no se reinicia solo mientras lo estás usando.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, null)],
        },
        new()
        {
            Id = "deliveryopt", Group = GUpdate, Icon = "ArrowUpload24", Title = "No compartir actualizaciones con otros PCs", Recommended = true,
            Description = "Tu conexión deja de usarse para enviar trozos de actualizaciones a desconocidos por Internet. Las descargas vienen solo de Microsoft.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)],
        },
        new()
        {
            Id = "llmnr", Group = GUpdate, Risk = Risk.Advanced, Icon = "Shield24", Title = "Desactivar LLMNR",
            Description = "Cierra un protocolo antiguo de nombres de red muy usado para robar contraseñas en redes públicas. En casas raras veces se nota; algunos NAS antiguos podrían no encontrarse por nombre.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, null)],
        },

        // ---------- Sistema ----------
        new()
        {
            Id = "remoteassist", Group = GSystem, Icon = "PersonDelete24", Title = "Desactivar Asistencia remota", Recommended = true,
            Description = "Nadie puede pedirte acceso remoto con la Asistencia de Windows (un truco típico de estafas de \"soporte técnico\"). Escritorio remoto no se toca.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1)],
        },
        new()
        {
            Id = "longpaths", Group = GSystem, Icon = "Folder24", Title = "Permitir rutas largas",
            Description = "Quita el límite de 260 caracteres en rutas de archivo para las apps que lo soportan (útil para programación y descargas profundas).",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)],
        },
        new()
        {
            Id = "lastaccess", Group = GSystem, Icon = "Storage24", Title = "No anotar la fecha de último acceso", Restart = "Reinicio",
            Description = "Cada vez que se lee un archivo, NTFS escribe la hora de acceso. Desactivarlo ahorra escrituras en disco.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", unchecked((int)0x80000001), unchecked((int)0x80000002))],
        },
        new()
        {
            Id = "fastboot", Group = GSystem, Icon = "Power24", Title = "Desactivar el inicio rápido",
            Description = "\"Apagar\" pasa a ser un apagado completo. Arregla problemas de drivers y de arranque dual; el encendido tarda unos segundos más.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)],
        },
        new()
        {
            Id = "verbose", Group = GSystem, Icon = "TextDescription24", Title = "Mensajes detallados al arrancar y apagar",
            Description = "En lugar de \"Espera\", Windows dice qué está haciendo. Útil si se queda colgado al apagar.",
            Ops = [new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1, null)],
        },
        new()
        {
            Id = "bsod", Group = GSystem, Icon = "ErrorCircle24", Title = "Detalles técnicos en pantallazos azules",
            Description = "Si Windows falla, la pantalla azul muestra los códigos de error para saber qué driver lo causó.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, null)],
        },
        new()
        {
            Id = "numlock", Group = GSystem, Icon = "NumberSymbol24", Title = "Bloq Num activado al iniciar sesión",
            Description = "El teclado numérico funciona nada más entrar en Windows.",
            Ops = [new(@"HKCU\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0")],
        },

        new()
        {
            Id = "regbackup", Group = GSystem, Icon = "DatabaseArrowDown20", Title = "Copias automáticas del registro", Recommended = true,
            Description = "Windows vuelve a guardar copias del registro en System32\\config\\RegBack (viene desactivado desde 2019). Sirve para recuperar Windows si el registro se corrompe.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager", "EnablePeriodicBackup", 1, null)],
        },
        new()
        {
            Id = "smb1", Group = GSystem, Icon = "ShieldLock24", Title = "Bloquear SMBv1", Recommended = true,
            Description = "Protocolo de compartir archivos de los 80, el que usó WannaCry. Windows 11 ya lo trae quitado; esto lo bloquea también por si algo lo reactiva.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", 0, null)],
        },
        new()
        {
            Id = "utc", Group = GSystem, Icon = "Clock24", Title = "Reloj de la BIOS en hora UTC", Restart = "Reinicio",
            Description = "Solo si tienes Linux en arranque dual: evita que la hora se desajuste al cambiar de sistema. Si no usas Linux, no lo actives.",
            Ops = [new(@"HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1, null)],
        },
        new()
        {
            Id = "spellcheck", Group = GSystem, Icon = "TextProofingTools24", Title = "Desactivar autocorrección y corrector del sistema",
            Description = "Windows deja de corregir y subrayar palabras en el teclado táctil y en los campos de texto del sistema. Los correctores de Word o del navegador no se tocan.",
            Ops = [new(@"HKCU\Software\Microsoft\TabletTip\1.7", "EnableAutocorrection", 0, null),
                   new(@"HKCU\Software\Microsoft\TabletTip\1.7", "EnableSpellchecking", 0, null)],
        },
        new()
        {
            Id = "ink", Group = GSystem, Icon = "Pen24", Title = "Desactivar Windows Ink Workspace",
            Description = "Quita el espacio de trabajo para lápiz digital. Solo útil en pantallas táctiles con lápiz.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 0, null)],
        },
        new()
        {
            Id = "snapassist", Group = GSmooth, Icon = "Grid24", Title = "Sin sugerencias de Snap",
            Description = "Al acoplar una ventana a un lado no te propone qué poner al otro, ni aparece el menú de diseños al pasar por el botón maximizar. Win+flechas sigue funcionando.",
            Ops = [new(Adv, "SnapAssist", 0, null),
                   new(Adv, "EnableSnapAssistFlyout", 0, null)],
        },
        new()
        {
            Id = "castmenu", Group = GExplorer, Icon = "Cast24", Title = "Quitar \"Transmitir a dispositivo\" del clic derecho", Restart = "Reiniciar Explorador",
            Description = "Limpia el menú contextual de una opción que casi nadie usa.",
            Ops = [new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "{7AD84985-87B4-4a16-BE58-8B72A5B390F7}", "", null)],
        },
        new()
        {
            Id = "photoviewer", Group = GExplorer, Icon = "Image24", Title = "Recuperar el Visor de fotos clásico",
            Description = "Vuelve a aparecer en \"Abrir con\" para JPG, PNG, BMP y GIF. Es ligerísimo. No cambia tu app predeterminada: eso lo eliges tú.",
            Ops = [.. new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".ico" }.Select(e =>
                   new RegOp(@"HKLM\SOFTWARE\Microsoft\Windows Photo Viewer\Capabilities\FileAssociations", e, "PhotoViewer.FileAssoc.Tiff", null))],
        },

        // ---------- Privacidad (apps de terceros y servicios) ----------
        new()
        {
            Id = "telemetrytasks", Group = GPrivacy, Icon = "CalendarCancel24", Title = "Desactivar tareas programadas de telemetría",
            Description = "Apaga las tareas que recopilan datos de compatibilidad, del programa de mejora (CEIP) y de diagnóstico de disco. Se guarda el estado original de cada una.",
            Check = () => ScheduledTasks.TelemetryTasks.Where(ScheduledTasks.Exists).All(p => !ScheduledTasks.IsEnabled(p)),
            CustomApply = () => { foreach (var p in ScheduledTasks.TelemetryTasks.Where(ScheduledTasks.Exists)) ScheduledTasks.SetEnabled(p, false); },
            CustomRevert = () => { foreach (var p in ScheduledTasks.TelemetryTasks) ScheduledTasks.RestoreOriginal(p); },
        },
        new()
        {
            Id = "officetel", Group = GPrivacy, Icon = "DocumentOnePage24", Title = "Telemetría de Office",
            Description = "Office 2016 o posterior (y Microsoft 365) deja de enviar datos de uso. Office sigue funcionando y actualizándose igual.",
            Ops = [new(@"HKCU\Software\Policies\Microsoft\Office\16.0\Common\ClientTelemetry", "DisableTelemetry", 1, null),
                   new(@"HKCU\Software\Policies\Microsoft\Office\16.0\Common\ClientTelemetry", "SendTelemetry", 3, null),
                   new(@"HKCU\Software\Policies\Microsoft\Office\16.0\osm", "Enablelogging", 0, null),
                   new(@"HKCU\Software\Policies\Microsoft\Office\16.0\osm", "EnableUpload", 0, null)],
        },
        new()
        {
            Id = "edgetel", Group = GPrivacy, Icon = "Globe24", Title = "Edge sin barra lateral, Copilot, compras ni telemetría",
            Description = "Quita la barra lateral (Copilot/Discover), el asistente de compras, las recomendaciones y el envío de datos de diagnóstico. Edge mostrará \"administrado por tu organización\".",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0, null)],
        },
        new()
        {
            Id = "chrometel", Group = GPrivacy, Icon = "Globe24", Title = "Telemetría de Google Chrome",
            Description = "Chrome deja de enviar estadísticas de uso e informes. Chrome mostrará \"gestionado por tu organización\": es normal, se quita al deshacer.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Google\Chrome", "MetricsReportingEnabled", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Google\Chrome", "UserFeedbackAllowed", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Google\Chrome", "ChromeCleanupReportingEnabled", 0, null)],
        },
        new()
        {
            Id = "firefoxtel", Group = GPrivacy, Icon = "Globe24", Title = "Telemetría de Firefox",
            Description = "Firefox deja de enviar telemetría y apaga su \"agente de navegador predeterminado\". Mostrará \"gestionado por tu organización\".",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Mozilla\Firefox", "DisableTelemetry", 1, null),
                   new(@"HKLM\SOFTWARE\Policies\Mozilla\Firefox", "DisableDefaultBrowserAgent", 1, null)],
        },
        new()
        {
            Id = "nvidiatel", Group = GPrivacy, Icon = "DeveloperBoard24", Title = "Telemetría de NVIDIA",
            Description = "El panel y el driver de NVIDIA dejan de enviar datos de uso. Los drivers, G-Sync, DLSS y la app de NVIDIA siguen funcionando.",
            Ops = [new(@"HKLM\SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", 0, null),
                   new(@"HKLM\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID44231", 0, null),
                   new(@"HKLM\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID64640", 0, null),
                   new(@"HKLM\SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID66610", 0, null)],
        },
        new()
        {
            Id = "vstel", Group = GPrivacy, Icon = "Code24", Title = "Telemetría de Visual Studio",
            Description = "Visual Studio deja de participar en el programa de mejora y de pedir capturas en los comentarios.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\VisualStudio\SQM", "OptIn", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\VisualStudio\Feedback", "DisableFeedbackDialog", 1, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\VisualStudio\Feedback", "DisableScreenshotCapture", 1, null)],
        },
        new()
        {
            Id = "cloudclip", Group = GPrivacy, Icon = "ClipboardPaste24", Title = "No sincronizar el portapapeles en la nube",
            Description = "Lo que copias no se sube a tu cuenta de Microsoft. El historial del portapapeles (Win+V) sigue funcionando en este PC.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard", 0, null)],
        },
        new()
        {
            Id = "cortana", Group = GPrivacy, Icon = "Mic24", Title = "Desactivar Cortana y búsqueda en la nube",
            Description = "La búsqueda de Windows no consulta OneDrive ni Outlook en la nube; tus archivos locales se siguen encontrando.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, null),
                   new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0, null)],
        },
        new()
        {
            Id = "errreport", Group = GPrivacy, Icon = "Bug24", Title = "No enviar informes de errores",
            Description = "Cuando una app se cuelga, Windows no envía el informe a Microsoft. Los registros siguen guardándose en tu PC (Visor de eventos).",
            Ops = [new(@"HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, null)],
        },

        // ---------- Avanzado ----------
        new()
        {
            Id = "telemetry", Group = GAdvanced, Risk = Risk.Advanced, Icon = "EyeOff24", Title = "Reducir telemetría al mínimo",
            Description = "Desactiva el servicio de seguimiento (DiagTrack) y fija la telemetría al nivel mínimo. Windows Update y Defender siguen funcionando.", Restart = "Reinicio",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null),
                   new(Svc + "DiagTrack", "Start", 4, 2),
                   new(Svc + "dmwappushservice", "Start", 4, 3)],
        },
        new()
        {
            Id = "copilot", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Bot24", Title = "Desactivar Copilot",
            Description = "Quita Copilot de la barra de tareas y desactiva su atajo.", Restart = "Cerrar sesión",
            Ops = [new(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null)],
        },
        new()
        {
            Id = "recall", Group = GAdvanced, Risk = Risk.Advanced, Icon = "EyeOff24", Title = "Desactivar Recall",
            Description = "Impide que Windows haga capturas periódicas de tu pantalla para la función Recall.",
            Ops = [new(@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null)],
        },
        new()
        {
            Id = "widgets", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Apps24", Title = "Desactivar Widgets",
            Description = "Quita el panel de noticias y tiempo y sus procesos en segundo plano (ahorra unos 100-300 MB de RAM).", Restart = "Reinicio",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)],
        },
        new()
        {
            Id = "gamebaroff", Group = GAdvanced, Risk = Risk.Advanced, Icon = "GameChat20", Title = "Desactivar capturas de Xbox Game Bar",
            Description = "Bloquea por completo la grabación y las capturas de Game Bar (Win+Alt+R). El modo de juego y el mando siguen funcionando.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null)],
        },
        new()
        {
            Id = "sysmain", Group = GAdvanced, Risk = Risk.Advanced, Icon = "DataArea24", Title = "Desactivar SysMain (Superfetch)", Restart = "Reinicio",
            Description = "Precarga en RAM las apps que usas. Con SSD y mucha RAM aporta poco y genera actividad de disco; algunas apps pueden tardar algo más la primera vez.",
            Ops = [new(Svc + "SysMain", "Start", 4, 2)],
        },
        new()
        {
            Id = "wsearch", Group = GAdvanced, Risk = Risk.Advanced, Icon = "SearchInfo24", Title = "Desactivar la indexación de búsqueda", Restart = "Reinicio",
            Description = "Menos actividad de disco, pero buscar archivos será más lento y Outlook no podrá buscar correos. Las apps y ajustes se siguen encontrando.",
            Ops = [new(Svc + "WSearch", "Start", 4, 2)],
        },
        new()
        {
            Id = "spooler", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Print24", Title = "Desactivar la cola de impresión", Restart = "Reinicio",
            Description = "Solo si NO usas impresora ni \"Imprimir a PDF\". Cierra una vía de ataque conocida (PrintNightmare).",
            Ops = [new(Svc + "Spooler", "Start", 4, 2)],
        },
        new()
        {
            Id = "xboxlive", Group = GAdvanced, Risk = Risk.Advanced, Icon = "XboxController24", Title = "Desactivar servicios de Xbox Live", Restart = "Reinicio",
            Description = "Libera 3 servicios. OJO: si juegas a Game Pass o juegos de la Xbox app/Microsoft Store, dejarán de iniciar sesión y de guardar en la nube. Steam/Epic no se ven afectados. Los mandos siguen funcionando.",
            Ops = [new(Svc + "XblAuthManager", "Start", 4, 3),
                   new(Svc + "XblGameSave", "Start", 4, 3),
                   new(Svc + "XboxNetApiSvc", "Start", 4, 3)],
        },
        new()
        {
            Id = "fax", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Print24", Title = "Desactivar servicio de Fax",
            Description = "Nadie usa fax en un PC doméstico. Se puede volver a activar sin problema.", Restart = "Reinicio",
            Ops = [new(Svc + "Fax", "Start", 4, 3)],
        },
        new()
        {
            Id = "wmpshare", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Video24", Title = "Desactivar uso compartido del Reproductor de Windows Media",
            Description = "Servicio que comparte tu biblioteca multimedia con otros dispositivos de la red (DLNA).", Restart = "Reinicio",
            Ops = [new(Svc + "WMPNetworkSvc", "Start", 4, 3)],
        },
        new()
        {
            Id = "insider", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Beaker24", Title = "Desactivar servicio del programa Insider",
            Description = "Solo lo usa quien prueba versiones beta de Windows.", Restart = "Reinicio",
            Ops = [new(Svc + "wisvc", "Start", 4, 3)],
        },
        new()
        {
            Id = "sensors", Group = GAdvanced, Risk = Risk.Advanced, Icon = "Gauge24", Title = "Desactivar servicio de sensores",
            Description = "En un sobremesa no hace nada. En portátiles y tablets quita el brillo automático y la rotación de pantalla.", Restart = "Reinicio",
            Ops = [new(Svc + "SensorService", "Start", 4, 3)],
        },
        new()
        {
            Id = "compat", Group = GAdvanced, Risk = Risk.Advanced, Icon = "AppGeneric24", Title = "Desactivar el Asistente de compatibilidad",
            Description = "Windows deja de vigilar las apps que instalas para avisarte de problemas de compatibilidad (\"Puede que este programa no se haya instalado correctamente\").",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisablePCA", 1, null)],
        },
        new()
        {
            Id = "notifcenter", Group = GAdvanced, Risk = Risk.Advanced, Icon = "AlertOff24", Title = "Desactivar el centro de notificaciones",
            Description = "Quita el panel de notificaciones y el calendario de la barra de tareas. Las notificaciones emergentes de apps dejan de guardarse.", Restart = "Cerrar sesión",
            Ops = [new(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1, null)],
        },
        new()
        {
            Id = "onedrive", Group = GAdvanced, Risk = Risk.Advanced, Icon = "CloudOff24", Title = "Desactivar OneDrive",
            Description = "OneDrive deja de sincronizar y de arrancar. Tus archivos en la nube NO se borran (siguen en onedrive.com), pero los cambios dejan de subirse. Para quitarlo del todo, usa el desinstalador.",
            Ops = [new(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1, null)],
        },
        new()
        {
            Id = "hibernate", Group = GAdvanced, Risk = Risk.Advanced, Icon = "WeatherMoon24", Title = "Desactivar hibernación",
            Description = "Borra hiberfil.sys y recupera varios GB en C:. También desactiva el \"inicio rápido\" (el arranque tarda unos segundos más).",
            Check = () => Equals(Read(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"), 0),
            CustomApply = () => Store.Run("powercfg.exe", "/hibernate off"),
            CustomRevert = () => Store.Run("powercfg.exe", "/hibernate on"),
        },
    ];

    /// <summary>Writing Services\X\Start for a service that isn't installed would create a broken service entry: such ops are skipped.</summary>
    static IEnumerable<RegOp> Live(Tweak t) => t.Ops.Where(op => !op.Key.StartsWith(Svc, StringComparison.OrdinalIgnoreCase) || KeyExists(op.Key));

    /// <summary>False when the tweak only touches services this PC doesn't have.</summary>
    public static bool Available(Tweak t) => t.Ops.Length == 0 || Live(t).Any();

    public static bool IsApplied(Tweak t) =>
        t.Check?.Invoke() ?? (Available(t) && Live(t).All(op => Equals(Read(op.Key, op.Name), op.Value)));

    public static void Apply(Tweak t)
    {
        var ops = Live(t).ToList();
        var backup = LoadBackup();
        // custom tweaks get an empty entry too: it records that Tidy (not another tool) turned it on
        if (!backup.ContainsKey(t.Id))
        {
            backup[t.Id] = ops.Select(Capture).ToList();
            Store.WriteJson(BackupFile, backup); // backup hits disk before the first write
        }
        try
        {
            t.CustomApply?.Invoke();
            foreach (var op in ops) Write(op.Key, op.Name, op.Value);
        }
        catch (Exception e)
        {
            // never leave a tweak half-applied
            Store.Log($"Optimización FALLÓ, se deshace: {t.Title} [{t.Id}]: {e.Message}");
            try { Revert(t, quiet: true); } catch { }
            throw new InvalidOperationException($"{t.Title}: {e.Message}", e);
        }
        Store.Log($"Optimización APLICADA: {t.Title} [{t.Id}]");
        History.Add(HistoryKind.Tweak, $"Activada: {t.Title}", t.Group, $"tweak:{t.Id}");
    }

    /// <summary>Exact registry values a tweak writes, for the "¿Qué cambia exactamente?" view.</summary>
    public static string Details(Tweak t)
    {
        if (t.Ops.Length == 0) return t.Id switch
        {
            "hibernate" => "Ejecuta: powercfg /hibernate off   (deshacer: powercfg /hibernate on)",
            "classicmenu" => $"Crea la clave HKCU\\{ClassicMenu}\\InprocServer32 (deshacer: se borra esa clave)",
            "telemetrytasks" => "Desactiva estas tareas programadas (deshacer: vuelven a su estado original):\n" + string.Join("\n", ScheduledTasks.TelemetryTasks),
            _ => "",
        };
        return string.Join("\n", t.Ops.Select(o =>
            $"{o.Key}\\{(o.Name == "" ? "(predeterminado)" : o.Name)} = {Show(o.Value)}   (Windows: {(o.Default == null ? "no existe" : Show(o.Default))})"));
        static string Show(object v) => v is int i ? $"{i} (0x{i:X})" : $"\"{v}\"";
    }

    // ---------- presets ----------
    public record Preset(string Id, string Name, string Description, string Icon, bool UltimatePlan, Func<Tweak, bool> Includes);

    static readonly HashSet<string> PrivacyExtra = ["telemetry", "copilot", "recall", "telemetrytasks", "officetel", "edgetel", "nvidiatel", "vstel", "cloudclip", "cortana", "errreport", "activity", "apptrack"];
    static readonly HashSet<string> GamingExtra = ["gamemode", "hags", "gamedvr", "mmcss", "netthrottle", "powerthrottle", "stickykeys"];
    static readonly HashSet<string> MaxExtra = ["animations", "transparency", "snapassist", "sysmain", "widgets", "lastaccess", "fax", "wmpshare", "insider"];

    public static readonly Preset[] Presets =
    [
        new("basic", "Recomendado", "Lo que mejora el PC para casi cualquiera, sin efectos secundarios: menos anuncios, menos espera, más seguridad.", "ShieldCheckmark24", false,
            t => t.Recommended),
        new("gaming", "Gaming", "Recomendado + todo lo que da prioridad al juego: GPU, planificador, red, energía. Activa el plan Máximo rendimiento.", "Games24", true,
            t => t.Recommended || GamingExtra.Contains(t.Id)),
        new("privacy", "Privacidad máxima", "Recomendado + corta la telemetría de Windows, Office, Edge, NVIDIA y Visual Studio, Copilot y Recall.", "EyeOff24", false,
            t => t.Recommended || PrivacyExtra.Contains(t.Id)),
        new("max", "Rendimiento máximo", "Gaming + sin animaciones ni transparencias, sin SysMain, Widgets ni servicios que no usa un sobremesa. Para exprimir el PC.", "TopSpeed24", true,
            t => t.Recommended || GamingExtra.Contains(t.Id) || MaxExtra.Contains(t.Id)),
    ];

    public static List<Tweak> Pending(Preset p) => All.Where(t => p.Includes(t) && Available(t) && !IsApplied(t)).ToList();

    /// <summary>Everything Tidy changed and still has a backup for.</summary>
    public static List<Tweak> Changed() { var b = LoadBackup(); return All.Where(t => b.ContainsKey(t.Id)).ToList(); }

    // ---------- export / import ----------
    public class Profile
    {
        public int Version { get; set; } = 1;
        public DateTime Created { get; set; } = DateTime.Now;
        public List<string> Tweaks { get; set; } = [];
    }

    public static Profile Export() => new() { Tweaks = All.Where(IsApplied).Select(t => t.Id).ToList() };

    /// <summary>Only ids from our own catalog are honoured: a profile file can't smuggle in arbitrary registry writes.</summary>
    public static List<Tweak> FromProfile(Profile p) => All.Where(t => p.Tweaks.Contains(t.Id) && Available(t) && !IsApplied(t)).ToList();

    /// <summary>Shown in the app so it's clear what's missing on purpose compared to other optimizers.</summary>
    public static readonly (string What, string Why)[] NotIncluded =
    [
        ("Desactivar Windows Defender", "Te deja sin antivirus. Si usas otro antivirus, Windows ya desactiva Defender solo."),
        ("Desactivar SmartScreen", "Es lo que bloquea descargas y webs de phishing conocidas."),
        ("Desactivar las actualizaciones automáticas", "Los parches de seguridad son la principal defensa contra virus. Tidy solo evita los reinicios inesperados."),
        ("Desactivar VBS / Integridad de memoria", "Gana 1-5% de FPS a cambio de abrir la puerta a drivers maliciosos."),
        ("Desactivar HPET", "Mito: en CPUs modernas no mejora nada y puede causar tirones."),
        ("Desactivar Restaurar sistema", "Es tu red de seguridad. Tidy la usa antes de cada cambio."),
        ("Saltarse TPM / Secure Boot", "Solo sirve para instalar Windows 11 en equipos no compatibles; el tuyo ya lo es."),
        ("Excluir drivers de Windows Update", "La pestaña Drivers de Tidy usa Windows Update para encontrarlos."),
    ];

    public static void Revert(Tweak t, bool quiet = false)
    {
        var backup = LoadBackup();
        if (t.CustomRevert != null) t.CustomRevert();
        if (backup.TryGetValue(t.Id, out var saved))
        {
            foreach (var s in saved)
            {
                // the backup lives in a user-writable folder: only ever restore the exact values this tweak owns
                if (t.Ops.Any(o => o.Key == s.Key && o.Name == s.Name)) Restore(s);
                else Store.Log($"Optimización: BLOQUEADO valor ajeno en la copia de seguridad: {s.Key}\\{s.Name}");
            }
            backup.Remove(t.Id);
            Store.WriteJson(BackupFile, backup);
        }
        else
        {
            foreach (var op in Live(t))
                if (op.Default == null) Delete(op.Key, op.Name); else Write(op.Key, op.Name, op.Default);
        }
        Store.Log($"Optimización DESHECHA: {t.Title} [{t.Id}]");
        if (!quiet) History.Add(HistoryKind.Tweak, $"Deshecha: {t.Title}", "Valor original restaurado");
    }

    static Dictionary<string, List<SavedValue>> LoadBackup() =>
        Store.ReadJson<Dictionary<string, List<SavedValue>>>(BackupFile) ?? [];

    // ---------- registry helpers ----------
    static RegistryKey Hive(string key, out string sub)
    {
        var i = key.IndexOf('\\');
        sub = key[(i + 1)..];
        return key[..i] switch
        {
            "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            _ => throw new ArgumentException($"Hive no permitido: {key}"),
        };
    }

    static bool KeyExists(string key)
    {
        using var hive = Hive(key, out var sub);
        using var k = hive.OpenSubKey(sub);
        return k != null;
    }

    public static object? Read(string key, string name)
    {
        using var hive = Hive(key, out var sub);
        using var k = hive.OpenSubKey(sub);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    static void Write(string key, string name, object value)
    {
        using var hive = Hive(key, out var sub);
        using var k = hive.CreateSubKey(sub, true);
        k.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    static void Delete(string key, string name)
    {
        using var hive = Hive(key, out var sub);
        using var k = hive.OpenSubKey(sub, true);
        k?.DeleteValue(name, false);
    }

    static SavedValue Capture(RegOp op) => Read(op.Key, op.Name) switch
    {
        int i => new SavedValue { Key = op.Key, Name = op.Name, Kind = "dword", Value = i.ToString() },
        string s => new SavedValue { Key = op.Key, Name = op.Name, Kind = "sz", Value = s },
        _ => new SavedValue { Key = op.Key, Name = op.Name },
    };

    static void Restore(SavedValue s)
    {
        switch (s.Kind)
        {
            case "dword": Write(s.Key, s.Name, int.Parse(s.Value!)); break;
            case "sz": Write(s.Key, s.Name, s.Value!); break;
            default: Delete(s.Key, s.Name); break;
        }
    }

    // ---------- bloatware ----------
    public record BloatApp(string Package, string Name, string Description);

    /// <summary>Allowlist: only these can ever be removed. Store, Xbox, Photos, Calculator, Terminal etc. are never offered.</summary>
    public static readonly BloatApp[] Bloat =
    [
        new("Microsoft.BingNews", "Noticias", "App de noticias de Microsoft Start."),
        new("Microsoft.BingWeather", "El Tiempo", "App del tiempo de MSN."),
        new("Microsoft.BingSearch", "Búsqueda de Bing", "Integración de Bing en el sistema."),
        new("Microsoft.GetHelp", "Obtener ayuda", "Asistente de soporte de Microsoft."),
        new("Microsoft.Getstarted", "Recomendaciones", "Consejos y tutoriales de Windows."),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Juegos de cartas con anuncios."),
        new("Microsoft.People", "Contactos", "Agenda de contactos antigua."),
        new("Microsoft.Todos", "Microsoft To Do", "Lista de tareas."),
        new("Microsoft.WindowsFeedbackHub", "Centro de opiniones", "Enviar comentarios a Microsoft."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "Lanzador/anuncio de Office. No desinstala Office."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", "Automatización para empresas."),
        new("Clipchamp.Clipchamp", "Clipchamp", "Editor de vídeo online."),
        new("Microsoft.549981C3F5F10", "Cortana", "Asistente antiguo, ya sin uso."),
        new("Microsoft.WindowsMaps", "Mapas", "Mapas de Windows."),
        new("Microsoft.ZuneVideo", "Películas y TV", "Reproductor y tienda de vídeo antigua."),
        new("Microsoft.Copilot", "Copilot", "App de Copilot."),
        new("Microsoft.OutlookForWindows", "Outlook (nuevo)", "Cliente de correo. Si lo usas, no lo quites."),
        new("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", "Control parental."),
        new("MicrosoftTeams", "Microsoft Teams (personal)", "Chat personal de Teams. Si lo usas, no lo quites."),
        new("MSTeams", "Microsoft Teams", "Si lo usas para trabajo/clase, no lo quites."),
        new("Microsoft.YourPhone", "Enlace móvil", "Conectar el móvil al PC. Si lo usas, no lo quites."),
        new("Microsoft.Windows.DevHome", "Dev Home", "Panel para desarrolladores."),
        new("Microsoft.MixedReality.Portal", "Portal de realidad mixta", "Solo para visores Windows MR."),
        new("Microsoft.SkypeApp", "Skype", "Skype ya no existe."),
    ];

    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    static readonly Regex SafeName = new(@"^[A-Za-z0-9._~-]+$");

    public static List<(BloatApp App, string FullName)> InstalledBloat()
    {
        var (_, json) = Store.Run(PowerShell,
            "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object Name,PackageFullName | ConvertTo-Json -Compress\"");
        var start = json.IndexOf('[');
        if (start < 0) return [];
        var installed = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json[start..]) ?? [];
        return Bloat.SelectMany(b => installed.Where(i => i.GetValueOrDefault("Name") == b.Package)
                                              .Select(i => (b, i["PackageFullName"]))).ToList();
    }

    public static bool RemoveApp(BloatApp app, string fullName)
    {
        if (!SafeName.IsMatch(fullName) || !fullName.StartsWith(app.Package + "_")) return false;
        var (code, output) = Store.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{fullName}'\"");
        Store.Log($"Bloatware: desinstalar {fullName} -> {(code == 0 ? "OK" : output.Trim())}");
        return code == 0;
    }
}

/// <summary>System Restore point before any change. Windows only allows one every 24 h, so we reuse a recent one.</summary>
public static class SafetyNet
{
    static bool _doneThisSession;

    public record Result(bool Ok, string Message);

    public static Result EnsureRestorePoint(string description)
    {
        if (_doneThisSession) return new(true, "Punto de restauración ya creado en esta sesión.");
        try
        {
            var latest = Latest();
            if (latest is { } when && DateTime.Now - when < TimeSpan.FromHours(24))
            {
                _doneThisSession = true;
                return new(true, $"Se usa el punto de restauración del {when:dd/MM HH:mm} (Windows solo permite uno cada 24 h).");
            }
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var args = cls.GetMethodParameters("CreateRestorePoint");
            args["Description"] = $"Tidy: {description}";
            args["RestorePointType"] = 12; // MODIFY_SETTINGS
            args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var result = cls.InvokeMethod("CreateRestorePoint", args, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);
            Store.Log($"Punto de restauración '{description}' -> {code}");
            if (code != 0) return new(false, $"Windows no pudo crear el punto de restauración (código {code}). ¿Está desactivada la Protección del sistema en C:?");
            _doneThisSession = true;
            return new(true, "Punto de restauración creado.");
        }
        catch (Exception e)
        {
            Store.Log($"Punto de restauración falló: {e.Message}");
            return new(false, $"No se pudo crear el punto de restauración: {e.Message}");
        }
    }

    public static DateTime? Latest()
    {
        using var s = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
        return s.Get().Cast<ManagementObject>()
            .Select(o => (DateTime?)ManagementDateTimeConverter.ToDateTime((string)o["CreationTime"]))
            .Max();
    }

    public static bool EnableProtection()
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var args = cls.GetMethodParameters("Enable");
            args["Drive"] = Path.GetPathRoot(Environment.SystemDirectory);
            using var r = cls.InvokeMethod("Enable", args, null);
            var ok = Convert.ToUInt32(r["ReturnValue"]) == 0;
            Store.Log($"Protección del sistema activada en {args["Drive"]}: {ok}");
            return ok;
        }
        catch (Exception e) { Store.Log($"Activar protección falló: {e.Message}"); return false; }
    }
}
