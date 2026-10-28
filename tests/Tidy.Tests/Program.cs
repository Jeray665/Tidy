// Safety checks for Tidy's core. Everything destructive runs inside a throwaway sandbox folder and a
// throwaway HKCU\Software\TidyTest key. Usage: Tidy.Tests <sandboxDir> [--wu]
using System.Diagnostics;
using System.IO;
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
var framework = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // both projects target the same framework
var tidyExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\src\Tidy\bin\Debug", framework, @"win-x64\Tidy.exe"));
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
var body = "- New thing\n\n**Tidy-Setup.exe** installs.\n\n<details><summary>Español</summary>\n\n- Cosa nueva\n\n</details>\n";
Check(Changelog.NotesFor(body, false) == "- New thing\n\n**Tidy-Setup.exe** installs." && Changelog.NotesFor(body, true) == "- Cosa nueva"
    && Changelog.NotesFor("- Solo español", false) == "- Solo español", "notas de versión: inglés por defecto, español plegado, las antiguas enteras");
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

// ---- Images: convert, compress, resize, hidden data (all inside the sandbox) ----
{
    var imgDir = Path.Combine(sandbox, "img");
    Directory.CreateDirectory(imgDir);
    void Save(string path, System.Windows.Media.Imaging.BitmapEncoder enc, System.Windows.Media.Imaging.BitmapSource src, System.Windows.Media.Imaging.BitmapMetadata? meta = null)
    {
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src, null, meta, null));
        using var f = File.Create(path);
        enc.Save(f);
    }
    System.Windows.Media.Imaging.BitmapSource Solid(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> px)
    {
        var buf = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var (b, g, r, a) = px(x, y); var i = (y * w + x) * 4; (buf[i], buf[i + 1], buf[i + 2], buf[i + 3]) = (b, g, r, a); }
        return System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, buf, w * 4);
    }
    System.Windows.Media.Imaging.BitmapFrame Open(string path) =>
        System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(path)), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
    byte[] Pixel(System.Windows.Media.Imaging.BitmapSource s, int x, int y)
    {
        var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(s, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var p = new byte[4];
        c.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), p, 4, 0);
        return p;
    }

    // a PNG whose left half is see-through, right half red
    var png = Path.Combine(imgDir, "a.png");
    Save(png, new System.Windows.Media.Imaging.PngBitmapEncoder(), Solid(400, 300, (x, _) => x < 200 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255, (byte)255)));
    // a camera-style JPG stored sideways (orientation 6) with the camera model and GPS in its hidden data
    var jpg = Path.Combine(imgDir, "b.jpg");
    var meta = new System.Windows.Media.Imaging.BitmapMetadata("jpg");
    meta.SetQuery("System.Photo.Orientation", (ushort)6);
    meta.SetQuery("System.Photo.CameraModel", "TidyCam 3000");
    meta.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
    Save(jpg, new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 98 }, Solid(600, 400, (x, y) => ((byte)(x % 256), (byte)(y % 256), (byte)128, (byte)255)), meta);
    var pngBefore = File.ReadAllBytes(png);
    var jpgBefore = File.ReadAllBytes(jpg);
    string? Model(string path) { try { return (Open(path).Metadata as System.Windows.Media.Imaging.BitmapMetadata)?.GetQuery("System.Photo.CameraModel") as string; } catch { return null; } }
    Check(Model(jpg) == "TidyCam 3000", "imágenes: la foto de prueba lleva modelo de cámara");

    var r1 = Images.Process([png, jpg], new ImageJob("jpg", 85, 0, true, null));
    Check(r1.All(r => r.To != null) && File.Exists(Path.Combine(imgDir, "a (Tidy).jpg")) && File.Exists(Path.Combine(imgDir, "b (Tidy).jpg")), "imágenes: crea \"nombre (Tidy).jpg\" junto al original");
    Check(File.ReadAllBytes(png).SequenceEqual(pngBefore) && File.ReadAllBytes(jpg).SequenceEqual(jpgBefore), "imágenes: los originales no cambian ni un byte");
    var aJpg = Open(Path.Combine(imgDir, "a (Tidy).jpg"));
    var white = Pixel(aJpg, 50, 150); var red = Pixel(aJpg, 350, 150);
    Check(white[0] > 245 && white[1] > 245 && white[2] > 245 && red[2] > 230 && red[0] < 30, "imágenes: lo transparente pasa a blanco en JPG (no a negro)");
    Check(r1[0].Note?.Contains("transparentes") == true, "imágenes: avisa de que tenía transparencias");
    var bJpg = Open(Path.Combine(imgDir, "b (Tidy).jpg"));
    Check(bJpg.PixelWidth == 400 && bJpg.PixelHeight == 600, "imágenes: la foto tumbada se endereza de verdad (600x400 -> 400x600)");
    Check(Model(Path.Combine(imgDir, "b (Tidy).jpg")) == null, "imágenes: quitar datos ocultos borra el modelo de cámara y el GPS");

    var r2 = Images.Process([jpg], new ImageJob("", 90, 0, false, null));
    var kept = Path.Combine(imgDir, "b (Tidy) (2).jpg");
    Check(r2[0].To == kept && File.Exists(Path.Combine(imgDir, "b (Tidy).jpg")), "imágenes: nunca sobrescribe, la segunda copia es \"(Tidy) (2)\"");
    Check(Model(kept) == "TidyCam 3000" && (Open(kept).Metadata as System.Windows.Media.Imaging.BitmapMetadata)?.GetQuery("System.Photo.Orientation") is ushort o && o == 1 && Open(kept).PixelWidth == 400,
          "imágenes: sin quitar datos, se conservan y la orientación pasa a normal (no se gira dos veces)");

    var outDir = Path.Combine(sandbox, "img-out");
    Directory.CreateDirectory(outDir);
    var r3 = Images.Process([png], new ImageJob("", 85, 100, true, outDir));
    var small = Open(Path.Combine(outDir, "a (Tidy).png"));
    Check(small.PixelWidth == 100 && small.PixelHeight == 75 && Pixel(small, 10, 37)[3] == 0, "imágenes: reduce a 100 px manteniendo proporción y transparencia (PNG)");
    var r3b = Images.Process([png], new ImageJob("png", 85, 5000, true, outDir));
    Check(Open(r3b[0].To!).PixelWidth == 400, "imágenes: nunca agranda");

    var r4 = Images.Process([png], new ImageJob("ico", 85, 0, true, outDir));
    var ico = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(r4[0].To!)), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    Check(ico is System.Windows.Media.Imaging.IconBitmapDecoder && ico.Frames.Select(f => f.PixelWidth).Order().SequenceEqual([16, 24, 32, 48, 64, 128, 256]) && ico.Frames.All(f => f.PixelWidth == f.PixelHeight),
          "imágenes: ICO válido con 16 a 256 px, cuadrados");
    foreach (var fmt in new[] { "bmp", "tiff", "gif" })
        Check(Images.Process([jpg], new ImageJob(fmt, 85, 0, true, outDir))[0] is { To: { } t } && Open(t).PixelHeight == 600, $"imágenes: {fmt.ToUpperInvariant()} se crea y se abre");

    var r5 = Images.Process([png], new ImageJob("jpg", 85, 0, true, Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    Check(r5[0].To == null && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "a (Tidy).jpg")), "imágenes: nunca guarda en C:\\Windows");
    var gif = Path.Combine(imgDir, "anim.gif");
    var genc = new System.Windows.Media.Imaging.GifBitmapEncoder();
    genc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Solid(20, 20, (_, _) => (0, 0, 255, 255))));
    genc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Solid(20, 20, (_, _) => (255, 0, 0, 255))));
    using (var gf = File.Create(gif)) genc.Save(gf);
    Check(Images.Process([gif], new ImageJob("png", 85, 0, true, outDir))[0] is { To: null, Note: { } gn } && gn.Contains("varias"), "imágenes: un GIF animado se deja como está (no pierde fotogramas)");
    var junk = Path.Combine(imgDir, "roto.jpg");
    File.WriteAllText(junk, "esto no es una imagen");
    Check(Images.Process([junk], new ImageJob("png", 85, 0, true, outDir))[0] is { To: null, Note: not null }, "imágenes: un archivo roto da un motivo, no rompe el lote");

    var batches = Images.Batches().ToList();
    var first = batches.Select(Images.Record).FirstOrDefault(rec => rec?.Count == 2);
    Check(batches.Count >= 5 && first != null && first.All(p => p.Contains(" (Tidy)")) && !first.Contains(png) && !first.Contains(jpg),
          "imágenes: Historial guarda solo las copias creadas (deshacer nunca toca los originales)");
    Check(Images.Record(@"..\..\x") == null, "imágenes: un id con ruta no vale");
    Check(History.All().Any(h => h.Undo?.StartsWith("created:") == true && h.Title.Contains("convertidas a JPG")), "imágenes: queda en Historial con deshacer");
}

// ---- File check: fingerprints ----
{
    var f = Path.Combine(sandbox, "abc.txt");
    File.WriteAllText(f, "abc");
    var sha = await FileCheck.Hash(f, "SHA-256");
    Check(sha == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "huella: SHA-256 correcta");
    Check(await FileCheck.Hash(f, "MD5") == "900150983cd24fb0d6963f7d28e17f72", "huella: MD5 correcta");
    Check(FileCheck.Matches(sha, " SHA256: BA7816BF 8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD "), "huella: acepta mayúsculas, espacios y \"sha256:\"");
    Check(!FileCheck.Matches(sha, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ae") && !FileCheck.Matches(sha, "hola"), "huella: una distinta o texto suelto NO coinciden");
    Check(FileCheck.AlgorithmOf(sha) == "SHA-256" && FileCheck.AlgorithmOf("900150983cd24fb0d6963f7d28e17f72") == "MD5" && FileCheck.AlgorithmOf("xyz") == null, "huella: reconoce el tipo por la longitud");
    Check(FileCheck.HasValidSignature(Path.Combine(Environment.SystemDirectory, "notepad.exe")) || FileCheck.HasValidSignature(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
          "huella: firma de Windows válida");
    Check(!FileCheck.HasValidSignature(f), "huella: un archivo sin firma no pasa");
}

// ---- Video: compress, sound only (all inside the sandbox) ----
{
    var vDir = Path.Combine(sandbox, "vid");
    Directory.CreateDirectory(vDir);
    async Task<Windows.Media.MediaProperties.MediaEncodingProfile> Probe(string p)
    {
        using var s = File.OpenRead(p);
        return await Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromStreamAsync(s.AsRandomAccessStream());
    }
    // two seconds of a 440 Hz tone as a WAV
    var wav = Path.Combine(vDir, "tono.wav");
    using (var w = new BinaryWriter(File.Create(wav)))
    {
        const int rate = 44100, samples = rate * 2;
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
    }

    Check(Video.Fit(1920, 1080, 720) == (1280u, 720u) && Video.Fit(1080, 1920, 720) == (720u, 1280u) && Video.Fit(640, 360, 720) == (640u, 360u),
          "vídeo: reduce por el lado corto (también en vertical) y nunca agranda");
    Check(Video.Bitrate(1920, 1080, 30, 1, 0) is > 3_000_000 and < 6_000_000 && Video.Bitrate(1920, 1080, 30, 0, 2_000_000) <= 1_700_000,
          "vídeo: calidad razonable y nunca más datos que el original");
    var mp3 = (await Video.Process([wav], new VideoJob("mp3", 1, 0, null)))[0];
    Check(mp3.To is { } m && m.EndsWith("tono (Tidy).mp3") && (await Probe(m)) is { Audio: not null, Video: null }, "vídeo: WAV a MP3 (" + mp3.Note + ")");
    var m4a = (await Video.Process([wav], new VideoJob("m4a", 1, 0, null)))[0];
    Check(m4a.To is { } a && (await Probe(a)).Audio != null, "vídeo: WAV a M4A (" + m4a.Note + ")");
    Check((await Video.Process([wav], new VideoJob("mp4", 1, 0, null)))[0] is { To: null, Note: { } noVideo } && noVideo.Contains("imagen"), "vídeo: un audio no se comprime como vídeo (y dice por qué)");

    var oobe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4");
    if (File.Exists(oobe))
    {
        var clip = Path.Combine(vDir, "clip.mp4");
        File.Copy(oobe, clip);
        var original = File.ReadAllBytes(clip);
        var src = await Probe(clip);
        Console.WriteLine($"INFO  Vídeo de prueba: {src.Video?.Width}x{src.Video?.Height}, {src.Video?.Bitrate / 1000} kbps, sonido {src.Audio != null}, {original.Length / 1024} KB");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = (await Video.Process([clip], new VideoJob("mp4", 1, 480, null)))[0];
        Console.WriteLine($"INFO  Comprimido en {sw.ElapsedMilliseconds} ms: {r.Before / 1024} KB -> {r.After / 1024} KB {r.Note}");
        var made = r.To != null ? await Probe(r.To) : null;
        var expectShort = Math.Min(480u, Math.Min(src.Video!.Width, src.Video.Height));
        Check(made?.Video is { } mv && Math.Min(mv.Width, mv.Height) == expectShort && (src.Audio == null) == (made.Audio == null),
              $"vídeo: MP4 a 480p que Windows abre, con su sonido ({r.Note}{made?.Video?.Width}x{made?.Video?.Height})");
        Check(File.ReadAllBytes(clip).SequenceEqual(original), "vídeo: el original no cambia ni un byte");
        if (src.Audio != null) Check((await Video.Process([clip], new VideoJob("mp3", 1, 0, null)))[0].To is { } cm && (await Probe(cm)).Video == null, "vídeo: saca el sonido de un vídeo a MP3");

        // filmed upright on a phone: sideways frames plus "turn 90°" in the video track's header (tkhd matrix)
        var upright = Path.Combine(vDir, "vertical.mp4");
        var clipBytes = File.ReadAllBytes(clip);
        for (var i = 4; i + 84 < clipBytes.Length; i++)
        {
            if (clipBytes[i] != 't' || clipBytes[i + 1] != 'k' || clipBytes[i + 2] != 'h' || clipBytes[i + 3] != 'd' || clipBytes[i + 4] != 0) continue;
            var matrix = i + 4 + 4 + 4 * 5 + 8 + 2 * 4; // version/flags, 5 fields, reserved, layer/group/volume/reserved
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(clipBytes.AsSpan(matrix + 36)) == 0) continue; // the sound track has no width
            int[] turn = [0, 0x10000, 0, -0x10000, 0, 0, 0, 0, 0x40000000];
            for (var k = 0; k < 9; k++) System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(clipBytes.AsSpan(matrix + 4 * k), turn[k]);
            break;
        }
        File.WriteAllBytes(upright, clipBytes);
        var ur = (await Video.Process([upright], new VideoJob("mp4", 1, 480, null)))[0];
        var urOut = ur.To != null ? await Probe(ur.To) : null;
        Check(urOut?.Video is { } uv && uv.Height > uv.Width && Math.Min(uv.Width, uv.Height) == expectShort,
              $"vídeo: un vídeo grabado en vertical sale en vertical ({ur.Note}{urOut?.Video?.Width}x{urOut?.Video?.Height})");

        // an MP4 that is already small: a bigger "compressed" copy isn't kept
        var grown = (await Video.Process([upright], new VideoJob("mp4", 0, 0, null)))[0];
        Console.WriteLine($"INFO  Vídeo ya comprimido a calidad alta: {grown.To ?? "sin copia"} {grown.Note}");
        Check(grown.To != null ? grown.After < grown.Before : grown.Note?.Contains("ocuparía más") == true && Directory.GetFiles(vDir, "vertical (Tidy) (2)*").Length == 0,
              "vídeo: si la copia de un MP4 iba a ocupar más, no se guarda");

        // cancelled half way: the unfinished file doesn't stay behind
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        try { await Video.Process([clip], new VideoJob("mp4", 0, 0, null), each: new Progress<(int, double)>(p => { if (p.Item2 > 0.02) cts.Cancel(); }), ct: cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        var second = Path.Combine(vDir, "clip (Tidy) (2).mp4");
        Check(cancelled ? !File.Exists(second) : File.Exists(second) && new FileInfo(second).Length > 0, $"vídeo: al cancelar no queda un archivo a medias (cancelado: {cancelled})");
    }
    else Console.WriteLine("INFO  Sin vídeo de Windows para probar la compresión");

    Check((await Video.Process([wav], new VideoJob("mp3", 1, 0, Environment.GetFolderPath(Environment.SpecialFolder.Windows))))[0].To == null, "vídeo: nunca guarda en C:\\Windows");
    var broken = Path.Combine(vDir, "roto.mp4");
    File.WriteAllText(broken, "esto no es un vídeo");
    Check((await Video.Process([broken], new VideoJob("mp4", 1, 0, null)))[0] is { To: null, Note: not null } && !File.Exists(Path.Combine(vDir, "roto (Tidy).mp4")),
          "vídeo: un archivo roto da un motivo y no deja nada");
    Check(History.All().Any(h => h.Undo?.StartsWith("created:") == true && h.Title.Contains("audios sacados a MP3")) && Images.Batches().Select(Images.Record).Any(rec => rec?.Any(p => p.EndsWith("tono (Tidy).mp3")) == true),
          "vídeo: queda en Historial y se puede mandar a la Papelera");
}

// ---- App catalog: newest version line ----
var py = new CatalogApp("Python.Python.3.13", "Python 3.13", "Desarrollo", "Lenguaje de programación.");
Check(Winget.NewestOf(py, ["Python.Python.3.1", "Python.Python.3.12", "Python.Python.3.15", "Python.Python.3.14", "Python.Launcher"]) is { Id: "Python.Python.3.15", Name: "Python 3.15" },
      "catálogo: elige la versión más nueva (3.15, no 3.1 ni 3.14) y cambia el nombre");
Check(Winget.NewestOf(py, ["Python.Python.3.12", "Python.Python.3.13"]) == null, "catálogo: nunca baja a una versión más vieja");
Check(Winget.NewestOf(py, ["Python.Python.3.14t", "Evil.Python.Python.3.99", "Python.Python.3.99.x"]) == null, "catálogo: ignora ids que no son de la misma línea");
Check(Winget.NewestOf(new CatalogApp("GIMP.GIMP.3", "GIMP", "", ""), ["GIMP.GIMP.4"]) is { Id: "GIMP.GIMP.4", Name: "GIMP" }, "catálogo: un nombre sin número se queda igual");
Check(Winget.NewestOf(py, ["Python.Python.2", "Python.Python.3.14", "Python.Python.4.0", "Python.Python.4.1"]) is { Id: "Python.Python.4.1", Name: "Python 4.1" },
      "catálogo: salta de rama (3.14 -> 4.1) y no confunde 4.1 con 3.1");
Check(Winget.NewestOf(py, ["Python.Python.3.14", "Python.Python.4"]) is { Id: "Python.Python.4", Name: "Python 4" }, "catálogo: un id con solo \"4\" cuenta como 4.0");

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
