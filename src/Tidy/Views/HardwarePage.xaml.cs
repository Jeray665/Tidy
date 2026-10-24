using System.Windows;
using System.Windows.Controls;
using Tidy.Core;

namespace Tidy.Views;

public partial class HardwarePage : Page
{
    static List<HwSection>? _cache;

    public HardwarePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Sections.ItemsSource = _cache ??= await Task.Run(Hardware.Read);
            Loading.Visibility = Visibility.Collapsed;
        };
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_cache == null) return;
        Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, _cache.Select(s =>
            $"{s.Title}: {s.Headline}" + string.Concat(s.Items.Select(i => $"{Environment.NewLine}  {i.Label}: {i.Value}")))));
        Ui.Toast("Copiado", "Resumen del hardware copiado al portapapeles.");
    }
}
