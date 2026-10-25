// Safety checks for Tidy's core. Everything destructive runs inside a throwaway sandbox folder and a
// throwaway HKCU\Software\TidyTest key. Usage: Tidy.Tests <sandboxDir> [--wu]
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tidy.Core;

var sandbox = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "tidy-tests"));
if (Directory.Exists(sandbox)) Cleaner.DeleteTree(sandbox);
Directory.CreateDirectory(sandbox);
Store.Dir = Path.Combine(sandbox, "store");
Store.SecureDir = Path.Combine(sandbox, "secure"); // first thing: no test may touch the real Program Files\Tidy\data
int failed = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failed++; }
// --unelevated: only the run-without-admin checks (meant to be started as administrator)
if (args.Contains("--unelevated")) { UnelevatedChecks(); Cleaner.DeleteTree(sandbox); Console.WriteLine(failed == 0 ? "TODO OK" : $"{failed} FALLOS"); return failed; }

// ---- NVIDIA version parsing ----
Check(Drivers.NvidiaVersion("32.0.16.1742") == "617.42", "NVIDIA 32.0.16.1742 -> 617.42");
Check(Drivers.NvidiaVersion("32.0.15.7652") == "576.52", "NVIDIA 32.0.15.7652 -> 576.52");

// ---- Recycle bin $I parsing (v2) ----
var orig = @"C:\Users\x\Documents\informe.docx";
var info = new List<byte>();
info.AddRange(BitConverter.GetBytes(2L)); info.AddRange(BitConverter.GetBytes(12345L)); info.AddRange(BitConverter.GetBytes(0L));
info.AddRange(BitConverter.GetBytes(orig.Length + 1)); info.AddRange(Encoding.Unicode.GetBytes(orig + "\0"));
Check(Cleaner.ParseRecycleInfo(info.ToArray()) == (12345L, orig), "papelera: lee tamaño y ruta original de $I");

// ---- Allowlist ----
Check(Cleaner.IsUnder(@"C:\Temp\a.txt", [@"C:\Temp"]), "lista blanca: archivo dentro de la raíz");
Check(!Cleaner.IsUnder(@"C:\Temp2\a.txt", [@"C:\Temp"]), "lista blanca: carpeta hermana con prefijo parecido NO");
Check(!Cleaner.IsUnder(@"C:\Temp\..\Windows\win.ini", [@"C:\Temp"]), "lista blanca: escapar con .. NO");
Check(!Cleaner.IsUnder(@"C:\Temp", [@"C:\Temp\"]), "lista blanca: la raíz misma NO");

// ---- Cleaner: age filter, junctions, out-of-root ----
var root = Path.Combine(sandbox, "cache");
var outside = Path.Combine(sandbox, "precious");
Directory.CreateDirectory(Path.Combine(root, "sub"));
Directory.CreateDirectory(outside);
string F(string p, int ageDays)
{
    File.WriteAllText(p, "x");
    File.SetCreationTime(p, DateTime.Now.AddDays(-ageDays));
    File.SetLastWriteTime(p, DateTime.Now.AddDays(-ageDays));
    return p;
}
// just copied/extracted (e.g. by an installer running right now): old modified date, brand-new creation date
var copied = Path.Combine(root, "just-extracted.dll");
File.WriteAllText(copied, "x");
File.SetLastWriteTime(copied, DateTime.Now.AddYears(-1));
var oldFile = F(Path.Combine(root, "old.tmp"), 3);
var newFile = F(Path.Combine(root, "new.tmp"), 0);
var oldSub = F(Path.Combine(root, "sub", "old2.tmp"), 3);
var victim = F(Path.Combine(outside, "victim.txt"), 3);
var roFile = F(Path.Combine(root, "readonly.tmp"), 3);
File.SetAttributes(roFile, FileAttributes.ReadOnly);
var junction = Path.Combine(root, "link");
Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
Check(Directory.Exists(junction) && File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint), "sandbox: junction creado hacia carpeta externa");

var cat = new CleanCategory { Id = "t", Name = "t", Description = "", Icon = "", MinAge = TimeSpan.FromHours(24), Roots = () => [root] };
var scan = Cleaner.Scan(cat);
var found = scan.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
Check(found.Contains(oldFile) && found.Contains(oldSub) && found.Contains(roFile), "escaneo: encuentra archivos viejos (incl. subcarpetas y solo-lectura)");
Check(!found.Contains(newFile), "escaneo: ignora archivos de menos de 24 h");
Check(!found.Contains(copied), "escaneo: ignora archivos recién copiados aunque su fecha de modificación sea antigua");
Check(!found.Any(p => p.Contains("victim")), "escaneo: NO entra en el junction");

// planted item outside the roots must be refused even if someone sneaks it into the confirmed list
scan.Items.Add(new CleanItem { Path = victim, Display = victim, Size = 1 });
var report = Cleaner.Clean([scan]);
Check(!File.Exists(oldFile) && !File.Exists(oldSub) && !File.Exists(roFile), "limpieza: borra los archivos confirmados");
Check(File.Exists(newFile), "limpieza: archivo reciente intacto");
Check(File.Exists(victim), "limpieza: archivo fuera de la lista blanca INTACTO");
Check(Directory.Exists(junction) && File.Exists(Path.Combine(junction, "victim.txt")), "limpieza: junction y su destino intactos");
Check(report.Deleted == 3 && report.Skipped == 1, $"limpieza: informe correcto ({report.Deleted} borrados, {report.Skipped} omitidos)");
Check(File.ReadAllText(report.ListFile).Contains("old.tmp"), "limpieza: guarda la lista de lo borrado");

// recycle-bin style dir item containing a junction: delete must not follow it
var rb = Path.Combine(sandbox, "rb"); var payload = Path.Combine(rb, "$RABC123");
Directory.CreateDirectory(payload); F(Path.Combine(payload, "f.txt"), 0);
Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(payload, "j")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
var rbScan = new ScanResult { Category = new CleanCategory { Id = "r", Name = "r", Description = "", Icon = "", RecycleBin = true, Roots = () => [rb] } };
rbScan.Roots.Add(rb);
rbScan.Items.Add(new CleanItem { Path = payload, Display = payload, IsDir = true, Size = 1 });
Cleaner.Clean([rbScan]);
Check(!Directory.Exists(payload), "papelera: borra la carpeta que contiene un junction");
Check(File.Exists(victim), "papelera: NO sigue el junction de dentro (destino intacto)");

// ---- Tweaks: backup / undo restores the exact original state ----
const string testKey = @"HKCU\Software\TidyTest";
Registry.CurrentUser.DeleteSubKeyTree(@"Software\TidyTest", false);
using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TidyTest")) { k.SetValue("Existing", 5, RegistryValueKind.DWord); k.SetValue("Text", "hola"); }
var tw = new Tweak
{
    Id = "test", Group = "t", Title = "t", Description = "", Icon = "",
    Ops = [new(testKey, "Existing", 1, 0), new(testKey, "Text", "adios", "x"), new(testKey, "Missing", 7, null)],
};
Check(!Tweaks.IsApplied(tw), "tweak: no aplicado al inicio");
Tweaks.Apply(tw);
Check(Tweaks.IsApplied(tw), "tweak: aplicado");
Check(File.Exists(Path.Combine(Store.Dir, "tweaks-backup.json")), "tweak: copia de seguridad en disco");
Tweaks.Apply(tw); // applying twice must not overwrite the original backup
Tweaks.Revert(tw);
Check(Equals(Tweaks.Read(testKey, "Existing"), 5) && Equals(Tweaks.Read(testKey, "Text"), "hola") && Tweaks.Read(testKey, "Missing") == null,
      "tweak: deshacer restaura valores originales exactos (y borra el que no existía)");
// tampered backup (it lives in a user-writable folder): undo must not write values the tweak doesn't own
Tweaks.Apply(tw);
var bpath = Path.Combine(Store.Dir, "tweaks-backup.json");
File.WriteAllText(bpath, File.ReadAllText(bpath).Replace("\"test\": [", "\"test\": [{\"Key\":\"HKCU\\\\Software\\\\TidyTest\",\"Name\":\"Evil\",\"Kind\":\"dword\",\"Value\":\"1\"},"));
Check(File.ReadAllText(bpath).Contains("Evil"), "sandbox: copia de seguridad manipulada");
Tweaks.Revert(tw);
Check(Tweaks.Read(testKey, "Evil") == null, "tweak: valor inyectado en la copia de seguridad BLOQUEADO");
Check(Equals(Tweaks.Read(testKey, "Existing"), 5), "tweak: los valores legítimos se restauran igualmente");
// values of other types come back with their exact type, not deleted or turned into text
using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TidyTest"))
{
    k.SetValue("Exp", @"%SystemRoot%\x", RegistryValueKind.ExpandString); k.SetValue("Q", 1L << 40, RegistryValueKind.QWord);
    k.SetValue("Bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary); k.SetValue("Multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
}
var tw2 = new Tweak
{
    Id = "test2", Group = "t", Title = "t", Description = "", Icon = "",
    Ops = [new(testKey, "Exp", 1, null), new(testKey, "Q", 1, null), new(testKey, "Bin", 1, null), new(testKey, "Multi", 1, null)],
};
Tweaks.Apply(tw2);
Tweaks.Revert(tw2);
using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TidyTest")!)
    Check(k.GetValueKind("Exp") == RegistryValueKind.ExpandString && Equals(Tweaks.Read(testKey, "Exp"), @"%SystemRoot%\x")
          && k.GetValueKind("Q") == RegistryValueKind.QWord && Equals(k.GetValue("Q"), 1L << 40)
          && ((byte[])k.GetValue("Bin")!).SequenceEqual(new byte[] { 1, 2, 3 }) && ((string[])k.GetValue("Multi")!).SequenceEqual(["a", "b"]),
          "tweak: deshacer devuelve cada valor con su tipo exacto (expandible, qword, binario, multicadena)");
Registry.CurrentUser.DeleteSubKeyTree(@"Software\TidyTest", false);
try { Tweaks.Read(@"HKCR\x", "y"); Check(false, "tweak: hive no permitido rechazado"); }
catch (ArgumentException) { Check(true, "tweak: hive no permitido rechazado"); }

// ---- Authenticode ----
var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe");
Check(Drivers.IsSignedBy(dotnet, "Microsoft Corporation"), "firma: dotnet.exe válido y firmado por Microsoft");
Check(!Drivers.IsSignedBy(dotnet, "NVIDIA Corporation"), "firma: dotnet.exe NO pasa como NVIDIA");
var tampered = Path.Combine(sandbox, "tampered.exe");
var bytes = File.ReadAllBytes(dotnet); bytes[bytes.Length / 3] ^= 0xFF; File.WriteAllBytes(tampered, bytes);
Check(!Drivers.IsSignedBy(tampered, "Microsoft Corporation"), "firma: ejecutable manipulado RECHAZADO");

// ---- Tweak catalog sanity ----
var ids = Tweaks.All.Select(t => t.Id).ToList();
Check(ids.Distinct().Count() == ids.Count, $"catálogo: {ids.Count} optimizaciones con id único");
var ops = Tweaks.All.SelectMany(t => t.Ops.Select(o => (t.Id, Key: o.Key.ToLowerInvariant(), Name: o.Name.ToLowerInvariant()))).ToList();
var shared = ops.GroupBy(o => (o.Key, o.Name)).Where(g => g.Select(o => o.Id).Distinct().Count() > 1).Select(g => $"{g.Key.Key}\\{g.Key.Name}").ToList();
Check(shared.Count == 0, "catálogo: ningún valor del registro lo tocan dos optimizaciones (deshacer una no rompe otra) " + string.Join(", ", shared));
Check(Tweaks.All.SelectMany(t => t.Ops).All(o => o.Key.StartsWith(@"HKCU\") || o.Key.StartsWith(@"HKLM\")), "catálogo: solo HKCU/HKLM");
Check(Tweaks.All.SelectMany(t => t.Ops).All(o => o.Value is int or string && (o.Default == null || o.Default.GetType() == o.Value.GetType())), "catálogo: tipos de valor coherentes con el original");
Check(!Tweaks.All.Any(t => t.Risk == Risk.Advanced && t.Recommended), "catálogo: nada avanzado entra en 'recomendadas'");
Check(Tweaks.All.All(t => t.Ops.Length > 0 || (t.Check != null && t.CustomApply != null && t.CustomRevert != null)), "catálogo: todo lo personalizado se puede comprobar y deshacer");
string[] forbidden = ["enablelua", "disableantispyware", "disablerealtimemonitoring", "enablevirtualizationbasedsecurity", "hypervisorenforcedcodeintegrity",
                      "featuresettingsoverride", "smartscreenenabled", "enablesmartscreen", "noautoupdate", "auoptions", "disablewindowsupdateaccess", "consentpromptbehavioradmin"];
Check(!ops.Any(o => forbidden.Contains(o.Name)), "catálogo: NINGUNA optimización baja la seguridad (UAC, Defender, SmartScreen, VBS, mitigaciones, Windows Update)");
Check(Tweaks.Presets.All(p => Tweaks.All.Where(t => t.Recommended).All(p.Includes)), "perfiles: todos incluyen las recomendadas");
Check(Tweaks.FromProfile(new Tweaks.Profile { Tweaks = ["no-existe", "../evil"] }).Count == 0, "importar: ids desconocidos se ignoran");

// ---- Services / tasks guards ----
Check(Services.IsProtected("WinDefend", "") && Services.IsProtected("RpcSs", "") && Services.IsProtected("wuauserv", "") && Services.IsProtected("Appinfo", ""), "servicios: críticos protegidos (Defender, RPC, Update, UAC)");
Check(Services.IsProtected("cbdhsvc_1a2b3", ""), "servicios: instancias por usuario protegidas");
Check(!Services.IsProtected("SysMain", "Automático"), "servicios: SysMain sí se puede cambiar");
string[] needed = ["WSearch", "Spooler", "bthserv", "WlanSvc", "lfsvc", "WbioSrvc", "SCardSvr", "XblAuthManager", "XblGameSave", "XboxGipSvc", "Audiosrv"];
Check(Unneeded.WindowsServices.All(s => !Services.IsProtected(s.Name, "Automático") && !needed.Contains(s.Name, StringComparer.OrdinalIgnoreCase)),
    "servicios que sobran: ninguno protegido ni de los que se usan (búsqueda, impresora, Bluetooth, Wi-Fi, Xbox…)");
Check(Unneeded.WindowsServices.All(s => s.Mode is StartMode.Manual or StartMode.Disabled), "servicios que sobran: solo pasan a manual o deshabilitado");

// ---- scheduled cleanup: only what you chose, and the recycle bin never by itself ----
var savedCats = Store.Settings.CleanCategories;
Store.Settings.CleanCategories = ["temp", "recycle"];
Check(CleanReminder.Scheduled(auto: true).Select(c => c.Id).SequenceEqual(["temp"]) && CleanReminder.Scheduled(auto: false).Select(c => c.Id).SequenceEqual(["temp", "recycle"]),
      "limpieza programada: solo lo marcado, y la papelera nunca se vacía sola (avisar sí)");
Store.Settings.CleanCategories = null;
Check(CleanReminder.Scheduled(auto: true).All(c => c.DefaultOn && !c.RecycleBin) && new Settings().AutoClean == false, "limpieza programada: por defecto solo avisa, con lo que Limpieza marca de serie");
Store.Settings.CleanCategories = savedCats;

// ---- what slows the boot: made-up slowdowns, nothing applied ----
StartupApp FakeStartup(string name, string command, string? exe, bool on = true) => new()
{
    Name = name, Command = command, ExePath = exe, Enabled = on, Location = "Registro", ApprovedHive = Microsoft.Win32.Registry.CurrentUser, ApprovedKey = "x", ApprovedName = name,
};
var bootItems = StartupPresets.BootItems([
        new(BootSlowKind.App, "Discord", "Discord.exe", 3, 2.5, DateTime.Now),
        new(BootSlowKind.App, "NZXT CAM", @"C:\Program Files\NZXT CAM\NZXT CAM.exe", 2, 4, DateTime.Now),
        new(BootSlowKind.App, "Steam", "steam.exe", 1, 1, DateTime.Now),
        new(BootSlowKind.Driver, "nvlddmkm", "nvlddmkm.sys", 5, 0.8, DateTime.Now),
        new(BootSlowKind.Service, "Windows Search", "WSearch", 2, 1.2, DateTime.Now)],
    [FakeStartup("Discord", @"""C:\Users\x\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", @"C:\Users\x\AppData\Local\Discord\Update.exe"),
     FakeStartup("NZXT CAM", @"""C:\Program Files\NZXT CAM\NZXT CAM.exe"" --startup", @"C:\Program Files\NZXT CAM\NZXT CAM.exe"),
     FakeStartup("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", @"C:\Program Files (x86)\Steam\steam.exe", on: false)]);
Check(bootItems.Count == 5 && bootItems.Where(i => i.Kind == PresetKind.App).Select(i => i.Name).SequenceEqual(["Discord"]) && bootItems.Single(i => i.Kind == PresetKind.App).On
      && bootItems.Where(i => i.Kind == PresetKind.Info).All(i => !i.On),
      "arranque: solo se ofrece quitar del inicio lo que se puede (Discord); hardware, apps ya desactivadas, drivers y servicios solo se explican");

// ---- hosts copies: only on a real change, and the restore list says exactly what goes back (fake copies in the sandbox; the real hosts is only read) ----
Check(Network.SameHosts("127.0.0.1 a  \r\n\r\n", "127.0.0.1 a\n") && !Network.SameHosts("127.0.0.1 a", "127.0.0.1 b"), "hosts: saltos de línea y espacios finales no cuentan como cambio");
var block = "# >>> Tidy: bloqueo de anuncios (no editar a mano) >>>\r\n0.0.0.0 x.com\r\n0.0.0.0 y.com\r\n# <<< Tidy: bloqueo de anuncios <<<\r\n";
var (hAdd, hDel) = Network.DiffHosts("127.0.0.1 a\n127.0.0.1 a\n", "127.0.0.1 a\n0.0.0.0 b\n" + block);
Check(hAdd.SequenceEqual(["0.0.0.0 b", "Lista de bloqueo de anuncios (2 dominios)"]) && hDel.SequenceEqual(["127.0.0.1 a"]), "hosts: diferencias línea a línea, repetidas incluidas, y la lista de bloqueo como una sola línea");
var hostsDir = Directory.CreateDirectory(Path.Combine(Store.SecureDir, "hosts")).FullName;
File.WriteAllText(Path.Combine(hostsDir, "hosts-20200101-000000.txt"), "127.0.0.1 a\n");
File.WriteAllText(Path.Combine(hostsDir, "hosts-20200101-000001.txt"), "127.0.0.1 a\n0.0.0.0 b\n");
File.WriteAllText(Path.Combine(hostsDir, "hosts-20200101-000002.txt"), "127.0.0.1 a\n0.0.0.0 b\n"); // repeated: a write that changed nothing
File.WriteAllText(Path.Combine(hostsDir, "hosts-20200101-000003.txt"), "0.0.0.0 b\n");
var undo = Network.HostsUndo(Network.HostsBackups().Last());
var older = undo.Steps.SkipWhile(s => !s.Removed.Contains("127.0.0.1 a")).ToList();
Check(older.Count == 2 && older[0].Removed.SequenceEqual(["127.0.0.1 a"]) && older[0].Added.Count == 0 && older[1].Added.SequenceEqual(["0.0.0.0 b"]) && older[1].Removed.Count == 0
      && Network.HostsTimeline()[1].Change.Added.Count + Network.HostsTimeline()[1].Change.Removed.Count == 0,
      "hosts: restaurar la copia más antigua lista cada cambio (borró, añadió) del más reciente al más antiguo, sin los que no cambiaron nada");
Check(Network.DeleteHostsBackups([new FileInfo(Path.Combine(hostsDir, "hosts-20200101-000002.txt")), new FileInfo(Path.Combine(Path.GetTempPath(), "hosts-20200101-000002.txt"))]) == 1
      && Network.HostsBackups().Count == 3, "hosts: borrar copias solo toca copias de Tidy, nunca un archivo de otra carpeta");
Directory.Delete(hostsDir, true);

// ---- disk scan console: only parsed here, never run (Repair-Volume needs admin and touches the real disk) ----
var scanPs = Encoding.Unicode.GetString(Convert.FromBase64String(Repairs.ScanScript()));
var parse = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
    "-NoProfile -NonInteractive -Command \"$e=$null; [void][Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$null, [ref]$e); exit $e.Count\"")
    { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true })!;
parse.StandardInput.Write(scanPs); parse.StandardInput.Close(); parse.WaitForExit();
Check(parse.ExitCode == 0 && scanPs.Contains("Repair-Volume -DriveLetter C -Scan") && !scanPs.Contains("-SpotFix") && !scanPs.Contains("-OfflineScanAndFix"),
    "analizar disco: el script es PowerShell válido y solo analiza (no repara)");
File.WriteAllText(Path.Combine(Directory.CreateDirectory(Store.SecureDir).FullName, "services-backup.json"),
    "{\"SysMain\":{\"Start\":0,\"Delayed\":0},\"WinDefend\":{\"Start\":4,\"Delayed\":0}}");
Check(!Services.Restore("SysMain") && !Services.Restore("WinDefend"), "servicios: copia manipulada (arranque de sistema / desactivar Defender) RECHAZADA");
File.Delete(Path.Combine(Store.SecureDir, "services-backup.json"));
Check(!ScheduledTasks.CanChange(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan"), "tareas: las de Windows Update son de solo lectura");
Check(ScheduledTasks.CanChange(ScheduledTasks.TelemetryTasks[0]) && ScheduledTasks.CanChange(@"\GoogleUpdateTaskMachineCore"), "tareas: telemetría y de terceros sí se pueden cambiar");

// ---- Uninstaller ----
Check(Uninstaller.BaseName("7-Zip 24.08 (x64)") == "7-Zip" && Uninstaller.BaseName("Discord") == "Discord" && Uninstaller.BaseName("VLC media player 3.0.21") == "VLC media player",
      "desinstalador: nombre base sin versión ni arquitectura");
Check(Uninstaller.CleanPublisher("Valve Corporation") == "Valve" && Uninstaller.CleanPublisher("Discord Inc.") == "Discord", "desinstalador: fabricante sin sufijos legales");
var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
Check(new[] { @"C:\", @"C:\Windows", @"C:\Windows\System32\drivers", pf, Path.Combine(pf, "Common Files"), @"C:\Users", up,
              Path.Combine(up, @"AppData\Roaming"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(up, "Downloads") }
          .All(Uninstaller.IsProtectedPath), "desinstalador: nunca ofrece C:\\, Windows, Program Files, Common Files, Usuarios, tu perfil, AppData, Documentos ni Descargas");
Check(!Uninstaller.IsProtectedPath(Path.Combine(pf, "AlgunaApp")), "desinstalador: Program Files\\AlgunaApp sí");

// ---- background apps: helpers join their program, but a shared folder never swallows other apps; only measuring here, nothing is closed ----
var local = Path.Combine(up, @"AppData\Local");
Check(Background.AppRoot(Path.Combine(pf, @"Steam\bin\cef\cef.win64\steamwebhelper.exe")) == Path.Combine(pf, "Steam")
      && Background.AppRoot(Path.Combine(local, @"Medal\recorder-1\MedalEncoder.exe")) == Path.Combine(local, "Medal")
      && Background.AppRoot(Path.Combine(local, @"Programs\Code\Code.exe")) == Path.Combine(local, @"Programs\Code")
      && Background.AppRoot(Path.Combine(local, @"Temp\x\a.exe")) == Path.Combine(local, @"Temp\x")
      && Background.AppRoot(Path.Combine(pf, @"WindowsApps\Pkg_1\App\a.exe")) == Path.Combine(pf, @"WindowsApps\Pkg_1")
      && Background.AppRoot(@"C:\Tools\Una\a.exe") == @"C:\Tools\Una" && Background.AppRoot(@"C:\Tools\a.exe") == @"C:\Tools"
      && Background.AppRoot(Path.Combine(up, @"Desktop\a.exe")) == Path.Combine(up, "Desktop") && Background.AppRoot(Path.Combine(pf, "a.exe")) == pf,
      "segundo plano: los ayudantes van con su programa, pero Program Files, AppData, Temp, el Escritorio o C:\\Tools nunca juntan apps distintas");
Check(new[] { @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0\msedgewebview2.exe", @"C:\Program Files\dotnet\dotnet.exe",
              @"C:\Program Files\nodejs\node.exe", Path.Combine(up, @"AppData\Local\Programs\Python\Python312\python.exe") }.All(Background.Runtimes.IsMatch)
      && !Background.Runtimes.IsMatch(Path.Combine(pf, @"Steam\steam.exe")), "segundo plano: WebView2, .NET, Node y Python nunca salen (otras apps viven dentro)");
var bg = Background.Analyze(TimeSpan.FromSeconds(1));
var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
Check(bg.All(a => a.Level is >= 1 and <= 3 && !a.Path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) && !a.Pids.Contains(Environment.ProcessId)
                  && StartupPresets.Level($"{a.Name} {a.Path}") != null),
      $"segundo plano: ni Windows, ni Tidy, ni nada protegido ({bg.Count} encontrados: {string.Join(", ", bg.Select(a => a.Name))})");
Check(Uninstaller.IsAllowedKey(@"HKCU\Software\Discord") && Uninstaller.IsAllowedKey(@"HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ABC}"), "desinstalador: claves de la app y su entrada de desinstalación sí");
Check(new[] { @"HKCU\Software\Microsoft", @"HKLM64\SOFTWARE\Classes", @"HKCU\Software", @"HKLM64\SYSTEM\CurrentControlSet", @"HKCU\Software\Policies",
              @"HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\..\x" }.All(k => !Uninstaller.IsAllowedKey(k)), "desinstalador: claves de sistema RECHAZADAS");
Check(Uninstaller.SplitCommand($"\"{Path.Combine(Environment.SystemDirectory, "notepad.exe")}\" /x", out var ue, out var ua) && ua == "/x" && ue.EndsWith("notepad.exe"), "desinstalador: separa ejecutable y argumentos");
Check(!Uninstaller.SplitCommand(@"C:\no\existe.exe /S", out _, out _), "desinstalador: desinstalador inexistente no se ejecuta");

var appDir = Path.Combine(sandbox, "TidyFakeApp123");
Directory.CreateDirectory(Path.Combine(appDir, "bin"));
File.WriteAllText(Path.Combine(appDir, "bin", "app.dll"), "dll");
var outsideVictim = Path.Combine(sandbox, "precious", "keep.txt");
Directory.CreateDirectory(Path.GetDirectoryName(outsideVictim)!); File.WriteAllText(outsideVictim, "no me borres");
Process.Start("cmd.exe", $"/c mklink /J \"{Path.Combine(appDir, "link")}\" \"{Path.GetDirectoryName(outsideVictim)}\"")!.WaitForExit();
using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TidyFakeApp123")) k.SetValue("Setting", "1");
var fake = new InstalledProgram { Name = "TidyFakeApp123 1.2.3 (x64)", Publisher = "Tidy Test Corp", InstallLocation = appDir, UninstallString = "" };
var left = Uninstaller.FindLeftovers(fake);
Check(left.Any(l => l.Kind == LeftoverKind.Folder && l.Target == appDir), "desinstalador: encuentra la carpeta de instalación");
Check(left.Any(l => l.Kind == LeftoverKind.RegKey && l.Target == @"HKCU\Software\TidyFakeApp123"), "desinstalador: encuentra su clave del registro");
Check(left.Where(l => l.Kind is LeftoverKind.Folder or LeftoverKind.File).All(l => l.Target.StartsWith(sandbox)), "desinstalador: no propone nada fuera de la app");
var q = Uninstaller.Remove(fake.Name, left);
Check(!Directory.Exists(appDir) && Registry.CurrentUser.OpenSubKey(@"Software\TidyFakeApp123") == null, "desinstalador: restos movidos a cuarentena");
Check(File.Exists(outsideVictim), "desinstalador: NO sigue el junction de dentro (lo de fuera intacto)");
Check(Uninstaller.Quarantines().Any(x => x.Id == q.Id), "desinstalador: la cuarentena aparece en Copias de seguridad");
var (rok, rfail) = Uninstaller.Restore(q);
Check(rfail == 0 && File.Exists(Path.Combine(appDir, "bin", "app.dll")) && Equals(Registry.CurrentUser.OpenSubKey(@"Software\TidyFakeApp123")?.GetValue("Setting"), "1"),
      $"desinstalador: RESTAURAR devuelve archivos y registro ({rok} elementos)");
Registry.CurrentUser.DeleteSubKeyTree(@"Software\TidyFakeApp123", false);
var bad = new InstalledProgram { Name = "Users", Publisher = "Microsoft", InstallLocation = @"C:\Users", UninstallString = "" };
Check(!Uninstaller.FindLeftovers(bad).Any(l => l.Kind == LeftoverKind.Folder && l.Target.TrimEnd('\\').Equals(@"C:\Users", StringComparison.OrdinalIgnoreCase)),
      "desinstalador: una app con InstallLocation = C:\\Users NO lo propone");

// ---- Storage ----
Check(Storage.IsProtected(@"C:\Windows\win.ini") && Storage.IsProtected(@"C:\pagefile.sys") && Storage.IsProtected(Path.Combine(pf, @"X\y.exe")), "almacenamiento: Windows, pagefile y programas protegidos");
var dupDir = Path.Combine(sandbox, "dups");
Directory.CreateDirectory(Path.Combine(dupDir, "a"));
var blob = new byte[300_000]; new Random(1).NextBytes(blob);
foreach (var p in new[] { "1.bin", "2.bin", @"a\3.bin" }) File.WriteAllBytes(Path.Combine(dupDir, p), blob);
var other = (byte[])blob.Clone(); other[^1] ^= 1; File.WriteAllBytes(Path.Combine(dupDir, "distinto.bin"), other);
var groups = Storage.Duplicates([dupDir], 1, null, CancellationToken.None);
Check(groups.Count == 1 && groups[0].Files.Count == 3, "duplicados: 3 copias idénticas agrupadas, el que difiere en 1 byte NO");
var (rec, skip) = Storage.Recycle([new FoundFile(@"C:\Windows\win.ini", 1, DateTime.Now, false)]);
Check(rec == 0 && skip == 1 && File.Exists(@"C:\Windows\win.ini"), "almacenamiento: archivo de Windows RECHAZADO aunque venga marcado como no protegido");
Check(Storage.IsProtected(@"C:\XboxGames\Dead By Daylight\Content\pakchunk2-WinGDK.pak") && Storage.IsProtected(@"D:\SteamLibrary\steamapps\common\X\data.pak")
      && !Storage.IsProtected(Path.Combine(sandbox, "mods.zip")), "almacenamiento: archivos de juegos instalados protegidos, los tuyos no");
var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
Check(Storage.IsProtected(Path.Combine(lad, @"Google\Chrome\User Data\weights.bin")) && Storage.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Code\x.db"))
      && !Storage.IsProtected(Path.Combine(lad, @"Temp\x.tmp")), "almacenamiento: datos de programas en AppData protegidos, Temp no");
Check(Storage.Kind("a.MP4") == FileKind.Video && Storage.Kind("b.zip") == FileKind.Archive && Storage.Kind("c.pdf") == FileKind.Document
      && Storage.Kind("d.rpf") == FileKind.Other && Storage.Kind("sin-extension") == FileKind.Other, "almacenamiento: tipo de archivo por extensión");
// move and put back: a name clash gets " (2)", nothing is overwritten, and undo refuses to overwrite a file that took the old place
var mvSrc = Path.Combine(sandbox, "mv-src"); var mvDst = Path.Combine(sandbox, "mv-dst");
Directory.CreateDirectory(mvSrc); Directory.CreateDirectory(mvDst);
File.WriteAllText(Path.Combine(mvSrc, "a.bin"), "A"); File.WriteAllText(Path.Combine(mvSrc, "b.bin"), "B"); File.WriteAllText(Path.Combine(mvDst, "a.bin"), "otro");
var mvFiles = new[] { "a.bin", "b.bin" }.Select(n => new FoundFile(Path.Combine(mvSrc, n), 1, DateTime.Now, false)).ToList();
var (mvd, mvs) = Storage.Move(mvFiles, mvDst);
var mvId = Storage.Moves().SingleOrDefault();
Check(mvd == 2 && mvs == 0 && File.ReadAllText(Path.Combine(mvDst, "a.bin")) == "otro" && File.ReadAllText(Path.Combine(mvDst, "a (2).bin")) == "A"
      && !File.Exists(Path.Combine(mvSrc, "a.bin")) && mvId != null && History.All()[0].Undo == $"moved:{mvId}", "mover: sin pisar nada (a (2).bin) y queda en Historial");
File.WriteAllText(Path.Combine(mvSrc, "b.bin"), "nuevo");
Check(!Storage.UndoMove(mvId!) && File.ReadAllText(Path.Combine(mvSrc, "a.bin")) == "A" && File.ReadAllText(Path.Combine(mvSrc, "b.bin")) == "nuevo"
      && Storage.Moves().Contains(mvId), "mover: deshacer devuelve a.bin, NO pisa el b.bin nuevo y lo deja pendiente");
File.Delete(Path.Combine(mvSrc, "b.bin"));
Check(Storage.UndoMove(mvId!) && File.ReadAllText(Path.Combine(mvSrc, "b.bin")) == "B" && !Storage.Moves().Any(), "mover: al quedar libre, deshacer termina");
Check(Storage.MoveRecord(@"..\..\history") == null, "mover: id con ruta RECHAZADO");
bool mvThrew = false; try { Storage.Move(mvFiles, @"C:\Windows"); } catch (IOException) { mvThrew = true; }
Check(mvThrew, "mover: carpeta de Windows como destino RECHAZADA");

// ---- Processes / network ----
var procs = Processes.List();
Check(procs.Count > 10, $"procesos: {procs.Count} programas listados");
var crit = procs.FirstOrDefault(p => p.Name.Equals("csrss", StringComparison.OrdinalIgnoreCase)) ?? new ProcInfo(0, "csrss", "", null, 0, 0, true, 1);
Check(crit.Critical && Processes.Close(crit).Closed == 0, "procesos: csrss es crítico y no se cierra");
Check(Network.Providers.SelectMany(p => p.Servers).All(s => System.Net.IPAddress.TryParse(s, out _)), "dns: todas las direcciones son IPs válidas");

// ---- winget parsing and input checks (nothing is installed) ----
var sample = "\r-\r\\\r\nNombre                Id                    Versión         Disponible     Origen\r\n" +
             "---------------------------------------------------------------------------------\r\n" +
             "Git                   Git.Git               2.54.0          2.56.0.3       winget\r\n" +
             "Ubisoft Connect       Ubisoft.Connect       < 174.2.0.13389 174.2.0.13389  winget\r\n" +
             "Algo raro             ARP\\Machine\\X64\\{1}  1.0             2.0            winget\r\n" +
             "38 actualizaciones disponibles.\r\n" +
             "3 paquete(s) tienen números de versión que no se pueden determinar. Use --include-unknown para ver todos los resultados.\r\n";
var parsed = Winget.Table(sample);
Check(parsed.Count == 2 && parsed[0] is { Id: "Git.Git", Version: "2.54.0", Available: "2.56.0.3", Source: "winget" } && parsed[1].Version == "< 174.2.0.13389",
    $"winget: tabla leída bien ({string.Join(", ", parsed.Select(p => p.Id))}), pie y entradas ARP ignorados");
Check(Winget.Table("Nada").Count == 0, "winget: salida sin tabla = lista vacía");
Check(Winget.Search("foo\" & calc.exe & \"").Count == 0 && Winget.Install("Evil\" --override \"x", "winget").Ok == false && Winget.Install("Git.Git", "file://x").Ok == false,
    "winget: búsquedas, ids u orígenes con comillas o raros RECHAZADOS antes de llamar a winget");
Check(Winget.Catalog.Select(a => a.Id).Distinct().Count() == Winget.Catalog.Length, $"winget: catálogo de {Winget.Catalog.Length} apps sin duplicados");

// ---- history (sandboxed secure folder) ----
History.Add(HistoryKind.Tweak, "Prueba", "detalle", "tweak:test");
var hist = History.All();
Check(hist.Count > 0 && hist[0].Title == "Prueba" && File.Exists(Path.Combine(Store.SecureDir, "history.json")), "historial: entrada guardada en la carpeta protegida");
Check(hist.Any(h => h.Title.StartsWith("Activada:")) && hist.Any(h => h.Title.StartsWith("Deshecha:")), "historial: las optimizaciones de las pruebas quedaron apuntadas");

// ---- game mode detection (no game is started) ----
var fakeGame = Path.Combine(sandbox, "MiJuego.exe");
File.WriteAllBytes(fakeGame, []);
Store.Settings.GameExes.Add(fakeGame);
Check(GameMode.GameName(fakeGame) == "MiJuego" && GameMode.GameName(@"C:\Windows\notepad.exe") == null, "modo juego: reconoce un juego añadido a mano y no el Bloc de notas");
Store.Settings.GameExes.Clear();
var libs = GameMode.Libraries();
Console.WriteLine($"INFO  Bibliotecas de juegos: {string.Join(" | ", libs.Select(l => $"{l.Launcher} {l.Root}"))}");

// ---- read-only security review and measurements ----
var sec = SecurityStatus.Run();
Check(sec.Count == 13, $"seguridad: {sec.Count} comprobaciones ejecutadas sin errores");
foreach (var c in sec) Console.WriteLine($"INFO  Seguridad {c.State,-8} {c.Title}: {c.Detail}");
var disks = SecurityStatus.DiskStatus(fresh: true);
Check(disks.Count > 0, $"discos: {string.Join(", ", disks.Select(d => $"{d.Name} ({d.Letters}) {d.Health}"))}");
var m1 = Benchmark.Measure("Primera");
var m2 = Benchmark.Measure("Segunda");
Check(Benchmark.All() is { Count: 2 } ms && ms[0].Label == "Primera" && m2.Processes > 10, $"mediciones: guardadas ({m2.Processes} procesos, {m2.RamUsedGb} GB RAM, arranque {m2.BootSec?.ToString("0.0") ?? "?"} s)");

// ---- 1.0.0 Spanish folder names migrate to English without losing anything ----
var oldQ = Path.Combine(sandbox, "datos", "cuarentena", "q-old");
Directory.CreateDirectory(Path.Combine(oldQ, "files"));
File.WriteAllText(Path.Combine(oldQ, "files", "keep.txt"), "x");
File.WriteAllText(Path.Combine(oldQ, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new Quarantine
    { Id = "q-old", App = "Vieja", Items = [new(LeftoverKind.File, Path.Combine(sandbox, "restored.txt"), Path.Combine(oldQ, "files", "keep.txt"))] }));
Directory.CreateDirectory(Path.Combine(Store.Dir, "limpiezas"));
File.WriteAllText(Path.Combine(Store.Dir, "limpiezas", "limpieza-1.txt"), "lista");
Store.MigrateFolders();
var mq = Uninstaller.Quarantines().FirstOrDefault(x => x.Id == "q-old");
Check(mq != null && File.Exists(mq.Items[0].Stored) && mq.Items[0].Stored.StartsWith(Uninstaller.QuarantineRoot)
    && File.Exists(Path.Combine(Store.CleanupsDir, "limpieza-1.txt")) && !Directory.Exists(Path.Combine(sandbox, "datos")), "carpetas: datos/cuarentena/limpiezas pasan a data/quarantine/cleanups");
Check(mq != null && Uninstaller.Restore(mq).Failed == 0 && File.ReadAllText(Path.Combine(sandbox, "restored.txt")) == "x", "carpetas: la cuarentena migrada se puede restaurar");

// ---- updates: only a verified GitHub release of Tidy is accepted ----
string Rel(string tag, string url, long size, string? digest, bool draft = false) =>
    $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":false,"body":"notas","assets":[{"name":"Tidy-Setup.exe","size":{{size}},"browser_download_url":"{{url}}"{{(digest == null ? "" : $",\"digest\":\"sha256:{digest}\"")}}}]}""";
var tidyExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\src\Tidy\bin\Debug\net10.0-windows\win-x64\Tidy.exe"));
var exeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tidyExe)));
var exeVer = new Version(System.Diagnostics.FileVersionInfo.GetVersionInfo(tidyExe).FileVersion!).ToString(3);
var okUrl = $"https://github.com/Jeray665/Tidy/releases/download/v{exeVer}/Tidy-Setup.exe";
var rel = Updater.Parse(System.Text.Json.JsonDocument.Parse(Rel("v" + exeVer, okUrl, new FileInfo(tidyExe).Length, exeHash)).RootElement);
Check(rel != null && rel.Version == new Version(exeVer) && rel.Sha256 == exeHash, "actualizar: lee versión, tamaño y huella de la release");
Check(Updater.Parse(System.Text.Json.JsonDocument.Parse(Rel("v9.0.0", "https://evil.example/Tidy-Setup.exe", 10, exeHash)).RootElement) == null, "actualizar: rechaza descargas fuera de github.com/Jeray665/Tidy");
Check(Updater.Parse(System.Text.Json.JsonDocument.Parse(Rel("v9.0.0", okUrl, 10, exeHash, draft: true)).RootElement) == null, "actualizar: ignora borradores");
bool Rejects(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
Check(rel != null && !Rejects(() => Updater.Verify(tidyExe, rel, exeHash)), "actualizar: acepta el Tidy.exe correcto");
Check(rel != null && Rejects(() => Updater.Verify(tidyExe, rel, new string('0', 64))), "actualizar: rechaza si la huella SHA-256 no coincide");
Check(rel != null && Rejects(() => Updater.Verify(tidyExe, rel with { Size = rel.Size + 1 }, exeHash)), "actualizar: rechaza si el tamaño no coincide");
var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
Check(rel != null && Rejects(() => Updater.Verify(notepad, rel with { Size = new FileInfo(notepad).Length }, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(notepad))))), "actualizar: rechaza un .exe que no es Tidy aunque la huella cuadre");
var pb = Updater.FromRedirect("https://github.com/Jeray665/Tidy/releases/tag/v1.2.0", 123);
Check(pb != null && pb.Version == new Version(1, 2, 0) && pb.DownloadUrl == "https://github.com/Jeray665/Tidy/releases/download/v1.2.0/Tidy-Setup.exe"
      && pb.Sha256 == null && pb.Sha256Url == pb.DownloadUrl + ".sha256", "plan B: la página de releases da la versión y las URLs de GitHub");
Check(new[] { "https://github.com/Otro/Tidy/releases/tag/v9.0.0", "https://github.com/Jeray665/Tidy/releases/tag/v9.0.0/../../x", "https://github.com/Jeray665/Tidy/releases", null }
      .All(l => Updater.FromRedirect(l, 1) == null), "plan B: rechaza otros repos, etiquetas raras y la página sin release");
try
{
    var liveRel = await Updater.FromReleasesPage();
    if (liveRel == null) Console.WriteLine("INFO  plan B (en vivo): no hay ninguna release publicada en GitHub; se prueba al publicar");
    else
    {
        var liveFile = await Updater.Download(liveRel);
        Check(liveFile != null && File.Exists(liveFile) && liveFile.StartsWith(Store.DownloadsDir),
              $"plan B (en vivo, sin API): encuentra {liveRel.Version}, descarga {Store.FormatBytes(liveRel.Size)} y pasa tamaño + SHA-256 + versión");
    }
}
catch (Exception ex) { Check(false, $"plan B (en vivo): {ex.Message}"); }
try
{
    var full = await Updater.Check();
    // null = no newer version (or none published yet, which must not be an error)
    Check(full == null || full.Version > Updater.Current, $"buscar actualizaciones (API o plan B si está limitada): {(full == null ? "al día" : $"ve la {full.Version}")}, sin error aunque no haya releases");
}
catch (Exception ex) { Check(false, $"buscar actualizaciones: {ex.Message}"); }

var swapDir = Path.Combine(sandbox, "swap"); Directory.CreateDirectory(swapDir);
var running = Path.Combine(swapDir, "Tidy.exe"); File.WriteAllText(running, "old");
var newer = Path.Combine(sandbox, "new.exe"); File.WriteAllText(newer, "new");
using (File.Open(running, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) Install.ReplaceExe(newer, running);
Check(File.ReadAllText(running) == "new" && File.ReadAllText(Path.Combine(swapDir, "Tidy.old.exe")) == "old", "actualizar: cambia el .exe aunque esté abierto y guarda el anterior");
var log = Changelog.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\CHANGELOG.md"))));
Check(log.Count >= 1 && log[0].Version.ToString() == System.Diagnostics.FileVersionInfo.GetVersionInfo(tidyExe).ProductVersion?.Split('+')[0] && log.All(v => v.Items.Count > 0), $"novedades: CHANGELOG.md tiene la versión de Tidy.exe ({log[0].Version}) con sus cambios");

// ---- texts: every UI string, doc and note is valid UTF-8 without broken accents (UTF-8 read as ANSI) ----
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\"));
var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
var brokenText = new List<string>();
foreach (var f in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
    .Where(f => new[] { ".cs", ".xaml", ".md", ".csproj", ".py", ".manifest" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .Where(f => !Regex.IsMatch(f, @"\\(bin|obj|dist|\.git)\\")))
{
    try
    {
        var text = strictUtf8.GetString(File.ReadAllBytes(f)).TrimStart('\uFEFF');
        var at = text.IndexOfAny(['\u00C3', '\u00C2', '\uFFFD', '\uFEFF']);
        if (at >= 0) brokenText.Add($"{Path.GetRelativePath(repoRoot, f)}:{text[..at].Count(c => c == '\n') + 1}");
    }
    catch (DecoderFallbackException) { brokenText.Add($"{Path.GetRelativePath(repoRoot, f)} (no es UTF-8)"); }
}
Check(brokenText.Count == 0, $"textos: sin acentos rotos ni archivos fuera de UTF-8 {string.Join(", ", brokenText)}");

Check(PowerTimeouts.Describe(0) == "nunca" && PowerTimeouts.Describe(900) == "15 min" && PowerTimeouts.Describe(5400) == "1 h 30 min", "energía: los tiempos se leen bien (nunca, 15 min, 1 h 30 min)");
Console.WriteLine($"INFO  Energía (plan activo): {string.Join(" | ", PowerTimeouts.All.Select(t => $"{t.Id} {PowerTimeouts.Describe(PowerTimeouts.Read(t).Ac)}"))}, hibernación {(PowerTimeouts.HibernateAvailable ? "sí" : "no")}, batería {(PowerTimeouts.HasBattery ? "sí" : "no")}");

Check(StartupPresets.Level("NVIDIA Telemetry Container") == 1 && StartupPresets.Level("Acme Crash Reporter") == 1
      && StartupPresets.Level("GoXLR Utility") == null && StartupPresets.Level("Claude") == null && StartupPresets.Level("Cloudflare One Client") == null
      && StartupPresets.Level("Steam steam.exe -silent Valve") == 2 && StartupPresets.Level("Acme Widget acme.exe") == 3
      && StartupPresets.Level("Google Update Core") == null && StartupPresets.Level("Avast Antivirus") == null
      && StartupPresets.Level("Realtek HD Audio Manager") == null && StartupPresets.Level("Microsoft OneDrive") == null
      && StartupPresets.Level("AnyDesk") == null && StartupPresets.Level("Steam Client Service", launchersAllowed: false) == 3,
      "preconfiguraciones de inicio: nunca tocan actualizadores, antivirus, audio, nube ni acceso remoto");
var sq = new System.Text.RegularExpressions.Regex(@"\S*Update\.exe""?\s+--processStart", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
Check(StartupPresets.Level("Discord " + sq.Replace(@"""C:\Users\x\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", "")) == 2,
      "preconfiguraciones de inicio: Discord (arranca con Update.exe) cuenta como app de chat, no como actualizador");
var presetItems = StartupPresets.Candidates(); // read-only: lists, never applies
Console.WriteLine($"INFO  Preconfiguraciones en este PC: Ligero {presetItems.Count(i => i.Level <= 1)}, Equilibrado {presetItems.Count(i => i.Level <= 2)}, Máximo {presetItems.Count}");
foreach (var i in presetItems) Console.WriteLine($"INFO    [{i.Level}] {i.Kind}: {i.Name} ({i.Detail})");

// ---- Running without admin (installers that refuse to run elevated) ----
UnelevatedChecks();
void UnelevatedChecks()
{
    var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
    if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        var ug = Store.RunUnelevated(whoami, "/groups");
        Check(ug.Exit == 0 && ug.Output.Contains("S-1-16-8192") && !ug.Output.Contains("S-1-16-12288"), "sin administrador: el proceso corre con nivel medio, no elevado");
        Check(Store.RunUnelevated(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 7").Exit == 7, "sin administrador: devuelve el código de salida");
    }
    else Check(Store.RunUnelevated(whoami, "/groups") is var r0 && (r0.Exit == 0 || r0.Output.StartsWith("No se pudo")), "sin administrador: sin elevar, no rompe");
}

// ---- Read-only probes of the real system ----
SysMon.Cpu(); Thread.Sleep(500);
var ram = SysMon.Ram();
Console.WriteLine($"INFO  CPU {SysMon.Cpu():0}%  RAM {ram.Pct}% ({ram.UsedGb:0.0}/{ram.TotalGb:0.0} GB)  listas {Memory.Lists()}");
Console.WriteLine($"INFO  GPU {SysMon.Gpu()}");
Console.WriteLine($"INFO  Planes: {string.Join(" | ", PowerPlan.All().Select(p => p.Name + (p.Active ? "*" : "")))}");
Console.WriteLine($"INFO  Hardware: {string.Join(" | ", Hardware.Read().Select(s => s.Headline))}");
var screens = Hardware.Displays();
Check(screens.Count >= 1 && screens.All(d => d.Value.Contains(" Hz")), $"pantallas: {string.Join(" | ", screens.Select(d => $"{d.Label} {d.Value}"))}");
var inv = Drivers.Inventory();
Console.WriteLine($"INFO  Drivers de terceros: {inv.Count}  (ej. {string.Join(", ", inv.Take(3).Select(d => d.Device + " " + d.Version))})");
Console.WriteLine($"INFO  Apps de inicio: {string.Join(", ", StartupApps.List().Select(a => a.Name + (a.Enabled ? "" : "(off)")))}");
Console.WriteLine($"INFO  Tweaks aplicados ya: {string.Join(", ", Tweaks.All.Where(Tweaks.IsApplied).Select(t => t.Id))}");
var nv = await Drivers.CheckNvidia();
Console.WriteLine($"INFO  NVIDIA: {(nv == null ? "al día / no hay" : $"{nv.CurrentVersion} -> {nv.NewVersion} ({nv.NewDateText}) {nv.DownloadUrl}")}");
foreach (var c in Cleaner.Categories) { var r = Cleaner.Scan(c); Console.WriteLine($"INFO  Limpieza {c.Name}: {r.Items.Count} elementos, {Store.FormatBytes(r.Bytes)} {r.Note}"); }
if (args.Contains("--wu"))
    foreach (var u in Drivers.SearchWindowsUpdate(inv)) Console.WriteLine($"INFO  WU: {u.Device} {u.CurrentVersion} -> {u.NewVersion} ({u.Size})");

foreach (var line in File.ReadLines(Store.LogPath).Where(l => l.Contains("Drivers"))) Console.WriteLine($"LOG   {line}");
Cleaner.DeleteTree(sandbox);
Console.WriteLine(failed == 0 ? "\nTODO OK" : $"\n{failed} FALLOS");
return failed;
