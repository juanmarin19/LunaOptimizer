using System.Diagnostics;

namespace LunaOptimizer.Models;

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double RamMB { get; set; }
    public string Status { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsHeavy => RamMB > 500;
    public bool IsMedium => RamMB > 200 && RamMB <= 500;
    public System.Windows.Media.Imaging.BitmapSource? Icon { get; set; }
    /// true = se puede cerrar sin romper Windows
    public bool CanKill { get; set; } = true;
    public string KillLabel => CanKill ? "Si" : "No (sistema)";
}

/// Una fila agrupada por nombre de exe: "brave x8 - 1200 MB".
public class ProcessGroup
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public double TotalRamMB { get; set; }
    public string Pids { get; set; } = "";
    public string Status { get; set; } = "OK";
    public string Path { get; set; } = "";
    public System.Windows.Media.Imaging.BitmapSource? Icon { get; set; }
    public bool CanKill { get; set; } = true;
    public string KillLabel => CanKill ? "Si" : "No (sistema)";
    public string Display => Count > 1 ? $"{Name} x{Count}" : Name;
    public List<int> PidList { get; set; } = new();
}


