using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LunaOptimizer.Services;

namespace LunaOptimizer.Views;

public partial class SettingsView : UserControl
{
    private readonly Action<string> _go;
    private readonly Action<string> _status;
    private readonly Action<bool> _tema;
    private bool _cargando;

    public SettingsView(Action<string> go, Action<string> status, Action<bool> aplicarTema)
    {
        InitializeComponent();
        _go = go;
        _status = status;
        _tema = aplicarTema;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        _cargando = true;
        var cfg = SettingsStore.Current;
        ChkOscuro.IsChecked = ThemeManager.Oscuro;
        ChkSmart.IsChecked = cfg.SmartBoost;
        ChkInicio.IsChecked = StartupService.AutoStartPropio;
        PanelUmbrales.IsEnabled = cfg.SmartBoost;
        PanelUmbrales.Opacity = cfg.SmartBoost ? 1 : 0.55;
        TxtRamUmbral.Text = $"{cfg.SmartBoostRamPct}%";
        TxtTempUmbral.Text = $"{cfg.SmartBoostTempMB} MB";

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }

        TxtDatos.Text = SettingsStore.CarpetaDatos;
        _cargando = false;
    }

    private void ChkOscuro_Click(object sender, RoutedEventArgs e)
    {
        if (_cargando) return;
        _tema(ChkOscuro.IsChecked == true);
        _status(ChkOscuro.IsChecked == true ? "Tema oscuro." : "Tema claro.");
    }

    private void ChkSmart_Click(object sender, RoutedEventArgs e)
    {
        if (_cargando) return;
        var cfg = SettingsStore.Current;
        cfg.SmartBoost = ChkSmart.IsChecked == true;
        SettingsStore.Save();
        PanelUmbrales.IsEnabled = cfg.SmartBoost;
        PanelUmbrales.Opacity = cfg.SmartBoost ? 1 : 0.55;
        _status(cfg.SmartBoost ? "Smart Boost activado." : "Smart Boost desactivado.");
    }

    private void ChkInicio_Click(object sender, RoutedEventArgs e)
    {
        if (_cargando) return;
        bool on = ChkInicio.IsChecked == true;
        StartupService.AutoStartPropio = on;
        bool real = StartupService.AutoStartPropio;
        if (real != on)
        {
            ChkInicio.IsChecked = real;
            _status("No se pudo cambiar el inicio automatico.");
        }
        else _status(on ? "LunaOptimizer se abrirá con Windows." : "LunaOptimizer ya no se abrirá solo.");
    }

    private void RamMenos_Click(object sender, RoutedEventArgs e) => AjustarRam(-5);
    private void RamMas_Click(object sender, RoutedEventArgs e) => AjustarRam(5);

    private void AjustarRam(int delta)
    {
        var cfg = SettingsStore.Current;
        cfg.SmartBoostRamPct = Math.Clamp(cfg.SmartBoostRamPct + delta, 60, 99);
        SettingsStore.Save();
        TxtRamUmbral.Text = $"{cfg.SmartBoostRamPct}%";
    }

    private void TempMenos_Click(object sender, RoutedEventArgs e) => AjustarTemp(-256);
    private void TempMas_Click(object sender, RoutedEventArgs e) => AjustarTemp(256);

    private void AjustarTemp(int delta)
    {
        var cfg = SettingsStore.Current;
        cfg.SmartBoostTempMB = Math.Clamp(cfg.SmartBoostTempMB + delta, 256, 10240);
        SettingsStore.Save();
        TxtTempUmbral.Text = $"{cfg.SmartBoostTempMB} MB";
    }

    private void BtnAbrirDatos_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.CarpetaDatos);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SettingsStore.CarpetaDatos + "\"")
                { UseShellExecute = true });
        }
        catch (Exception ex) { _status("No se pudo abrir la carpeta: " + ex.Message); }
    }

    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string log = Path.Combine(SettingsStore.CarpetaDatos, "errores.log");
            if (!File.Exists(log))
            {
                _status("No hay errores registrados.");
                return;
            }
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
        catch (Exception ex) { _status("No se pudo abrir el registro: " + ex.Message); }
    }

    private void BtnRepo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/juanmarin19/LunaOptimizer")
                { UseShellExecute = true });
        }
        catch { }
    }
}
