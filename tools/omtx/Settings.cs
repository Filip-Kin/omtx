using System.Text.Json;

namespace Omtx;

/// <summary>
/// Settings changed from the omtx ui page, kept in settings.json in the user's app data folder
/// (%APPDATA%\omtx on Windows, ~/.config/omtx on Linux) so they survive a restart.
/// </summary>
internal static class Settings
{
    static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "omtx", "settings.json");
    static readonly object Sync = new();
    static int outMaxFps = 30;
    static bool loaded;

    /// <summary>Highest frame rate an out bridge encodes; 0 = the source's own. Default 30: the
    /// projector PCs (2-core Celerons) cannot decode 1080p60 with camera footage in it.</summary>
    public static int OutMaxFps
    {
        get { Load(); return Volatile.Read(ref outMaxFps); }
        set { Load(); lock (Sync) { outMaxFps = value; Save(); } }
    }

    static void Load()
    {
        if (loaded) return;
        lock (Sync)
        {
            if (loaded) return;
            loaded = true;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
                if (doc.RootElement.TryGetProperty("outMaxFps", out var v) && v.TryGetInt32(out int n) && n >= 0) outMaxFps = n;
            }
            catch { /* no file yet, or unreadable: defaults */ }
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject(); w.WriteNumber("outMaxFps", outMaxFps); w.WriteEndObject();
            }
            System.IO.File.WriteAllBytes(File, ms.ToArray());
        }
        catch (Exception ex) { Console.Error.WriteLine("omtx: settings not saved: " + ex.Message); }
    }
}
