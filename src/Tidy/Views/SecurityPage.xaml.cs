using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Tidy.Views;

public partial class SecurityPage : Page
{
    public SecurityPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
    }

    async void Recheck_Click(object sender, RoutedEventArgs e) => await Refresh();

    async Task Refresh()
    {
        RecheckBtn.IsEnabled = false;
        HeroTitle.Text = "Comprobando…";
        var checks = await Task.Run(SecurityStatus.Run);
        RecheckBtn.IsEnabled = true;

        var bad = checks.Count(c => c.State == CheckState.Bad);
        var warn = checks.Count(c => c.State == CheckState.Warning);
        HeroTitle.Text = bad > 0 ? $"{bad + warn} cosa(s) que arreglar" : warn > 0 ? $"{warn} cosa(s) que revisar" : "Todo en orden";
        HeroSub.Text = $"{checks.Count(c => c.State == CheckState.Ok)} de {checks.Count} comprobaciones correctas." +
                       (checks.Any(c => c.State == CheckState.Info) ? " Las marcadas con ⓘ son opcionales." : "");
        HeroBadge.Background = Ui.Res(bad > 0 ? "SystemFillColorCriticalBrush" : warn > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        HeroIcon.Symbol = bad + warn > 0 ? SymbolRegular.ShieldError24 : SymbolRegular.ShieldCheckmark24;

        Checks.Children.Clear();
        foreach (var c in checks.OrderBy(c => c.State switch { CheckState.Bad => 0, CheckState.Warning => 1, CheckState.Info => 2, CheckState.Unknown => 3, _ => 4 }))
            Checks.Children.Add(Row(c));
    }

    static UIElement Row(SecurityCheck c)
    {
        var (icon, brush) = StateIcon(c.State);
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        if (c.Advice != "") header.Children.Add(Ui.Text(c.Advice, "Muted", 0));
        var card = new CardControl
        {
            Icon = new SymbolIcon(icon) { FontSize = 22, Foreground = Ui.Res(brush) },
            Header = header, Margin = new Thickness(0, 0, 0, 6),
        };
        if (c.Fix != null)
        {
            var btn = new Wpf.Ui.Controls.Button { Content = c.FixLabel ?? "Abrir" };
            btn.Click += (_, _) => OpenFix(c.Fix);
            card.Content = btn;
        }
        return card;
    }

    public static (SymbolRegular, string) StateIcon(CheckState s) => s switch
    {
        CheckState.Ok => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
        CheckState.Warning => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
        CheckState.Bad => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
        CheckState.Info => (SymbolRegular.Info24, "AccentTextFillColorPrimaryBrush"),
        _ => (SymbolRegular.QuestionCircle24, "TextFillColorTertiaryBrush"),
    };

    /// <summary>Opens where a check gets fixed: a Tidy page, a Windows settings/Defender URI or a System32 tool. Never runs anything else.</summary>
    static void OpenFix(string fix)
    {
        if (fix.EndsWith("Page") && typeof(App).Assembly.GetType($"Tidy.Views.{fix}") is { } page) MainWindow.Current?.Navigate(page);
        else if (fix.Contains('|')) { var p = fix.Split('|'); Process.Start(new ProcessStartInfo(p[0], p[1]) { UseShellExecute = false }); }
        else if (fix.EndsWith(".exe")) Ui.RunSystemTool(fix);
        else Ui.OpenSettings(fix);
    }
}
