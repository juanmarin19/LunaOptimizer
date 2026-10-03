using FluentCleaner.Models;
using System.IO;
using System.IO.Enumeration;

namespace LunaOptimizer.Services;

/// Motor de analisis/limpieza propio que usa los modelos de FluentCleaner.Core
/// (CleanerEntry, FileKeyEntry, ScanResult) con licencia MIT.
/// Logica de escaneo adaptada del CleaningService original para WPF net8.
public static class ScanEngine
{
    private static readonly FluentCleaner.Services.PathExpander _expander = new();

    public static Task<ScanResult> AnalyzeAsync(CleanerEntry entry, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => Analyze(entry, progress, token), token);

    public static Task<(int count, long bytes)> CleanAsync(ScanResult result, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => Clean(result, progress, token), token);

    private static ScanResult Analyze(CleanerEntry entry, IProgress<string>? progress, CancellationToken token)
    {
        var result = new ScanResult { Entry = entry };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in entry.FileKeys)
        {
            if (token.IsCancellationRequested) break;
            foreach (var file in FindFiles(fk))
            {
                if (token.IsCancellationRequested) break;
                if (!seen.Add(file)) continue;
                // excluir protegidos (extensiones navegador)
                if (file.Contains(@"\IndexedDB\chrome-extension_", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static (int count, long bytes) Clean(ScanResult result, IProgress<string>? progress, CancellationToken token)
    {
        int c = 0; long b = 0;
        foreach (var f in result.FilesToDelete)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                long size = result.FileSizes.TryGetValue(f, out var s) ? s : 0;
                File.Delete(f);
                c++; b += size;
            }
            catch { }
        }
        // REMOVESELF: borrar carpetas vacias
        try
        {
            foreach (var fk in result.Entry.FileKeys)
            {
                if (fk.Flag != FileKeyFlag.RemoveSelf) continue;
                foreach (var d in _expander.ResolvePaths(fk.Path))
                {
                    try
                    {
                        if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                            Directory.Delete(d);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return (c, b);
    }
}
