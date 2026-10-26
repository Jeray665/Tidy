using System.IO;
using System.Windows;
using System.Windows.Controls;
using Tidy.Core;
using Wpf.Ui.Controls;

namespace Tidy.Views;

public partial class VerifyPage : Page
{
    string? _file;
    bool _busy;

    public VerifyPage() => InitializeComponent();

    void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = I18n.T("Archivo a comprobar"), InitialDirectory = Storage.Downloads };
        if (dlg.ShowDialog() != true) return;
        _file = dlg.FileName;
        var info = new FileInfo(_file);
        FileName.Text = info.Name;
        FileInfoText.Text = $"{Store.FormatBytes(info.Length)} · {info.DirectoryName}";
        Result.Children.Clear();
        Update();
    }

    void Expected_Changed(object sender, TextChangedEventArgs e)
    {
        var text = Expected.Text.Trim();
        ExpectedKind.Text = text == "" ? "" : FileCheck.AlgorithmOf(text) is { } a ? $"Huella {a}" : "Esto no parece una huella: deben ser solo letras de la a a la f y números.";
        Update();
    }

    // without a published fingerprint it still shows the SHA-256 and the signature, to compare by eye or search
    void Update() => CheckBtn.IsEnabled = _file != null && !_busy && (Expected.Text.Trim() == "" || FileCheck.AlgorithmOf(Expected.Text) != null);

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null || _busy) return;
        if (!File.Exists(_file)) { Ui.Toast("Ya no está", "El archivo se ha movido o borrado.", ControlAppearance.Caution); return; }
        _busy = true;
        Update();
        Result.Children.Clear();
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        var expected = Expected.Text.Trim();
        var algorithm = expected == "" ? "SHA-256" : FileCheck.AlgorithmOf(expected)!;
        try
        {
            var file = _file;
            var progress = new Progress<double>(p => Progress.Value = p); // made here, so it reports on this (UI) thread
            var hash = await Task.Run(() => FileCheck.Hash(file, algorithm, progress));
            var signed = FileCheck.IsSignable(file) ? await Task.Run(() => FileCheck.HasValidSignature(file)) : (bool?)null;

            if (expected != "")
            {
                var ok = FileCheck.Matches(hash, expected);
                Result.Children.Add(Card(ok ? "CheckmarkCircle24" : "ErrorCircle24", ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush",
                    ok ? "Es el archivo auténtico" : "No coincide: NO es el archivo que publica la web",
                    ok ? $"La huella {algorithm} es idéntica a la de la web: el archivo no ha cambiado ni un byte."
                       : "Puede estar incompleto, dañado o haber sido cambiado por otro. No lo abras: bórralo y descárgalo otra vez desde la web oficial."));
                if (algorithm is "MD5" or "SHA-1")
                    Result.Children.Add(Ui.Text($"{algorithm} es una huella antigua: sirve para ver si la descarga está completa, pero no protege de alguien que la cambie a propósito. Si la web da SHA-256, usa esa.", "Muted", 6));
            }
            if (signed is { } s)
                Result.Children.Add(Card(s ? "ShieldCheckmark24" : "ShieldError24", s ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush",
                    s ? "Firma digital válida" : "Sin firma digital válida",
                    s ? "Windows confirma que el programa lleva la firma de su fabricante y que no se ha modificado desde que lo firmó."
                      : "Muchos programas pequeños no la llevan, pero los de empresas conocidas sí. Si esperabas un programa de una marca conocida, desconfía."));
            var hashBox = new Wpf.Ui.Controls.TextBox { Text = hash, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0) };
            Result.Children.Add(Ui.Text($"Huella {algorithm} del archivo:", "Muted", 0));
            Result.Children.Add(hashBox);
            Store.Log($"FileCheck: {file} {algorithm} {hash}{(expected != "" ? FileCheck.Matches(hash, expected) ? " matches" : " DOES NOT match" : "")}{(signed is { } sg ? $", signature {(sg ? "valid" : "missing/invalid")}" : "")}");
        }
        catch (Exception ex) { Result.Children.Add(Ui.Text($"No se pudo leer el archivo: {ex.Message}", null, 0)); }
        finally
        {
            _busy = false;
            Progress.Visibility = Visibility.Collapsed;
            Update();
        }
    }

    static CardControl Card(string icon, string brush, string title, string text)
    {
        var header = new StackPanel();
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        header.Children.Add(Ui.Text(text, "Muted", 0));
        return new CardControl { Icon = new SymbolIcon(Enum.Parse<SymbolRegular>(icon)) { FontSize = 26, Foreground = Ui.Res(brush) }, Header = header, Margin = new Thickness(0, 0, 0, 8) };
    }
}
