using System.IO;
using System.Text.RegularExpressions;

namespace Tidy.Core;

public record WingetPackage(string Name, string Id, string Version, string Available, string Source);

public record CatalogApp(string Id, string Name, string Category, string Description, string Source = "winget");

/// <summary>
/// Install and update apps through winget, Microsoft's package manager. winget downloads each installer from the
/// publisher's own URL in its manifest and refuses to run it if the SHA-256 doesn't match; Tidy only passes package ids.
/// </summary>
public static class Winget
{
    /// <summary>The App Installer execution alias. Absolute path, no PATH lookup.</summary>
    public static string ExePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
    public static bool Available => File.Exists(ExePath);
    const string Common = "--accept-source-agreements --disable-interactivity";
    static readonly Regex SafeId = new(@"^[\w.+-]{2,128}$");
    static readonly Regex SafeQuery = new(@"^[\w .+-]{2,60}$");
    // winget exit codes: APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION, _ADMIN_CONTEXT_ACTION_PROHIBITED
    const int ProhibitsElevation = unchecked((int)0x8A150056), UserScopeFromAdmin = unchecked((int)0x8A15007D);

    public static readonly CatalogApp[] Catalog =
    [
        new("Mozilla.Firefox", "Firefox", "Navegadores", "Navegador libre, centrado en la privacidad."),
        new("Google.Chrome", "Google Chrome", "Navegadores", "El navegador más usado."),
        new("Brave.Brave", "Brave", "Navegadores", "Basado en Chrome, bloquea anuncios de serie."),
        new("Opera.OperaGX", "Opera GX", "Navegadores", "Navegador para gamers con límites de RAM y CPU."),

        new("Valve.Steam", "Steam", "Juegos", "La tienda de juegos de PC más grande."),
        new("EpicGames.EpicGamesLauncher", "Epic Games", "Juegos", "Tienda de Epic, con juegos gratis cada semana."),
        new("GOG.Galaxy", "GOG Galaxy", "Juegos", "Juegos sin DRM y une todas tus bibliotecas."),
        new("ElectronicArts.EADesktop", "EA app", "Juegos", "Juegos de EA (FC, Battlefield, Los Sims…)."),
        new("Ubisoft.Connect", "Ubisoft Connect", "Juegos", "Juegos de Ubisoft (Assassin's Creed, Far Cry…)."),
        new("Blizzard.BattleNet", "Battle.net", "Juegos", "Juegos de Blizzard y Call of Duty."),
        new("Playnite.Playnite", "Playnite", "Juegos", "Una sola biblioteca para todos tus juegos."),

        new("Discord.Discord", "Discord", "Comunicación", "Chat de voz y texto para jugar con amigos."),
        new("Telegram.TelegramDesktop", "Telegram", "Comunicación", "Mensajería rápida con grupos y canales."),
        new("9NKSQGP7F2NH", "WhatsApp", "Comunicación", "WhatsApp para escritorio (Microsoft Store).", "msstore"),
        new("OpenWhisperSystems.Signal", "Signal", "Comunicación", "Mensajería cifrada de extremo a extremo."),
        new("Zoom.Zoom", "Zoom", "Comunicación", "Videollamadas y reuniones."),

        new("VideoLAN.VLC", "VLC", "Multimedia", "Reproduce cualquier vídeo o audio."),
        new("OBSProject.OBSStudio", "OBS Studio", "Multimedia", "Graba y emite tu pantalla o tus partidas."),
        new("Audacity.Audacity", "Audacity", "Multimedia", "Editor de audio sencillo."),
        new("GIMP.GIMP.3", "GIMP", "Multimedia", "Editor de imágenes gratuito."),
        new("HandBrake.HandBrake", "HandBrake", "Multimedia", "Convierte y comprime vídeos."),

        new("7zip.7zip", "7-Zip", "Utilidades", "Abre y crea .zip, .rar, .7z…"),
        new("Notepad++.Notepad++", "Notepad++", "Utilidades", "Bloc de notas avanzado."),
        new("Microsoft.PowerToys", "PowerToys", "Utilidades", "Utilidades oficiales de Microsoft para Windows."),
        new("voidtools.Everything", "Everything", "Utilidades", "Encuentra cualquier archivo al instante."),
        new("ShareX.ShareX", "ShareX", "Utilidades", "Capturas y grabaciones de pantalla."),
        new("qBittorrent.qBittorrent", "qBittorrent", "Utilidades", "Cliente torrent libre y sin anuncios."),
        new("Bitwarden.Bitwarden", "Bitwarden", "Utilidades", "Gestor de contraseñas."),
        new("Malwarebytes.Malwarebytes", "Malwarebytes", "Utilidades", "Segunda opinión antimalware (compatible con Defender)."),

        new("REALiX.HWiNFO", "HWiNFO", "Hardware", "Temperaturas, voltajes y sensores de todo."),
        new("CPUID.CPU-Z", "CPU-Z", "Hardware", "Detalles del procesador, placa y RAM."),
        new("TechPowerUp.GPU-Z", "GPU-Z", "Hardware", "Detalles de la tarjeta gráfica."),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Hardware", "Salud detallada de los discos (SMART)."),
        new("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", "Hardware", "Mide la velocidad de los discos."),
        new("Guru3D.Afterburner", "MSI Afterburner", "Hardware", "Monitor en pantalla y control de la gráfica."),

        new("Microsoft.VisualStudioCode", "VS Code", "Desarrollo", "Editor de código."),
        new("Git.Git", "Git", "Desarrollo", "Control de versiones."),
        new("Python.Python.3.13", "Python 3.13", "Desarrollo", "Lenguaje de programación."),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "Desarrollo", "JavaScript fuera del navegador."),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Desarrollo", "Terminal moderna de Microsoft."),
    ];

    static string Run(string args, int timeoutMs = 120_000)
    {
        if (!Available) return "";
        return Store.Run(ExePath, args, timeoutMs).Output;
    }

    public static List<WingetPackage> Upgrades() => Table(Run($"upgrade {Common}"));

    public static List<WingetPackage> Installed() => Table(Run($"list {Common}", 180_000));

    public static List<WingetPackage> Search(string query) =>
        SafeQuery.IsMatch(query) ? Table(Run($"search \"{query}\" --count 40 {Common}")).Select(p => p with { Available = "" }).ToList() : [];

    /// <summary>Packages published once per version (Python.Python.3.13, 3.14, one day 4.0…): the catalog shows the
    /// newest one winget has, so a new Python appears without a Tidy update. Null if winget doesn't answer.</summary>
    public static CatalogApp? Newest(CatalogApp app)
    {
        var own = VersionedId.Match(app.Id);
        if (!own.Success) return null;
        var ids = Table(Run($"search --id {own.Groups[1].Value} --source winget {Common}")).Select(p => p.Id);
        return NewestOf(app, ids);
    }

    // "Python.Python." + "3.13": the package family, then its version (whole, so 3.14 < 4.0 < 4.1)
    static readonly Regex VersionedId = new(@"^(.*?[A-Za-z][\w+-]*\.)(\d{1,4}(?:\.\d{1,4}){0,2})$");

    public static CatalogApp? NewestOf(CatalogApp app, IEnumerable<string> ids)
    {
        var own = VersionedId.Match(app.Id);
        if (!own.Success) return null;
        static Version V(string s) => Version.Parse(s.Contains('.') ? s : s + ".0"); // "4" -> 4.0
        var family = own.Groups[1].Value;
        var best = ids.Select(id => VersionedId.Match(id)).Where(m => m.Success && m.Groups[1].Value == family).MaxBy(m => V(m.Groups[2].Value));
        if (best == null || V(best.Groups[2].Value) <= V(own.Groups[2].Value)) return null; // never an older one
        // "Python 3.13" -> "Python 4.0": same name, the new version
        return app with { Id = best.Value, Name = Regex.Replace(app.Name, @"\d+(\.\d+)*$", best.Groups[2].Value) };
    }

    public static (bool Ok, string Message) Upgrade(string id) => Act("upgrade", id, null);

    public static (bool Ok, string Message) Install(string id, string source) => Act("install", id, source);

    public static (bool Ok, string Message) Uninstall(string id) => Act("uninstall", id, null);

    static (bool Ok, string Message) Act(string verb, string id, string? source)
    {
        if (!SafeId.IsMatch(id)) return (false, "Identificador no válido.");
        if (source is not (null or "winget" or "msstore")) return (false, "Origen no válido.");
        var args = $"{verb} --id \"{id}\" --exact {(source != null ? $"--source {source} " : "")}--silent {(verb == "uninstall" ? "" : "--accept-package-agreements ")}{Common}";
        var (code, output) = Store.Run(ExePath, args, 20 * 60_000);
        // some installers refuse to run as administrator (Spicetify…), and winget won't touch per-user apps from an admin
        // context: the same command again as the normal user
        if (code is ProhibitsElevation or UserScopeFromAdmin)
        {
            Store.Log($"winget {verb} {id} -> {code}, sin administrador");
            (code, output) = Store.RunUnelevated(ExePath, args, 20 * 60_000);
        }
        var lines = Clean(output);
        Store.Log($"winget {verb} {id} -> {code} {lines.LastOrDefault()}");
        return (code == 0, code == 0 ? "" : lines.LastOrDefault() ?? $"código {code}");
    }

    /// <summary>Text lines without progress bars and spinners.</summary>
    static List<string> Clean(string output) => output.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
        .Select(l => l.Trim()).Where(l => l.Length > 1 && !l.Any(c => c is '█' or '▒') && !Regex.IsMatch(l, @"^[-\\|/ ]+$")).ToList();

    /// <summary>
    /// Parses winget's fixed-width table: the header line above the "-----" line gives the column starts.
    /// Columns are Name, Id, Version, [Available|Match], Source. Stops at the first line that isn't a row (footer).
    /// </summary>
    public static List<WingetPackage> Table(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();
        var sep = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
        if (sep < 1) return [];
        var header = lines[sep - 1];
        var starts = new List<int> { 0 };
        for (var i = 1; i < header.Length; i++) if (header[i] != ' ' && header[i - 1] == ' ') starts.Add(i);
        if (starts.Count < 3) return [];

        var result = new List<WingetPackage>();
        foreach (var l in lines.Skip(sep + 1))
        {
            if (l.Trim().Length == 0) break;
            if (l.Length <= starts[2]) continue;
            string Col(int i) => i >= starts.Count || starts[i] >= l.Length ? "" : l[starts[i]..(i + 1 < starts.Count ? Math.Min(starts[i + 1], l.Length) : l.Length)].Trim();
            var id = Col(1);
            if (!SafeId.IsMatch(id)) continue; // footer text, or an ARP\… entry winget can't manage
            result.Add(new WingetPackage(Col(0), id, Col(2), starts.Count >= 5 ? Col(3) : "", starts.Count >= 4 ? Col(starts.Count - 1) : ""));
        }
        return result;
    }
}
