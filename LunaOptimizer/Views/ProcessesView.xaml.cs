using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class ProcessesView : UserControl
{
    private System.Windows.Threading.DispatcherTimer? _timer;
    private System.Windows.Threading.DispatcherTimer? _filterTimer;
    private readonly Action<string>? _status;

    public ProcessesView(Action<string>? status = null)
    {
        _status = status;
        InitializeComponent();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // El evento Checked de "Agrupar" se dispara durante InitializeComponent,
        // antes de que existan las columnas del DataGrid: en ese caso no hacemos nada.
        if (ColPids is null || ColName is null || ColRam is null) return;
        try
        {
            var filter = TxtFilter?.Text ?? "";
            bool grouped = ChkGroup?.IsChecked != false;
            // Por defecto solo apps con ventana visible y seguras de cerrar.
            bool onlySafe = !(ChkTodos?.IsChecked == true);
            ColPids.Visibility = grouped ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (grouped)
            {
                ColName.Header = "Programa";
                ColName.Binding = new System.Windows.Data.Binding("Display");
                ColRam.Header = "RAM MB";
                ColRam.Binding = new System.Windows.Data.Binding("TotalRamMB");
                var groups = await Task.Run(() => ProcessService.GetGroups(filter, onlySafe));
                Grid.ItemsSource = groups;
                int totalProcs = groups.Sum(g => g.Count);
                TxtCount.Text = $"{groups.Count} apps ({totalProcs} procesos)";
                ActualizarVacio(groups.Count);
            }
            else
            {
                ColName.Header = "Nombre";
                ColName.Binding = new System.Windows.Data.Binding("Name");
                ColRam.Header = "RAM MB";
                ColRam.Binding = new System.Windows.Data.Binding("RamMB");
                var items = await Task.Run(() => ProcessService.GetProcesses(filter, onlySafe));
                Grid.ItemsSource = items;
                TxtCount.Text = $"{items.Count} procesos";
                ActualizarVacio(items.Count);
            }
        }
        catch (Exception ex)
        {
            // Nunca dejar la lista vacia sin decir por que.
            TxtCount.Text = "Error: " + ex.Message;
        }
    }

    /// Mensaje centrado cuando la lista no tiene nada que mostrar.
    private void ActualizarVacio(int n)
    {
        if (TxtEmpty is null) return;
        if (n > 0)
        {
            TxtEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        string f = (TxtFilter?.Text ?? "").Trim();
        TxtEmpty.Text = f.Length > 0
            ? $"No hay procesos que coincidan con \"{f}\"."
            : "No hay procesos que mostrar.\nActiva \"Ver todos\" para incluir procesos de fondo.";
        TxtEmpty.Visibility = Visibility.Visible;
    }

    public void Refresh() => _ = RefreshAsync();

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer?.Stop();
        _filterTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(300) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Refresh(); };
        _filterTimer.Start();
    }
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void BtnKillRow_Click(object sender, RoutedEventArgs e)
        => KillTarget((sender as FrameworkElement)?.DataContext);

    private void KillTarget(object? target)
    {
        // Modo agrupado: mata todos los PIDs del grupo
        if (target is Models.ProcessGroup grp)
        {
            if (!grp.CanKill)
            {
                MessageBox.Show($"{grp.Name} es del sistema y no se puede cerrar.", "Protegido",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Sin confirmacion: "Finalizar" cierra directamente y el resultado
            // se informa en la barra de estado (no con ventanas modales).
            var (killed, failed) = ProcessService.KillGroup(grp);
            _status?.Invoke($"{grp.Name}: {killed} terminados de {grp.Count}" +
                            (failed > 0 ? $", {failed} fallaron" : "") + ".");
            Refresh();
            return;
        }
        if (target is not Models.ProcessInfo pi)
        {
            MessageBox.Show("Selecciona un proceso primero.");
            return;
        }
        if (!pi.CanKill)
        {
            MessageBox.Show($"{pi.Name} es del sistema y no se puede cerrar.", "Protegido",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (ok, msg) = ProcessService.KillProcess(pi.Pid);
        _status?.Invoke(msg);
        Refresh();
    }

    private void BtnLocation_Click(object sender, RoutedEventArgs e)
    {
        string? path = null;
        if (Grid.SelectedItem is Models.ProcessGroup grp) path = grp.Path;
        else if (Grid.SelectedItem is Models.ProcessInfo info) path = info.Path;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void ChkGroup_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void ChkTodos_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void ChkAuto_Checked(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    private void ChkAuto_Unchecked(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }
}
