using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BepInEx;

namespace CrewStats;

/// <summary>
/// Auto-Update über GitHub-Releases: prüft beim Spielstart im Hintergrund, ob es eine neuere
/// Version gibt, und tauscht die eigene DLL aus. Die geladene DLL darf Windows nur umbenennen,
/// nicht löschen – deshalb: alte → .old, neue an ihren Platz; .old wird beim nächsten Start gelöscht.
/// Die neue Version läuft ab dem nächsten Spielstart (Hinweis im Hauptmenü).
/// </summary>
internal static class Updater
{
    /// <summary>GitHub-Repo mit den Releases ("besitzer/repo"). Muss zum Installer passen!</summary>
    public const string GitHubRepo = "Kyloschmeilo/CrewStats";
    public const string AssetName = "CrewStats.dll";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Hinweis fürs Hauptmenü, sobald ein Update installiert wurde.</summary>
    public static string? Notice { get; private set; }

    private static string PluginFile
    {
        get
        {
            var location = typeof(Updater).Assembly.Location;
            return string.IsNullOrEmpty(location) ? Path.Combine(Paths.PluginPath, AssetName) : location;
        }
    }

    /// <summary>Beim Laden: Reste vom letzten Update wegräumen (die alte DLL ist jetzt nicht mehr geladen).</summary>
    public static void RemoveLeftovers()
    {
        foreach (var leftover in new[] { PluginFile + ".old", PluginFile + ".new" })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogWarning($"Update-Rest {Path.GetFileName(leftover)} nicht löschbar: {exc.Message}");
            }
        }
    }

    public static async Task CheckAsync()
    {
        if (!CrewStatsPlugin.AutoUpdate.Value || GitHubRepo.StartsWith("DEIN-")) return;
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var release = JsonDocument.Parse(json);
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseVersion(tag);
            var current = ParseVersion(CrewStatsPlugin.Version);
            if (latest == null || current == null)
            {
                CrewStatsPlugin.Logger.LogWarning($"Update-Prüfung: Version nicht lesbar ({tag} / {CrewStatsPlugin.Version}).");
                return;
            }
            if (latest <= current)
            {
                CrewStatsPlugin.Logger.LogInfo($"CrewStats ist aktuell ({current}).");
                return;
            }

            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind != JsonValueKind.Object)
            {
                CrewStatsPlugin.Logger.LogWarning($"Release {tag} enthält keine {AssetName}.");
                return;
            }

            var bytes = await Http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString());
            if (bytes.Length < 10_000 || bytes[0] != 'M' || bytes[1] != 'Z')
            {
                CrewStatsPlugin.Logger.LogWarning("Update-Download ist keine gültige DLL – übersprungen.");
                return;
            }

            Install(bytes);
            Notice = $"CrewStats {latest} installiert – bitte Among Us neu starten";
            CrewStatsPlugin.Logger.LogInfo($"Update {current} → {latest} installiert, aktiv ab dem nächsten Start.");
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Update-Prüfung fehlgeschlagen: {exc.Message}");
        }
    }

    /// <summary>"v1.2.0", "1.2.0" oder "1.2.0+abc123" (Build-Metadaten) → 1.2.0</summary>
    private static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    private static void Install(byte[] bytes)
    {
        var path = PluginFile;
        var old = path + ".old";
        var fresh = path + ".new";
        File.WriteAllBytes(fresh, bytes);
        if (File.Exists(old)) File.Delete(old);

        File.Move(path, old); // geladene DLL: umbenennen geht, löschen nicht
        try
        {
            File.Move(fresh, path);
        }
        catch
        {
            File.Move(old, path); // alte Version wiederherstellen
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CrewStats-Updater"); // GitHub verlangt einen User-Agent
        return client;
    }
}
