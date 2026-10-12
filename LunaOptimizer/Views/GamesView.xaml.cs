using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Modo juego: en un solo clic cierra apps en segundo plano, libera RAM y
/// purga las listas de memoria. Reutiliza CloseAllApps + MemoryBoost.Boost +
/// QuickCleanAsync: NO hay logica nueva, solo orquesta lo que ya existe.
/// </summary>
public partial class GamesView : UserControl
{
    private readonly Action<string>? _status;

    public GamesView(Action<string>? status = null)
    {
        InitializeComponent();
        _status = status;
    }

    private async void BtnGame_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Preparar para jugar:\n\n" +
            "  - Se cierran tus aplicaciones en segundo plano (navegador, Discord...)\n" +
            "  - Se libera la memoria RAM (working sets, standby, cache)\n" +
            "  - Se borran temporales del sistema\n\n" +
            "Guarda lo que tengas abierto. Continuar?",
            "Modo juego", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        BtnGame.IsEnabled = false;
        BarGame.Value = 0;
        var prog = new Progress<string>(s => TxtGame.Text = s);

        try
        {
            // 1) cerrar apps de fondo (reutiliza el cierre "suave" de la limpieza rapida)
            TxtGame.Text = "Cerrando aplicaciones en segundo plano...";
            BarGame.Value = 15;
            var (closed, failed) = await Task.Run(() => ProcessService.CloseAllApps(prog));

            // 2) liberar RAM (working sets + standby + cache, 5 etapas)
            TxtGame.Text = "Liberando memoria RAM...";
            BarGame.Value = 45;
            var boost = await Task.Run(() => MemoryBoost.Boost());

            // 3) temporales
            TxtGame.Text = "Borrando temporales...";
            BarGame.Value = 75;
            var r = await TempCleaner.QuickCleanAsync(prog);

            BarGame.Value = 100;
            long baja = Math.Max(0, boost.usoAntesMB - boost.usoDespuesMB);
            string resumen =
                $"Apps cerradas: {closed}{(failed > 0 ? $" ({failed} protegidas)" : "")}\n" +
                $"RAM en uso: {boost.usoAntesMB / 1024.0:F1} GB -> {boost.usoDespuesMB / 1024.0:F1} GB (-{baja / 1024.0:F1} GB)\n" +
                $"Temporales: {r.FilesDeleted} archivos ({TempCleaner.FormatBytes(r.BytesFreed)})\n" +
                $"Listo para jugar. ({DateTime.Now:HH:mm})";
            TxtResumen.Text = resumen;
            TxtGame.Text = "Equipo preparado.";
            _status?.Invoke($"Modo juego: -{baja / 1024.0:F1} GB de RAM, {r.FilesDeleted} temporales.");
        }
        catch (Exception ex)
        {
            TxtGame.Text = "Error: " + ex.Message;
        }
        finally
        {
            BtnGame.IsEnabled = true;
        }
    }
}
