using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Tidy.Core;

/// <summary>Tidy's own autostart: copied to Program Files (admin-only writable) and started elevated by a logon task.</summary>
public static class Autostart
{
    const string TaskName = "Tidy";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tidy");
    public static string InstalledExe => Path.Combine(InstallDir, "Tidy.exe");

    public static bool IsEnabled => Store.Run("schtasks.exe", $"/query /tn \"{TaskName}\"").Exit == 0;

    public static bool Enable()
    {
        // An elevated logon task must never point at a user-writable exe (anything could replace it and get admin silently),
        // so it only ever starts the installed copy in Program Files. Portable Tidy has no autostart.
        if (Install.IsPortable || !File.Exists(InstalledExe)) { Store.Log("Autostart needs the installed version"); return false; }
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Tidy: optimizador en la bandeja del sistema</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(InstalledExe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        // admin-only folder: a user-level process must not be able to swap the XML before schtasks reads it
        var tmp = Path.Combine(InstallDir, "task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        var (code, output) = Store.Run("schtasks.exe", $"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
        File.Delete(tmp);
        Store.Log($"Autostart enabled ({InstalledExe}) -> {code} {output.Trim()}");
        return code == 0;
    }

    public static bool Disable()
    {
        var (code, output) = Store.Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
        Store.Log($"Autostart disabled -> {code} {output.Trim()}");
        return code == 0;
    }
}

public class StartupApp
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public string Publisher { get; init; } = "";
    public string? ExePath { get; init; }
    public bool Enabled { get; set; }
    public required RegistryKey ApprovedHive { get; init; }
    public required string ApprovedKey { get; init; }
    public required string ApprovedName { get; init; }
}

/// <summary>
/// Same mechanism as Task Manager's "Startup apps" tab: we only flip the StartupApproved flag,
/// never delete the Run entry or the shortcut, so anything can be re-enabled later from here or Task Manager.
/// </summary>
public static class StartupApps
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> List()
    {
        var list = new List<StartupApp>();
        var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        AddRun(list, hkcu, hkcu, "Run", "Usuario");
        AddRun(list, hklm, hklm, "Run", "Todos los usuarios");
        AddRun(list, hklm32, hklm, "Run32", "Todos los usuarios (32 bits)");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), hkcu, "Carpeta Inicio");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), hklm, "Carpeta Inicio (todos)");
        return list.OrderByDescending(a => a.Enabled).ThenBy(a => a.Name).ToList();
    }

    static void AddRun(List<StartupApp> list, RegistryKey runHive, RegistryKey approvedHive, string approvedKey, string where)
    {
        using var run = runHive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (run == null) return;
        foreach (var name in run.GetValueNames())
        {
            if (run.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            var exe = ExeFromCommand(cmd);
            list.Add(new StartupApp
            {
                Name = Pretty(name, exe), Command = cmd, Location = where, ExePath = exe, Publisher = Publisher(exe),
                Enabled = IsApproved(approvedHive, approvedKey, name),
                ApprovedHive = approvedHive, ApprovedKey = approvedKey, ApprovedName = name,
            });
        }
    }

    static void AddFolder(List<StartupApp> list, string folder, RegistryKey approvedHive, string where)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f);
            list.Add(new StartupApp
            {
                Name = Path.GetFileNameWithoutExtension(f), Command = f, Location = where, ExePath = f,
                Enabled = IsApproved(approvedHive, "StartupFolder", name),
                ApprovedHive = approvedHive, ApprovedKey = "StartupFolder", ApprovedName = name,
            });
        }
    }

    static bool IsApproved(RegistryKey hive, string key, string name)
    {
        using var k = hive.OpenSubKey(Approved + key);
        // missing = enabled; first byte even (02/06) = enabled, odd (03) = disabled
        return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static void SetEnabled(StartupApp app, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        using var k = app.ApprovedHive.CreateSubKey(Approved + app.ApprovedKey, true);
        k.SetValue(app.ApprovedName, data, RegistryValueKind.Binary);
        app.Enabled = enabled;
        Store.Log($"Windows startup: {(enabled ? "enabled" : "disabled")} '{app.Name}' ({app.Command})");
        History.Add(HistoryKind.Startup, $"Inicio de Windows: {app.Name} {(enabled ? "activado" : "desactivado")}", "", "startup");
    }

    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); return end > 1 ? cmd[1..end] : null; }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
    }

    static string Publisher(string? exe)
    {
        try { return exe != null && File.Exists(exe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "" : ""; }
        catch { return ""; }
    }

    static string Pretty(string valueName, string? exe)
    {
        try
        {
            var desc = exe != null && File.Exists(exe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription : null;
            return string.IsNullOrWhiteSpace(desc) ? valueName : desc.Trim();
        }
        catch { return valueName; }
    }
}
