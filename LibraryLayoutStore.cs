using System.Text.Json;

namespace VideoLocalShow;

/// <summary>One line of the saved list: either a file (by name) or a group heading.</summary>
public sealed class LayoutEntry
{
    public string? File { get; set; }

    public string? GroupId { get; set; }

    public string? GroupName { get; set; }

    public bool Expanded { get; set; } = true;
}

/// <summary>
/// Remembers how the downloaded files are arranged: their order (which is also the playback order)
/// and the group headings between them. Files are stored by name, not path - the app's data folder
/// path can change between installs/updates.
/// </summary>
public static class LibraryLayoutStore
{
    private const string LayoutKey = "LibraryLayout";

    // The list of plain file names saved before groups existed.
    private const string LegacyOrderKey = "DownloadOrder";

    public static List<LayoutEntry> Load()
    {
        try
        {
            var json = Preferences.Default.Get(LayoutKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                return JsonSerializer.Deserialize<List<LayoutEntry>>(json) ?? [];
            }

            var legacy = Preferences.Default.Get(LegacyOrderKey, string.Empty);
            if (!string.IsNullOrEmpty(legacy))
            {
                return (JsonSerializer.Deserialize<List<string>>(legacy) ?? [])
                    .Select(name => new LayoutEntry { File = name })
                    .ToList();
            }
        }
        catch (Exception)
        {
            // A damaged saved layout just means starting from the default arrangement.
        }

        return [];
    }

    public static void Save(IEnumerable<LayoutEntry> entries)
    {
        try
        {
            Preferences.Default.Set(LayoutKey, JsonSerializer.Serialize(entries.ToList()));
        }
        catch (Exception)
        {
            // Losing the arrangement is not worth failing the list over.
        }
    }
}
