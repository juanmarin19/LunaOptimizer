using FluentCleaner.Models;

namespace FluentCleaner.Services;

// Owns the Custom/ folder.
// CustomPage uses CustomDir to manage files; CleanerPageViewModel calls LoadEnabledEntriesAsync()
// to pull active entries into the cleaner
public class CustomEntryService
{
    public static readonly string CustomDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Custom");

    private readonly Winapp2Parser    _parser    = new();
    private readonly DetectionService _detection = new();

    //Loads only ENABLED entries (.disabled files excluded) as ready-to-use CleanerEntry objects.
    //Contrast: CustomPage.LoadEntriesAsync() loads everything including disabled for the management UI
    public async Task<List<CleanerEntry>> LoadEnabledEntriesAsync()
    {
        var entries = new List<CleanerEntry>();
        if (!Directory.Exists(CustomDir)) return entries;

        // .ini entries parsed as Winapp2;detection criteria honoured when present,
        // skipped entirely when the user deliberately omitted them
        foreach (var path in Directory.GetFiles(CustomDir, "*.ini")
                                      .Where(f => !f.EndsWith(".ini.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var ce in await _parser.ParseFileAsync(path))
            {
                ce.IsCustom = true;
                bool hasDetection = ce.DetectFiles.Count > 0 || ce.DetectKeys.Count > 0 || ce.SpecialDetect is not null;
                if (hasDetection && !_detection.IsInstalled(ce)) continue;
                entries.RemoveAll(e => string.Equals(e.Name, ce.Name, StringComparison.OrdinalIgnoreCase));
                entries.Add(ce);
            }
        }

        return entries;
    }
}
