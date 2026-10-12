using FluentCleaner.Models;
using FluentCleaner.Services;
using Microsoft.Win32;
using System.IO;
using System.IO.Enumeration;

namespace LunaOptimizer.Services;

/// Motor de analisis/limpieza propio que usa los modelos de FluentCleaner.Core
/// (CleanerEntry, FileKeyEntry, ScanResult) con licencia MIT.
/// Logica de escaneo adaptada del CleaningService original para WPF net8.
public static class ScanEngine
{
    private static readonly PathExpander _expander = new();

    public static Task<ScanResult> AnalyzeAsync(CleanerEntry entry, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => Analyze(entry, progress, token), token);

    private static ScanResult Analyze(CleanerEntry entry, IProgress<string>? progress, CancellationToken token)
    {
        var result = new ScanResult { Entry = entry };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ExcludeKeyN: lo que Winapp2 marca como intocable NO se debe borrar.
        var fileExcludes = BuildFileExcludes(entry);
        var regExcludes = BuildRegExcludes(entry);

        foreach (var fk in entry.FileKeys)
        {
            if (token.IsCancellationRequested) break;
            foreach (var file in FindFiles(fk))
            {
                if (token.IsCancellationRequested) break;
                if (!seen.Add(file)) continue;
                // excluir protegidos (extensiones navegador)
                if (file.Contains(@"\IndexedDB\chrome-extension_", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsFileExcluded(file, fileExcludes)) continue;
                long size;
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;
                    size = fi.Length;
                    // skip bloqueados: intentar abrir
                    try { using var s = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch { continue; }
                }
                catch { continue; }
                result.FilesToDelete.Add(file);
                result.FileSizes[file] = size;
                result.TotalBytes += size;
            }
        }

        // RegKeyN: claves/valores de registro a borrar (con sus exclusiones).
        foreach (var item in CollectRegistry(entry, regExcludes))
            result.RegistryToDelete.Add(item);

        return result;
    }

    private static IEnumerable<string> FindFiles(FileKeyEntry fk)
    {
        List<string> dirs;
        try { dirs = _expander.ResolvePaths(fk.Path); }
        catch { yield break; }
        string[] patterns = fk.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) patterns = new[] { "*.*" };
        bool recurse = fk.Flag is FileKeyFlag.Recurse or FileKeyFlag.RemoveSelf;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pat in patterns)
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir, pat,
                        recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                }
                catch { continue; }
                foreach (var f in files)
                {
                    string file = f;
                    // FileSystemName check para patrones complejos
                    try
                    {
                        if (!FileSystemName.MatchesSimpleExpression(pat, Path.GetFileName(file), ignoreCase: true)
                            && pat != "*.*") continue;
                    }
                    catch { }
                    yield return file;
                }
            }
        }
    }

    // ---- ExcludeKey ----

    private static List<(string Path, string? Pattern, ExcludeType Type)> BuildFileExcludes(CleanerEntry entry)
    {
        var list = new List<(string, string?, ExcludeType)>();
        foreach (var ex in entry.ExcludeKeys)
        {
            if (ex.Type == ExcludeType.Reg) continue;
            string raw = ex.Path;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                foreach (var p in _expander.ResolvePaths(raw))
                    list.Add((p, ex.Pattern, ex.Type));
            }
            catch { }
        }
        return list;
    }

    private static bool IsFileExcluded(string file, List<(string Path, string? Pattern, ExcludeType Type)> excludes)
    {
        if (excludes.Count == 0) return false;
        string name = Path.GetFileName(file);
        foreach (var (path, pattern, type) in excludes)
        {
            if (file.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = path.TrimEnd('\\', '/') + "\\";
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (type == ExcludeType.Path || string.IsNullOrEmpty(pattern)) return true;
            try { if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return true; }
            catch { }
        }
        return false;
    }

    private static HashSet<string> BuildRegExcludes(CleanerEntry entry)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in entry.ExcludeKeys)
        {
            if (ex.Type != ExcludeType.Reg) continue;
            string p = Environment.ExpandEnvironmentVariables(ex.Path).Trim();
            if (p.Length == 0) continue;
            set.Add(NormReg(p, null));
            set.Add(NormReg(p, ex.Pattern));
        }
        return set;
    }

    // ---- RegKey ----

    private static IEnumerable<RegistryItemToDelete> CollectRegistry(CleanerEntry entry, HashSet<string> excludes)
    {
        foreach (var rk in entry.RegKeys)
        {
            foreach (var key in ResolveRegKeys(rk.KeyPath))
            {
                if (excludes.Contains(NormReg(key, null)) || excludes.Contains(NormReg(key, rk.ValueName)))
                    continue;
                yield return new RegistryItemToDelete { KeyPath = key, ValueName = rk.ValueName };
            }
        }
    }

    private static string NormReg(string key, string? value)
        => key.Trim().ToUpperInvariant() + "|" +
           (string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant());

    private static IEnumerable<string> ResolveRegKeys(string rawPath)
    {
        string path = Environment.ExpandEnvironmentVariables(rawPath).Trim();
        if (path.Length == 0) yield break;
        if (path.IndexOfAny(new[] { '*', '?' }) < 0) { yield return path; yield break; }
        foreach (var r in ResolveRegWildcards(path)) yield return r;
    }

    private static IEnumerable<string> ResolveRegWildcards(string path)
    {
        var parts = path.Split('\\');
        if (parts.Length < 2) yield break;
        var baseKey = RegistryHelpers.OpenHive(parts[0].ToUpperInvariant());
        if (baseKey is null) yield break;
        foreach (var full in RecurseReg(baseKey, parts[0], parts.Skip(1).ToArray(), 0))
            yield return full;
    }

    private static IEnumerable<string> RecurseReg(RegistryKey? key, string prefix, string[] parts, int idx)
    {
        if (key is null) yield break;
        if (idx >= parts.Length) { yield return prefix; yield break; }
        string seg = parts[idx];
        if (seg.IndexOf('*') < 0 && seg.IndexOf('?') < 0)
        {
            foreach (var r in RecurseReg(OpenSub(key, seg), prefix + "\\" + seg, parts, idx + 1))
                yield return r;
            yield break;
        }
        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { yield break; }
        foreach (var sub in subs)
        {
            try { if (!FileSystemName.MatchesSimpleExpression(seg, sub, ignoreCase: true)) continue; }
            catch { continue; }
            foreach (var r in RecurseReg(OpenSub(key, sub), prefix + "\\" + sub, parts, idx + 1))
                yield return r;
        }
    }

    private static RegistryKey? OpenSub(RegistryKey key, string name)
    {
        try { return key.OpenSubKey(name); } catch { return null; }
    }

    /// Ramas del registro que NUNCA se borran aunque una regla (mal formada) lo
    /// pida: son la columna vertebral de Windows. Solo aplica al borrado de la
    /// clave completa; borrar un VALOR concreto si se permite (p. ej. una entrada
    /// de "Run").
    private static readonly HashSet<string> RegKeysProtegidas = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKCU\SOFTWARE",
        @"HKCU\SOFTWARE\Microsoft",
        @"HKCU\SOFTWARE\Microsoft\Windows",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKLM\SOFTWARE",
        @"HKLM\SOFTWARE\Microsoft",
        @"HKLM\SOFTWARE\Microsoft\Windows",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKLM\SYSTEM",
        @"HKLM\SYSTEM\CurrentControlSet",
        @"HKLM\SYSTEM\CurrentControlSet\Control",
        @"HKLM\SYSTEM\CurrentControlSet\Services",
    };

    /// Borra las claves/valores de registro recogidos en el analisis.
    public static void DeleteRegistry(ScanResult result)
    {
        foreach (var item in result.RegistryToDelete) DeleteRegistryItem(item);
    }

    public static void DeleteRegistryItem(RegistryItemToDelete item)
    {
        try
        {
            int sep = item.KeyPath.IndexOf('\\');
            if (sep <= 0) return;
            string hive = item.KeyPath[..sep].ToUpperInvariant();
            string sub = item.KeyPath[(sep + 1)..];
            var baseKey = RegistryHelpers.OpenHive(hive);
            if (baseKey is null) return;

            if (item.ValueName is null)
            {
                if (RegKeysProtegidas.Contains(item.KeyPath)) return;   // nunca la clave completa
                int last = sub.LastIndexOf('\\');
                if (last < 0)
                {
                    baseKey.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                }
                else
                {
                    using var parent = baseKey.OpenSubKey(sub[..last], writable: true);
                    parent?.DeleteSubKeyTree(sub[(last + 1)..], throwOnMissingSubKey: false);
                }
            }
            else
            {
                using var key = baseKey.OpenSubKey(sub, writable: true);
                key?.DeleteValue(item.ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { AppLog.Error(ex, "Borrando registro " + item.KeyPath); }
    }
}
