using System.Text.Json;

namespace VideoLocalShow;

/// <summary>
/// Remembers the order the user arranged the downloaded files in, which is also the order they
/// play back in. Stored as a list of file names (not paths - the app's data folder path can
/// change between installs/updates) in the app's preferences.
/// </summary>
public static class DownloadOrderStore
{
    private const string PreferenceKey = "DownloadOrder";

    /// <summary>
    /// Sorts files by the saved order. Files the user hasn't placed yet - freshly downloaded
    /// ones - go first, newest first, so a new download is visible at the top rather than buried
    /// at the bottom of a long custom order.
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> items, Func<T, string> getName, Func<T, DateTime> getLastWriteUtc)
    {
        var saved = Load();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < saved.Count; i++)
        {
            position.TryAdd(saved[i], i);
        }

        var list = items.ToList();
        var unplaced = list
            .Where(item => !position.ContainsKey(getName(item)))
            .OrderByDescending(getLastWriteUtc);
        var placed = list
            .Where(item => position.ContainsKey(getName(item)))
            .OrderBy(item => position[getName(item)]);

        return unplaced.Concat(placed).ToList();
    }

    public static void Save(IEnumerable<string> fileNamesInOrder)
    {
        try
        {
            Preferences.Default.Set(PreferenceKey, JsonSerializer.Serialize(fileNamesInOrder.ToList()));
        }
        catch (Exception)
        {
            // Losing the custom order is not worth failing the list over.
        }
    }

    private static List<string> Load()
    {
        try
        {
            var json = Preferences.Default.Get(PreferenceKey, string.Empty);
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
