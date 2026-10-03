using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class BoostView : UserControl
{
    public BoostView()
    {
        InitializeComponent();
    }

    private void BtnBoost_Click(object sender, RoutedEventArgs e)
    {
        BtnBoost.IsEnabled = false;
        TxtResult.Text = "Optimizando memoria...";
        var (trimmed, gained, log) = MemoryBoost.Boost();
        TxtResult.Text = $"Listo.\n{log}";
        BtnBoost.IsEnabled = true;
    }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        BtnClean.IsEnabled = false;
        BtnBoost.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        try
        {
            var prog = new Progress<string>(s => TxtResult.Text = s);
            var r = await TempCleaner.QuickCleanAsync(prog);
            TxtResult.Text = $"Limpieza rapida:\n{r.FilesDeleted} archivos borrados\n{TempCleaner.FormatBytes(r.BytesFreed)} liberados\n{r.Errors} bloqueados (en uso, normal)\n{string.Join("\n", r.Log)}";
        }
        catch (Exception ex)
        {
            TxtResult.Text = "Error (no se cerro la app): " + ex.Message;
        }
        finally
        {
            Bar.Visibility = Visibility.Collapsed;
            BtnClean.IsEnabled = true;
            BtnBoost.IsEnabled = true;
        }
    }
}
