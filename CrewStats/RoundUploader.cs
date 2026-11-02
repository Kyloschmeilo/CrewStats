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
/// Offline-Puffer: speichert jede Runde lokal (BepInEx/CrewStats/pending) und schickt sie per Discord-Webhook an den
/// Bot. Schlägt das Senden fehl, wird es nachgeholt – beim nächsten Start, nach der nächsten Runde und (Config
/// [Komfort] Nachsenden, Standard an) alle 2 Minuten, solange das Spiel läuft. Gesendete liegen in „sent“ (die
/// neuesten 300), Runden, die Discord dauerhaft ablehnt, in „failed“ (blockieren die anderen nicht).
/// </summary>
internal static class RoundUploader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    // Kompakt (ohne Einrückung): mit Zeitleiste und Laufwegen ist eine Runde sonst schnell mehrere MB groß
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string PendingDirectory => Path.Combine(CrewStatsPlugin.DataDirectory, "pending");
    private static string SentDirectory => Path.Combine(CrewStatsPlugin.DataDirectory, "sent");
    private static string FailedDirectory => Path.Combine(CrewStatsPlugin.DataDirectory, "failed");
    private const int KeepSent = 300;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
    private static DateTime _nextRetry = DateTime.UtcNow + TimeSpan.FromMinutes(1);
    private static int _pending = -1;

    /// <summary>Wie viele Runden noch aufs Senden warten (für den Verbindungsstatus).</summary>
    public static int PendingCount
    {
        get
        {
            if (_pending < 0) _pending = CountPending();
            return _pending;
        }
    }

    private static int CountPending()
    {
        try
        {
            return Directory.Exists(PendingDirectory) ? Directory.GetFiles(PendingDirectory, "*.json").Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Aus dem Spiel-Thread (jeden Frame): liegen Runden im Puffer, alle 2 Minuten erneut senden.</summary>
    public static void Tick()
    {
        if (DateTime.UtcNow < _nextRetry) return;
        _nextRetry = DateTime.UtcNow + RetryInterval;
        if (!CrewStatsPlugin.RetryUploads.Value || PendingCount == 0 || WebhookUrl == null) return;
        Task.Run(FlushPendingAsync);
    }

    /// <summary>Sofort nachsenden (Knopf im Einstellungsmenü).</summary>
    public static void RetryNow()
    {
        _nextRetry = DateTime.UtcNow + RetryInterval;
        Task.Run(FlushPendingAsync);
    }

    public static void SaveAndSend(RoundRecord round)
    {
        Directory.CreateDirectory(PendingDirectory);
        var file = Path.Combine(PendingDirectory, $"{round.StartedAt:yyyyMMdd-HHmmss}-{round.GameId}.json");
        File.WriteAllBytes(file, Serialize(round));
        _pending = CountPending();
        Task.Run(FlushPendingAsync); // im Hintergrund – das Spiel ruckelt nicht
    }

    /// <summary>JSON der Runde; wird es für den Discord-Webhook zu groß, ohne Laufwege.</summary>
    internal static byte[] Serialize(RoundRecord round)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(round, JsonOptions);
        if (json.Length <= RoundStats.MaxBytesWithTracks || round.Tracks.Count == 0) return json;
        CrewStatsPlugin.Logger.LogWarning($"Rundendaten sind {json.Length / 1024} KB groß – Laufwege werden weggelassen.");
        round.Tracks = new();
        return JsonSerializer.SerializeToUtf8Bytes(round, JsonOptions);
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
                var result = await SendAsync(url, file);
                if (result == Upload.Retry) break; // später erneut versuchen
                var target = result == Upload.Sent ? SentDirectory : FailedDirectory;
                Directory.CreateDirectory(target);
                File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            PruneSent();
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Senden fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            _pending = CountPending();
            SendLock.Release();
        }
    }

    private static void PruneSent()
    {
        var sent = Directory.GetFiles(SentDirectory, "*.json").OrderByDescending(f => f).Skip(KeepSent).ToList();
        foreach (var file in sent)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // nicht schlimm
            }
        }
    }

    private enum Upload { Sent, Retry, Rejected }

    private static async Task<Upload> SendAsync(string url, string file)
    {
        try
        {
            var status = await PostJsonFileWithStatusAsync(url, await File.ReadAllBytesAsync(file), "crewstats-round.json", "🎮 Neue Among-Us-Runde");
            if (status is >= 200 and < 300)
            {
                CrewStatsPlugin.Logger.LogInfo($"Runde an Discord gesendet ({Path.GetFileName(file)})");
                return Upload.Sent;
            }
            if (status is 401 or 404)
            {
                ConnectionStatus.Report(ConnectionStatus.State.Error, "Webhook-URL ungültig – /mod-setup im Discord");
                return Upload.Retry; // mit neuer URL klappt es wieder
            }
            if (status is 400 or 413)
            {
                CrewStatsPlugin.Logger.LogWarning($"Discord lehnt {Path.GetFileName(file)} dauerhaft ab – liegt jetzt in „failed“.");
                return Upload.Rejected;
            }
            ConnectionStatus.Report(ConnectionStatus.State.Error, $"Runde nicht gesendet (Discord {status}) – wird nachgeholt");
            return Upload.Retry;
        }
        catch (Exception exc) when (exc is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            ConnectionStatus.Report(ConnectionStatus.State.Error, "Discord nicht erreichbar – Runde wird nachgeholt");
            CrewStatsPlugin.Logger.LogWarning($"Runde nicht gesendet ({exc.Message}) – wird nachgeholt.");
            return Upload.Retry;
        }
    }

    /// <summary>Schickt eine JSON-Datei als Anhang über den Webhook. Der Bot erkennt sie am Dateinamen.</summary>
    public static async Task<bool> PostJsonFileAsync(string url, byte[] content, string fileName, string message) =>
        await PostJsonFileWithStatusAsync(url, content, fileName, message) is >= 200 and < 300;

    private static async Task<int> PostJsonFileWithStatusAsync(string url, byte[] content, string fileName, string message)
    {
        using var form = new MultipartFormDataContent();
        var payload = JsonSerializer.Serialize(new { username = "CrewStats", content = message, flags = 4096 });
        form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

        var json = new ByteArrayContent(content);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(json, "files[0]", fileName);

        using var response = await Http.PostAsync(url, form);
        if (!response.IsSuccessStatusCode)
            CrewStatsPlugin.Logger.LogWarning($"Discord hat {fileName} abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
        return (int)response.StatusCode;
    }
}
