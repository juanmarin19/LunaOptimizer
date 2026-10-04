using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class ProcessesView : UserControl
{
    private System.Windows.Threading.DispatcherTimer? _timer;

    public ProcessesView()
    {
        InitializeComponent();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // El evento Checked de "Agrupar" se dispara durante InitializeComponent,
        // antes de que existan las columnas del DataGrid: en ese caso no hacemos nada.
        if (ColPids is null || ColName is null || ColRam is null) return;
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
        }
    }

    public void Refresh() => _ = RefreshAsync();

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void BtnKill_Click(object sender, RoutedEventArgs e)
    {
        // Modo agrupado: mata todos los PIDs del grupo
        if (Grid.SelectedItem is Models.ProcessGroup grp)
        {
            if (!grp.CanKill)
            {
                MessageBox.Show($"{grp.Name} es del sistema y no se puede cerrar.", "Protegido",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = MessageBox.Show($"Terminar {grp.Display} ({grp.Count} procesos, {grp.TotalRamMB} MB)?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            var (killed, failed) = ProcessService.KillGroup(grp);
            MessageBox.Show($"{grp.Name}: {killed} terminados, {failed} fallaron.");
            Refresh();
            return;
        }
        if (Grid.SelectedItem is not Models.ProcessInfo pi)
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
            var r2 = MessageBox.Show($"Terminar {pi.Name} ({pi.Pid})?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r2 != MessageBoxResult.Yes) return;
        var (ok, msg) = ProcessService.KillProcess(pi.Pid);
        MessageBox.Show(msg);
        Refresh();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is Models.ProcessGroup grp)
            BtnKill.IsEnabled = grp.CanKill;
        else if (Grid.SelectedItem is Models.ProcessInfo pi)
            BtnKill.IsEnabled = pi.CanKill;
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
