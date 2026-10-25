namespace Tidy.Core;

public enum HistoryKind { Tweak, Service, Task, Network, Uninstall, Clean, Files, App, Power, Startup, Game }

/// <param name="Undo">What undoes it: "tweak:id", "service:name", "task:path", "dns", "hosts:copy", "quarantine:id", "winget:id", "recycle", "power:guid", "powertime:id:ac:dc", "startup", "startup:key|0/1", "policies:hive|key|name|value;…". Null = nothing to undo.</param>
public record HistoryEntry(DateTime When, HistoryKind Kind, string Title, string Detail, string? Undo);

/// <summary>
/// Timeline of every change Tidy makes. Kept in the admin-only folder: an entry decides what an "undo" click does,
/// so a normal-user process must not be able to plant one.
/// </summary>
public static class History
{
    const string FileName = "history.json";
    const int Max = 1000;
    static readonly object _lock = new();

    public static void Add(HistoryKind kind, string title, string detail = "", string? undo = null)
    {
        try
        {
            lock (_lock)
            {
                var all = All();
                all.Insert(0, new HistoryEntry(DateTime.Now, kind, title, detail, undo));
                if (all.Count > Max) all.RemoveRange(Max, all.Count - Max);
                Store.WriteJson(FileName, all, secure: true);
            }
        }
        catch (Exception e) { Store.Log($"History: could not save: {e.Message}"); } // never block the change itself
    }

    public static List<HistoryEntry> All() => Store.ReadJson<List<HistoryEntry>>(FileName, secure: true) ?? [];

    /// <summary>Oldest change ever recorded: the "before Tidy" line on the boot-time chart.</summary>
    public static DateTime? FirstChange() => All().LastOrDefault()?.When;
}
