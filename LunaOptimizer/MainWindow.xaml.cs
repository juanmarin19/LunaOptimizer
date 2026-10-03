using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LunaOptimizer.Services;
using LunaOptimizer.Views;

namespace LunaOptimizer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly BoostView _boost;
    private readonly ProcessesView _processes;
    private readonly CleanerView _cleaner;
    private readonly DispatcherTimer _memTimer;

    public MainWindow()
    {
        InitializeComponent();

        _boost = new BoostView();
        _processes = new ProcessesView();
        _cleaner = new CleanerView();

        Host.Content = _boost;

        RefreshMem();
        _memTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _memTimer.Tick += (_, _) => RefreshMem();
        _memTimer.Start();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void NavBoost_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _boost;
        if (TxtStatus is not null) TxtStatus.Text = "Rendimiento.";
    }

    private void NavProc_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _processes;
        _processes.Refresh();
        if (TxtStatus is not null) TxtStatus.Text = "Procesos.";
    }

    private void NavClean_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _cleaner;
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza.";
    }

    private void RefreshMem()
    {
        try
        {
            var (total, free) = ProcessService.GetMemory();
            TxtMem.Text = $"RAM: {free} MB libres de {total} MB";
        }
        catch (Exception ex) { TxtStatus.Text = ex.Message; }
    }
}
