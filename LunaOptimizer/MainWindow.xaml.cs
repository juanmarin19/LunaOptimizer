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
/// Ventana principal: barra de titulo propia, rail de navegacion estilo PC Manager
/// y un unico host de contenido. Las vistas se crean bajo demanda (lazy) para que
/// el arranque sea inmediato.
/// </summary>
public partial class MainWindow : Window
{
    private HomeView? _home;
    private ProcessesView? _processes;
    private CleanerView? _cleaner;
    private DeepCleanView? _deep;
    private GamesView? _games;
    private HealthView? _health;
    private AppsView? _apps;
    private ProtectionView? _protection;
    private StorageView? _storage;
    private StartupView? _startup;
    private ToolboxView? _toolbox;
    private SettingsView? _settings;

    private HomeView Home => _home ??= new HomeView(Navigate, SetStatus);
    private ProcessesView Processes => _processes ??= new ProcessesView(SetStatus);
    private CleanerView Cleaner => _cleaner ??= new CleanerView(Navigate);
    private DeepCleanView Deep => _deep ??= new DeepCleanView(Navigate, SetStatus);
    private GamesView Games => _games ??= new GamesView(Navigate, SetStatus);
    private HealthView Health => _health ??= new HealthView(Navigate, SetStatus);
    private AppsView Apps => _apps ??= new AppsView(SetStatus);
    private ProtectionView Protection => _protection ??= new ProtectionView(Navigate, SetStatus);
    private StorageView Storage => _storage ??= new StorageView(Navigate, SetStatus);
    private StartupView Startup => _startup ??= new StartupView(Navigate, SetStatus);
    private ToolboxView Toolbox => _toolbox ??= new ToolboxView(Navigate);
    private SettingsView Settings => _settings ??= new SettingsView(Navigate, SetStatus, ApplyTheme);

    private readonly DispatcherTimer _memTimer;
    private readonly DispatcherTimer _smartTimer;
    private bool _smartBusy;
    private DateTime _ultimoSmart = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        Host.Content = Home;
        // CULTURA_DBG
        try { TxtVersion.Text = System.Globalization.CultureInfo.CurrentCulture.Name + " " + (59.66).ToString("F2"); } catch { }

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            MicaHelper.TryEnable(hwnd);
            ThemeManager.Registrar(this, RootBorder, hwnd);
            UpdateThemeIcon();
        };

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = v is null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { TxtVersion.Text = "v?"; }

        // Pagina inicial opcional (utilidad de soporte y pruebas):
        //   LunaOptimizer.exe --page=protect
        try
        {
            var arg = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (arg is not null) Navigate(arg["--page=".Length..]);
        }
        catch { }

        _memTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _memTimer.Tick += (_, _) => { try { _home?.UpdateMemory(); } catch { } };
        _memTimer.Start();

        // "Smart Boost" de PC Manager: optimiza solo cuando el equipo lo necesita.
        _smartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _smartTimer.Tick += (_, _) => _ = SmartBoostTickAsync();
        _smartTimer.Start();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnTheme_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    public void ApplyTheme(bool oscuro)
    {
        ThemeManager.Set(oscuro);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        try
        {
            ThemeIcon.Data = (System.Windows.Media.Geometry)FindResource(
                ThemeManager.Oscuro ? "I.Sun" : "I.Moon");
        }
        catch { }
    }

    public void SetStatus(string texto)
    {
        try
        {
            if (Dispatcher.CheckAccess()) TxtStatus.Text = texto;
            else Dispatcher.BeginInvoke(() => TxtStatus.Text = texto);
        }
        catch { }
    }

    private void AnimateIn()
    {
        try
        {
            Host.BeginAnimation(UIElement.OpacityProperty, null);
            var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Host.BeginAnimation(UIElement.OpacityProperty, a);
            Scroller.ScrollToTop();
        }
        catch { }
    }

    /// <summary>
    /// Cierre real. Se hace aqui, antes del apagado interno de WPF, porque en un
    /// exe de archivo unico la telemetria de WPF intenta cargar
    /// System.Diagnostics.Tracing y el proceso quedaba vivo sin ventana (y con el
    /// mutex de instancia unica bloqueaba los siguientes arranques).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        App.Cerrando = true;
        Environment.Exit(0);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.Cerrando = true;
        Environment.Exit(0);
    }

    private void ClearNav()
    {
        NavHome.IsChecked = false;
        NavProtect.IsChecked = false;
        NavStorage.IsChecked = false;
        NavProc.IsChecked = false;
        NavClean.IsChecked = false;
        NavTools.IsChecked = false;
        NavApps.IsChecked = false;
        NavSettings.IsChecked = false;
    }

    private void Show(UserControl vista)
    {
        Host.Content = vista;
        AnimateIn();
    }

    /// <summary>Navegacion por nombre (tarjetas del Inicio, acciones de revision...).</summary>
    public void Navigate(string destino)
    {
        switch (destino)
        {
            case "protect":
            case "proteccion":
                NavProtect.IsChecked = true; break;
            case "storage":
            case "almacen":
                NavStorage.IsChecked = true; break;
            case "proc":
                NavProc.IsChecked = true; break;
            case "clean":
                NavClean.IsChecked = true; break;
            case "apps":
                NavApps.IsChecked = true; break;
            case "tools":
                NavTools.IsChecked = true; break;
            case "settings":
                NavSettings.IsChecked = true; break;
            case "deep":
            case "deepclean":
                ShowDeep("home"); break;
            case "startup":
                ShowStartup(); break;
            case "games":
                ShowGames(); break;
            case "health":
                ShowHealth(); break;
            default:
                NavHome.IsChecked = true; break;
        }
    }

    private void ShowDeep(string back)
    {
        ClearNav();
        Deep.BackTarget = back;
        Show(Deep);
        Deep.OnShown();
    }

    private void ShowStartup()
    {
        ClearNav();
        Show(Startup);
        Startup.OnShown();
    }

    private void ShowGames()
    {
        ClearNav();
        Show(Games);
    }

    private void ShowHealth()
    {
        ClearNav();
        Show(Health);
        Health.Refresh();
    }

    private void NavHome_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Home);
        Home.Refresh();
    }

    private void NavProtect_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Protection);
        Protection.OnShown();
    }

    private void NavStorage_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Storage);
        Storage.OnShown();
    }

    private void NavProc_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Processes);
        Processes.Refresh();
    }

    private void NavClean_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Cleaner);
    }

    private void NavApps_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Apps);
        Apps.Refresh();
    }

    private void NavTools_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Toolbox);
    }

    private void NavSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        Show(Settings);
        Settings.Refresh();
    }

    /// <summary>
    /// Smart Boost: si el usuario lo activo en Ajustes, cada minuto se mira si la
    /// RAM o los temporales pasan del umbral y, solo entonces, se liberan. Nunca
    /// se ejecuta si hay video/juego/musica en curso (MediaGuard) ni mas de una
    /// vez cada 10 minutos.
    /// </summary>
    private async Task SmartBoostTickAsync()
    {
        try
        {
            var cfg = SettingsStore.Current;
            if (!cfg.SmartBoost || _smartBusy) return;
            if ((DateTime.Now - _ultimoSmart).TotalMinutes < 10) return;

            var (total, libre) = ProcessService.GetMemory();
            if (total <= 0) return;
            int uso = (int)Math.Round((total - libre) * 100.0 / total);

            long temp = 0;
            if (uso < cfg.SmartBoostRamPct)
                temp = await Task.Run(() => TempCleaner.MeasureTemp().bytes);

            bool porRam = uso >= cfg.SmartBoostRamPct;
            bool porTemp = temp >= cfg.SmartBoostTempMB * 1048576L;
            if (!porRam && !porTemp) return;
            if (MediaGuard.EstaActivo()) return;

            _smartBusy = true;
            _ultimoSmart = DateTime.Now;
            SetStatus(porRam ? "Smart Boost: memoria alta, liberando..." : "Smart Boost: limpiando temporales...");
            var boost = await Task.Run(() => MemoryBoost.Boost());
            var r = await TempCleaner.QuickCleanAsync();
            SetStatus($"Smart Boost: {TempCleaner.FormatBytes(r.BytesFreed)} liberados, " +
                      $"RAM en uso {boost.usoAntesMB / 1024.0:F1} GB -> {boost.usoDespuesMB / 1024.0:F1} GB.");
            _home?.Refresh();
        }
        catch (Exception ex) { AppLog.Error(ex, "smart boost"); }
        finally { _smartBusy = false; }
    }
}
