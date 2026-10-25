using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Tidy;

/// <summary>One in-app notification. The same title+message again doesn't stack a copy: it bumps "×N" and restarts the timer.</summary>
public sealed class Toast : Observable
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required ControlAppearance Kind { get; init; }
    public int Count { get; private set; } = 1;
    public string CountText => $"×{Count}";
    public Visibility CountVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public SymbolRegular Symbol => Kind switch
    {
        ControlAppearance.Danger => SymbolRegular.ErrorCircle24,
        ControlAppearance.Caution => SymbolRegular.Warning24,
        ControlAppearance.Info => SymbolRegular.Info24,
        _ => SymbolRegular.CheckmarkCircle24,
    };
    public Brush Color => Ui.Res(Kind switch
    {
        ControlAppearance.Danger => "SystemFillColorCriticalBrush",
        ControlAppearance.Caution => "SystemFillColorCautionBrush",
        ControlAppearance.Info => "AccentTextFillColorPrimaryBrush",
        _ => "SystemFillColorSuccessBrush",
    });
    internal readonly DispatcherTimer Timer = new();
    internal string Key => $"{Kind}|{Title}|{Message}";
    internal void Bump() { Count++; Changed(); }
}

/// <summary>Bottom-right stack of notifications: up to 3 at once, oldest leaves first, errors stay longer,
/// hovering keeps them open.</summary>
public static class Toasts
{
    const int MaxVisible = 3;
    public static ObservableCollection<Toast> Items { get; } = [];

    public static void Show(string title, string message, ControlAppearance kind)
    {
        if (Items.FirstOrDefault(t => t.Key == $"{kind}|{title}|{message}") is { } same)
        {
            same.Bump();
            Restart(same);
            return;
        }
        var t = new Toast { Title = title, Message = message, Kind = kind };
        t.Timer.Interval = TimeSpan.FromSeconds(kind is ControlAppearance.Danger or ControlAppearance.Caution ? 10 : 6);
        t.Timer.Tick += (_, _) => Close(t);
        Items.Add(t);
        while (Items.Count > MaxVisible) Close(Items[0]);
        t.Timer.Start();
    }

    public static void Close(Toast t)
    {
        t.Timer.Stop();
        Items.Remove(t);
    }

    public static void Pause(Toast t) => t.Timer.Stop();
    public static void Restart(Toast t) { t.Timer.Stop(); t.Timer.Start(); }
}
