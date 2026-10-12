using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

/// <summary>
/// Salud del sistema: muestra el estado de servicios y ajustes que afectan al
/// rendimiento en juegos y deja optimizarlos/restaurarlos uno a uno o todos.
/// Cada accion aplica y RE-escanea para reflejar el estado real, no el supuesto.
/// </summary>
public partial class HealthView : UserControl
{
    private readonly Action<string>? _status;

    public HealthView(Action<string>? status = null)
    {
        InitializeComponent();
        _status = status;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var items = HealthService.Scan();
        List.ItemsSource = items;
        TxtEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int ok = items.Count(i => i.Ok);
        int pct = items.Count == 0 ? 0 : ok * 100 / items.Count;
        TxtScore.Text = $"{ok}/{items.Count} optimizados";
        TxtScore.Foreground = pct >= 80
            ? (System.Windows.Media.Brush)FindResource("B_Green")
            : pct >= 50
                ? (System.Windows.Media.Brush)FindResource("B_Warn")
                : (System.Windows.Media.Brush)FindResource("B_Red");
    }

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HealthItem item) return;
        try
        {
            bool nuevoOk = item.Aplicar?.Invoke() ?? false;
            item.Ok = nuevoOk;
            item.Estado = nuevoOk ? "Optimizado" : "Sin optimizar";
            item.Accion = nuevoOk ? "Restaurar" : "Optimizar";
            _status?.Invoke($"{item.Nombre}: {(nuevoOk ? "optimizado" : "restaurado")}.");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo aplicar: " + ex.Message, "Salud del sistema",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOptimizeAll_Click(object sender, RoutedEventArgs e)
    {
        var items = HealthService.Scan();
        int aplicados = 0;
        foreach (var item in items.Where(i => !i.Ok && i.Aplicar is not null))
        {
            try { if (item.Aplicar!()) aplicados++; }
            catch { }
        }
        Refresh();
        _status?.Invoke($"Salud del sistema: {aplicados} ajustes optimizados.");
        MessageBox.Show($"{aplicados} ajustes optimizados.\n\n" +
            "Algunos servicios se desactivaran por completo al reiniciar.",
            "Optimizar todo", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
