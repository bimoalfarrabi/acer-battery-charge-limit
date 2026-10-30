using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattCtl.App.Services;

// Persists the last good snapshot so the window can paint real values on open
// instead of a skeleton. A corrupt or missing cache is a cache miss, not an error.
public sealed class SnapshotCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;

    public SnapshotCache()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BattCtl.App",
            "last-snapshot.json");
    }

    public CachedSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CachedSnapshot>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    public void Save(Snapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new CachedSnapshot(DateTime.Now, snapshot), SerializerOptions));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // A failed cache write must never take the app down; next refresh retries it.
        }
    }
}

public sealed class CachedSnapshot
{
    public CachedSnapshot(DateTime savedAt, Snapshot snapshot)
    {
        SavedAt = savedAt;
        Snapshot = snapshot;
    }

    [JsonPropertyName("savedAt")]
    public DateTime SavedAt { get; set; }

    [JsonPropertyName("snapshot")]
    public Snapshot Snapshot { get; set; }
}
