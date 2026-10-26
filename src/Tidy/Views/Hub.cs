using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

/// <summary>
/// One sidebar entry that groups several pages: a row of tabs over the existing page, shown untouched in its own frame.
/// The pages keep all their behaviour; only where they live in the menu changes.
/// </summary>
public abstract class Hub : Page
{
    static readonly Dictionary<Type, (string Name, SymbolRegular Icon, Type Page)[]> Groups = new()
    {
        [typeof(CleanHub)] = [("Limpieza", SymbolRegular.Broom24, typeof(CleanerPage)), ("Almacenamiento", SymbolRegular.DataPie24, typeof(StoragePage))],
        [typeof(SpeedHub)] = [("Rendimiento", SymbolRegular.TopSpeed24, typeof(PerformancePage)), ("Inicio de Windows", SymbolRegular.Rocket24, typeof(StartupPage)),
            ("Optimizaciones", SymbolRegular.Wand24, typeof(TweaksPage))],
        [typeof(AppsHub)] = [("Aplicaciones", SymbolRegular.AppsList24, typeof(AppsPage)), ("Drivers", SymbolRegular.ArrowSync24, typeof(DriversPage))],
        [typeof(SafetyHub)] = [("Seguridad", SymbolRegular.Shield24, typeof(SecurityPage)), ("Red", SymbolRegular.Globe24, typeof(NetworkPage))],
        [typeof(AdvancedHub)] = [("Servicios y tareas", SymbolRegular.Settings24, typeof(ServicesPage)), ("Herramientas", SymbolRegular.WrenchScrewdriver24, typeof(ToolsPage)),
            ("Hardware", SymbolRegular.DeveloperBoard24, typeof(HardwarePage))],
        [typeof(UtilsHub)] = [("Imágenes", SymbolRegular.Image24, typeof(ImagesPage)), ("Comprobar archivo", SymbolRegular.ShieldCheckmark24, typeof(VerifyPage))],
        [typeof(HistoryHub)] = [("Historial", SymbolRegular.History24, typeof(HistoryPage)), ("Copias de seguridad", SymbolRegular.ShieldCheckmark24, typeof(BackupsPage))],
    };

    /// <summary>The hub a page lives in, or null if it has its own sidebar entry.</summary>
    public static Type? Of(Type page) => Groups.FirstOrDefault(g => g.Value.Any(t => t.Page == page)).Key;

    /// <summary>Tab to open when the next hub is built (set right before navigating to it).</summary>
    public static Type? Pending { get; set; }
    public static Hub? Shown { get; private set; }

    readonly (string Name, SymbolRegular Icon, Type Page)[] _tabs;
    readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal };
    readonly Frame _frame = new() { NavigationUIVisibility = NavigationUIVisibility.Hidden, JournalOwnership = JournalOwnership.OwnsJournal, Focusable = false };
    Type? _current;

    protected Hub()
    {
        _tabs = Groups[GetType()];
        Shown = this;
        var strip = new Border
        {
            Child = _bar, Padding = new Thickness(4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(36, 20, 36, 0),
        };
        strip.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        strip.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        foreach (var t in _tabs)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new SymbolIcon { Symbol = t.Icon, Margin = new Thickness(0, 0, 8, 0) });
            label.Children.Add(new TextBlock { Text = t.Name });
            var btn = new Wpf.Ui.Controls.Button { Content = label, Tag = t.Page, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(0) };
            btn.Click += (_, _) => Select(t.Page);
            _bar.Children.Add(btn);
        }
        // the frame cuts color inheritance, as the main one does
        _frame.Navigated += (_, e) => (e.Content as FrameworkElement)?.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        Content = new StackPanel { Children = { strip, _frame } };
        Select(_tabs.Any(t => t.Page == Pending) ? Pending! : _tabs[0].Page);
        Pending = null;
    }

    public void Select(Type page)
    {
        if (page == _current || _tabs.All(t => t.Page != page)) return;
        _current = page;
        foreach (Wpf.Ui.Controls.Button b in _bar.Children)
            b.Appearance = (Type)b.Tag == page ? ControlAppearance.Primary : ControlAppearance.Transparent;
        _frame.Navigate(Activator.CreateInstance(page));
    }
}

public sealed class CleanHub : Hub;
public sealed class SpeedHub : Hub;
public sealed class AppsHub : Hub;
public sealed class SafetyHub : Hub;
public sealed class AdvancedHub : Hub;
public sealed class UtilsHub : Hub;
public sealed class HistoryHub : Hub;
