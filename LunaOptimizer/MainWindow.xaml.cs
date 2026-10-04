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
    private readonly HomeView _home;
    private readonly ProcessesView _processes;
    private readonly CleanerView _cleaner;
    private readonly DeepCleanView _deep;
    private readonly DispatcherTimer _memTimer;

    public MainWindow()
    {
        InitializeComponent();

        _home = new HomeView(Navigate, s => TxtStatus.Text = s);
        _processes = new ProcessesView();
        _cleaner = new CleanerView(Navigate);
        _deep = new DeepCleanView(Navigate, s => TxtStatus.Text = s);

        Host.Content = _home;

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

    /// Navegacion desde las tarjetas del Inicio y desde Limpieza.
    private void Navigate(string target)
    {
        switch (target)
        {
            case "proc": NavProc.IsChecked = true; break;
            case "clean": NavClean.IsChecked = true; break;
            case "deep": ShowDeep("home"); break;
            case "deepclean": ShowDeep("clean"); break;
            default: NavHome.IsChecked = true; break;
        }
    }

    /// Pantalla de limpieza profunda estilo PC Manager: sin ningun radio
    /// marcado, para que cualquier clic del rail dispare su Checked y salga.
    private void ShowDeep(string back)
    {
        NavHome.IsChecked = false;
        NavProc.IsChecked = false;
        NavClean.IsChecked = false;
        _deep.BackTarget = back;
        Host.Content = _deep;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza profunda";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza profunda.";
        _deep.OnShown();
    }

    private void NavHome_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _home;
        if (TxtTitle is not null) TxtTitle.Text = "Inicio";
        if (TxtStatus is not null) TxtStatus.Text = "Inicio.";
        _home.Refresh();
    }

    private void NavProc_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _processes;
        _processes.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Procesos";
        if (TxtStatus is not null) TxtStatus.Text = "Procesos.";
    }

    private void NavClean_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _cleaner;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza.";
    }

    private void RefreshMem()
    {
        try
        {
            var (total, free) = ProcessService.GetMemory();
            if (TxtSub is not null) TxtSub.Text = $"RAM: {free} MB libres de {total} MB";
            _home?.UpdateMemory();
        }
        catch (Exception ex) { if (TxtStatus is not null) TxtStatus.Text = ex.Message; }
    }
}
