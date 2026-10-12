using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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
    private readonly GamesView _games;
    private readonly HealthView _health;
    private readonly AppsView _apps;
    private readonly DispatcherTimer _memTimer;

    public MainWindow()
    {
        InitializeComponent();

        _home = new HomeView(Navigate, s => TxtStatus.Text = s);
        _processes = new ProcessesView(s => TxtStatus.Text = s);
        _cleaner = new CleanerView(Navigate);
        _deep = new DeepCleanView(Navigate, s => TxtStatus.Text = s);
        _games = new GamesView(s => TxtStatus.Text = s);
        _health = new HealthView(s => TxtStatus.Text = s);
        _apps = new AppsView(s => TxtStatus.Text = s);

        Host.Content = _home;

        // Esquinas redondeadas (Win11) y modo oscuro/claro del marco DWM
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            MicaHelper.TryEnable(hwnd);
            ThemeManager.Registrar(this, RootBorder, hwnd);
            ActualizarIconoTema();
        };

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = "v" + (v is null ? "1.1.0" : $"{v.Major}.{v.Minor}.{v.Build}");
        }
        catch { TxtVersion.Text = "v1.1.0"; }

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

    /// Cambio claro/oscuro (boton de la barra de titulo).
    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        ActualizarIconoTema();
    }

    private void ActualizarIconoTema()
    {
        try
        {
            ThemeIcon.Data = (System.Windows.Media.Geometry)FindResource(
                ThemeManager.Oscuro ? "I.Moon" : "I.Sun");
        }
        catch { }
    }

    /// Transicion suave al cambiar de pantalla.
    private void AnimateIn()
    {
        try
        {
            Host.BeginAnimation(UIElement.OpacityProperty, null);
            var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Host.BeginAnimation(UIElement.OpacityProperty, a);
        }
        catch { }
    }

    /// Cierre del todo: al cerrar la ventana, el proceso muere aunque quede
    /// algun hilo o ventana residual en segundo plano.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { Application.Current.Shutdown(); } catch { }
        Environment.Exit(0);
    }

    /// Navegacion desde las tarjetas del Inicio y desde Limpieza.
    private void Navigate(string target)
    {
        switch (target)
        {
            case "proc": NavProc.IsChecked = true; break;
            case "clean": NavClean.IsChecked = true; break;
            case "games": NavGames.IsChecked = true; break;
            case "health": NavHealth.IsChecked = true; break;
            case "apps": NavApps.IsChecked = true; break;
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
        NavGames.IsChecked = false;
        NavHealth.IsChecked = false;
        NavApps.IsChecked = false;
        _deep.BackTarget = back;
        Host.Content = _deep;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza profunda";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza profunda.";
        _deep.OnShown();
        AnimateIn();
    }

    private void NavHome_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _home;
        if (TxtTitle is not null) TxtTitle.Text = "Inicio";
        if (TxtStatus is not null) TxtStatus.Text = "Inicio.";
        _home.Refresh();
        AnimateIn();
    }

    private void NavProc_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _processes;
        _processes.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Procesos";
        if (TxtStatus is not null) TxtStatus.Text = "Procesos.";
        AnimateIn();
    }

    private void NavClean_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _cleaner;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza.";
        AnimateIn();
    }

    private void NavGames_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _games;
        if (TxtTitle is not null) TxtTitle.Text = "Juegos";
        if (TxtStatus is not null) TxtStatus.Text = "Modo juego.";
        AnimateIn();
    }

    private void NavHealth_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _health;
        _health.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Salud";
        if (TxtStatus is not null) TxtStatus.Text = "Salud del sistema.";
        AnimateIn();
    }

    private void NavApps_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = _apps;
        _apps.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Apps";
        if (TxtStatus is not null) TxtStatus.Text = "Apps instaladas.";
        AnimateIn();
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
