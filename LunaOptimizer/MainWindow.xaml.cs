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
    // Vistas creadas bajo demanda (lazy): no se instancia una pestana que el
    // usuario nunca abre, asi el arranque y el primer pintado son mas rapidos.
    private HomeView? _home;
    private ProcessesView? _processes;
    private CleanerView? _cleaner;
    private DeepCleanView? _deep;
    private GamesView? _games;
    private HealthView? _health;
    private AppsView? _apps;

    private HomeView Home => _home ??= new HomeView(Navigate, s => TxtStatus.Text = s);
    private ProcessesView Processes => _processes ??= new ProcessesView(s => TxtStatus.Text = s);
    private CleanerView Cleaner => _cleaner ??= new CleanerView(Navigate);
    private DeepCleanView Deep => _deep ??= new DeepCleanView(Navigate, s => TxtStatus.Text = s);
    private GamesView Games => _games ??= new GamesView(s => TxtStatus.Text = s);
    private HealthView Health => _health ??= new HealthView(s => TxtStatus.Text = s);
    private AppsView Apps => _apps ??= new AppsView(s => TxtStatus.Text = s);

    private readonly DispatcherTimer _memTimer;

    public MainWindow()
    {
        InitializeComponent();

        Host.Content = Home;

        // Esquinas redondeadas (Win11) y modo oscuro/claro del marco DWM
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            MicaHelper.TryEnable(hwnd);
            ThemeManager.Registrar(this, RootBorder, hwnd);
            ActualizarIconoTema();
        };

        // La version sale SOLO del ensamblado (que la toma de <Version> en el csproj):
        // asi no hay que mantener el numero en varios lugares.
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = v is null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { TxtVersion.Text = "v?"; }

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

    /// Cierre del todo. Se hace en OnClosing, ANTES de destruir la ventana, para
    /// no pasar por el apagado interno de WPF: en un exe de archivo unico su
    /// telemetria (ControlsTraceLogger.LogUsedControlsDetails) intenta cargar
    /// System.Diagnostics.Tracing, lanza FileNotFoundException y aborta el cierre.
    /// Resultado: la ventana desaparecia pero el PROCESO quedaba vivo y sin
    /// interfaz, reteniendo el mutex de instancia unica -> la app ya no volvia a
    /// abrirse nunca mas (habia que matarla desde el Administrador de tareas).
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        App.Cerrando = true;
        Environment.Exit(0);
    }

    /// Respaldo por si el cierre no pasa por OnClosing.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.Cerrando = true;   // a partir de aqui, no registrar errores de apagado
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
        Deep.BackTarget = back;
        Host.Content = Deep;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza profunda";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza profunda.";
        Deep.OnShown();
        AnimateIn();
    }

    private void NavHome_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Home;
        if (TxtTitle is not null) TxtTitle.Text = "Inicio";
        if (TxtStatus is not null) TxtStatus.Text = "Inicio.";
        Home.Refresh();
        AnimateIn();
    }

    private void NavProc_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Processes;
        Processes.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Procesos";
        if (TxtStatus is not null) TxtStatus.Text = "Procesos.";
        AnimateIn();
    }

    private void NavClean_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Cleaner;
        if (TxtTitle is not null) TxtTitle.Text = "Limpieza";
        if (TxtStatus is not null) TxtStatus.Text = "Limpieza.";
        AnimateIn();
    }

    private void NavGames_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Games;
        if (TxtTitle is not null) TxtTitle.Text = "Juegos";
        if (TxtStatus is not null) TxtStatus.Text = "Modo juego.";
        AnimateIn();
    }

    private void NavHealth_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Health;
        Health.Refresh();
        if (TxtTitle is not null) TxtTitle.Text = "Salud";
        if (TxtStatus is not null) TxtStatus.Text = "Salud del sistema.";
        AnimateIn();
    }

    private void NavApps_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Host.Content = Apps;
        Apps.Refresh();
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
