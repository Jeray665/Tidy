using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Tidy.Core;

/// <summary>One exe, three ways to run: installed (Program Files), portable (file name contains "portable": everything
/// next to the exe) or setup (any other copy, e.g. Tidy-Setup.exe in Downloads, offers to install itself).</summary>
public static class Install
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tidy";
    public const string Repo = "https://github.com/Jeray665/Tidy";

    public static string Current => Environment.ProcessPath!;
    public static string Dir => Autostart.InstallDir;
    public static string Exe => Autostart.InstalledExe;
    public static bool IsPortable => Path.GetFileNameWithoutExtension(Current).Contains("portable", StringComparison.OrdinalIgnoreCase);
    public static bool IsInstalled => string.Equals(Path.GetFullPath(Current), Exe, StringComparison.OrdinalIgnoreCase);
    public static Version? InstalledVersion =>
        File.Exists(Exe) && Version.TryParse(FileVersionInfo.GetVersionInfo(Exe).FileVersion, out var v) ? v : null;

    /// <summary>Portable: settings, backups and history live in TidyData next to the exe, so removing that folder
    /// leaves nothing behind. The part an elevated Tidy acts on is locked to administrators.</summary>
    public static void UsePortablePaths()
    {
        var root = Path.Combine(Path.GetDirectoryName(Current)!, "TidyData");
        Store.Dir = root;
        Store.SecureDir = Path.Combine(root, "secure");
        // ponytail: best effort. A portable exe sits in a user-writable folder anyway (malware could swap the exe itself);
        // the installed version is the hardened one. FAT/exFAT USB sticks have no ACLs at all.
        try { LockToAdmins(Store.SecureDir); } catch (Exception e) { Store.Log($"Portable: could not lock {Store.SecureDir}: {e.Message}"); }
    }

    static void LockToAdmins(string dir)
    {
        var di = Directory.CreateDirectory(dir);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.SetOwner(admins);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    /// <summary>Asks a running Tidy (tray included) to close, so its exe can be replaced. Waits up to 10 s.</summary>
    public static bool CloseRunning()
    {
        if (EventWaitHandle.TryOpenExisting(@"Local\Tidy.Quit", out var quit)) using (quit) quit.Set();
        for (var i = 0; i < 100; i++)
        {
            if (!Mutex.TryOpenExisting(@"Local\Tidy.Single", out var m)) return true;
            m.Dispose();
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Copies this exe to Program Files\Tidy and registers it like any installed app. Data folders are left alone,
    /// so reinstalling or updating keeps settings, backups and history.</summary>
    public static void Run(bool desktopShortcut, bool autostart)
    {
        if (!CloseRunning()) throw new IOException("Tidy sigue abierto. Ciérralo desde el icono de la bandeja e inténtalo de nuevo.");
        Directory.CreateDirectory(Dir);
        ReplaceExe(Current, Exe);

        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Tidy.lnk"));
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tidy.lnk");
        if (desktopShortcut) Shortcut(desktop);

        Register();
        if (autostart) Autostart.Enable();
        Store.Log($"Installed {Exe} (desktop shortcut {desktopShortcut}, autostart {autostart})");
    }

    /// <summary>The entry in Settings > Apps > Installed apps. Refreshed on every start so it shows the updated version.</summary>
    public static void Register()
    {
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            var version = typeof(Install).Assembly.GetName().Version!.ToString(3);
            k.SetValue("DisplayName", "Tidy");
            k.SetValue("DisplayVersion", version);
            k.SetValue("Publisher", "Jeray665");
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
            k.SetValue("URLInfoAbout", Repo);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        }
    }

    /// <summary>Swaps an exe even while it's running: the running file can be renamed, just not overwritten.</summary>
    public static void ReplaceExe(string from, string to)
    {
        var old = Path.ChangeExtension(to, ".old.exe");
        var tmp = to + ".new";
        File.Copy(from, tmp, true);
        try { File.Delete(old); } catch { }
        if (File.Exists(to)) File.Move(to, old, true);
        try { File.Move(tmp, to); }
        catch { if (File.Exists(old)) File.Move(old, to); throw; }
    }

    /// <summary>Leftover from an update or reinstall: the previous exe, renamed while it was running.</summary>
    public static void CleanOldExe()
    {
        try { File.Delete(Path.ChangeExtension(Current, ".old.exe")); } catch { }
    }

    /// <summary>Removes the program. Settings and backups stay unless asked, so changes made by Tidy can still be undone
    /// by reinstalling it.</summary>
    public static void Uninstall(bool deleteData)
    {
        CloseRunning();
        Autostart.Disable();
        foreach (var lnk in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Tidy.lnk"),
                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tidy.lnk") })
            try { if (File.Exists(lnk) && IsOurShortcut(lnk)) File.Delete(lnk); } catch { }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        Store.Log($"Uninstalled (data {(deleteData ? "deleted" : "kept")})");
        if (deleteData)
        {
            Cleaner.DeleteTree(Store.SecureDir);
            Cleaner.DeleteTree(Store.Dir);
        }
        // the exe is running (it's us): a hidden cmd deletes it a moment after we exit, then the folder if it's empty
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Process.Start(new ProcessStartInfo(cmd, $"/d /c \"\"{ping}\" -n 4 127.0.0.1 >nul & del /f /q \"{Exe}\" \"{Path.ChangeExtension(Exe, ".old.exe")}\" & rmdir \"{Dir}\"\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory });
    }

    static bool IsOurShortcut(string lnk)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return string.Equals((string)shell.CreateShortcut(lnk).TargetPath, Exe, StringComparison.OrdinalIgnoreCase);
    }

    static void Shortcut(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = Exe;
            sc.WorkingDirectory = Dir;
            sc.Description = "Tidy: optimizador seguro para Windows 11";
            sc.Save();
        }
        catch (Exception e) { Store.Log($"Shortcut {lnk} failed: {e.Message}"); }
    }
}
