using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CrewStats;

/// <summary>
/// Schickt kurze Zustandsmeldungen (JSON im Nachrichtentext) über den Webhook an den Bot.
/// Nacheinander und „neueste gewinnt“: liegt beim Senden schon eine neuere Meldung bereit,
/// wird die ältere übersprungen. Läuft im Hintergrund, das Spiel ruckelt nicht.
/// </summary>
internal sealed class LatestWebhookSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _name;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _sequence;

    public LatestWebhookSender(string name) => _name = name;

    public void Send(string content)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        Task.Run(() => PostAsync(content, sequence));
    }

    private async Task PostAsync(string content, int sequence)
    {
        var url = RoundUploader.WebhookUrl;
        if (url == null)
        {
            CrewStatsPlugin.Logger.LogWarning($"{_name}: keine gültige Webhook-URL in der Config.");
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (sequence != Volatile.Read(ref _sequence)) return; // inzwischen gibt es eine neuere Meldung

            var body = JsonSerializer.Serialize(new
            {
                username = "CrewStats",
                content,
                flags = 4096, // keine Benachrichtigung im Channel
                allowed_mentions = new { parse = Array.Empty<string>() },
            });
            using var response = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
                CrewStatsPlugin.Logger.LogWarning($"{_name}: Discord hat abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"{_name}: Senden fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }
}
