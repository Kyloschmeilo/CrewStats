using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CrewStats;

/// <summary>
/// Speichert jede Runde lokal (BepInEx/CrewStats/pending) und schickt sie per
/// Discord-Webhook an den Bot. Schlägt das Senden fehl, wird es später nachgeholt.
/// </summary>
internal static class RoundUploader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string PendingDirectory => Path.Combine(CrewStatsPlugin.DataDirectory, "pending");
    private static string SentDirectory => Path.Combine(CrewStatsPlugin.DataDirectory, "sent");

    public static void SaveAndSend(RoundRecord round)
    {
        Directory.CreateDirectory(PendingDirectory);
        var file = Path.Combine(PendingDirectory, $"{round.StartedAt:yyyyMMdd-HHmmss}-{round.GameId}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(round, JsonOptions));
        Task.Run(FlushPendingAsync); // im Hintergrund – das Spiel ruckelt nicht
    }

    /// <summary>Die Webhook-URL aus der Config, oder null, wenn keine gültige eingetragen ist.</summary>
    public static string? WebhookUrl
    {
        get
        {
            var url = CrewStatsPlugin.WebhookUrl.Value.Trim();
            return url.StartsWith("https://") && url.Contains("/api/webhooks/") ? url : null;
        }
    }

    public static async Task FlushPendingAsync()
    {
        var url = WebhookUrl;
        if (url == null)
        {
            CrewStatsPlugin.Logger.LogWarning("Keine gültige Webhook-URL in der Config – Runden werden nur lokal gespeichert.");
            return;
        }
        if (!Directory.Exists(PendingDirectory)) return;

        await SendLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(SentDirectory);
            foreach (var file in Directory.GetFiles(PendingDirectory, "*.json").OrderBy(f => f))
            {
                if (!await SendAsync(url, file)) break; // später erneut versuchen
                File.Move(file, Path.Combine(SentDirectory, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Senden fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            SendLock.Release();
        }
    }

    private static async Task<bool> SendAsync(string url, string file)
    {
        if (await PostJsonFileAsync(url, await File.ReadAllBytesAsync(file), "crewstats-round.json", "🎮 Neue Among-Us-Runde"))
        {
            CrewStatsPlugin.Logger.LogInfo($"Runde an Discord gesendet ({Path.GetFileName(file)})");
            return true;
        }
        return false;
    }

    /// <summary>Schickt eine JSON-Datei als Anhang über den Webhook. Der Bot erkennt sie am Dateinamen.</summary>
    public static async Task<bool> PostJsonFileAsync(string url, byte[] content, string fileName, string message)
    {
        using var form = new MultipartFormDataContent();
        var payload = JsonSerializer.Serialize(new { username = "CrewStats", content = message, flags = 4096 });
        form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

        var json = new ByteArrayContent(content);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(json, "files[0]", fileName);

        using var response = await Http.PostAsync(url, form);
        if (response.IsSuccessStatusCode) return true;
        CrewStatsPlugin.Logger.LogWarning($"Discord hat {fileName} abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
        return false;
    }
}
