using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CrewStats;

/// <summary>
/// Fragen an den Bot. Ist die URL eine Adresse der Stats-Website (…/api/mod/…, ab 1.8.0), antwortet die Website in
/// EINER Anfrage. Sonst wie bisher über einen Webhook (kein offener Port beim Bot): Anfrage mit ?wait=true schicken
/// (dadurch kennen wir die Nachrichten-ID), der Bot bearbeitet GENAU diese Nachricht mit seiner Antwort, die Mod liest
/// sie und löscht sie.
/// Benutzt von Auto-Join und den Komfort-Funktionen (Stats, Lobby-Info, Verknüpfen). Meldet jedes Ergebnis an
/// <see cref="ConnectionStatus"/>. Läuft immer im Hintergrund (nie im Spiel-Thread aufrufen und warten).
/// </summary>
internal static class BotClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// URL für Spieler-Anfragen: die Auto-Join-URL (bekommt jeder per /autojoin), sonst die geheime Webhook-URL
    /// des Hosts. Null = nichts eingetragen, dann gibt es keine Komfort-Funktionen mit dem Bot.
    /// </summary>
    public static string? PlayerUrl =>
        AutoJoinRules.NormalizeUrl(CrewStatsPlugin.AutoJoinUrl.Value) ?? AutoJoinRules.NormalizeUrl(CrewStatsPlugin.WebhookUrl.Value);

    /// <summary>Anfrage senden und auf die Antwort warten; null = keine Antwort (Grund steht im Verbindungsstatus).</summary>
    public static async Task<T?> AskAsync<T>(string url, string content, string username, Func<string?, T?> parse, TimeSpan timeout)
        where T : class
    {
        if (AutoJoinRules.IsWebsiteUrl(url)) return await AskWebsiteAsync(url, content, parse, timeout);

        string messageUrl;
        try
        {
            using var posted = await Http.PostAsync(url + "?wait=true", Body(username, content));
            if (posted.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ConnectionStatus.Report(ConnectionStatus.State.Busy, "Discord bremst gerade – gleich nochmal");
                return null;
            }
            if (!posted.IsSuccessStatusCode)
            {
                var invalid = posted.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized;
                ConnectionStatus.Report(ConnectionStatus.State.Error,
                    invalid ? "URL ungültig – im Discord /autojoin neu holen" : $"Discord-Fehler {(int)posted.StatusCode}");
                CrewStatsPlugin.Logger.LogWarning($"Bot-Anfrage: Discord hat abgelehnt: {(int)posted.StatusCode} {posted.ReasonPhrase}");
                return null;
            }
            using var json = JsonDocument.Parse(await posted.Content.ReadAsStringAsync());
            messageUrl = $"{url}/messages/{json.RootElement.GetProperty("id").GetString()}";
        }
        catch (Exception exc)
        {
            ConnectionStatus.Report(ConnectionStatus.State.Error, "Discord nicht erreichbar (Internet?)");
            CrewStatsPlugin.Logger.LogWarning($"Bot-Anfrage fehlgeschlagen: {exc.Message}");
            return null;
        }

        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                try
                {
                    using var response = await Http.GetAsync(messageUrl);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(RetryAfter(response));
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) continue;
                    string? text;
                    using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                        text = json.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var reply = parse(text);
                    if (reply != null)
                    {
                        ConnectionStatus.Report(ConnectionStatus.State.Ok, "Bot verbunden");
                        return reply;
                    }
                }
                catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException)
                {
                    // einzelner Abruf fehlgeschlagen – bis zur Frist weiter versuchen
                }
            }
            ConnectionStatus.Report(ConnectionStatus.State.NoReply, "Bot antwortet nicht (offline oder alte Version?)");
            return null;
        }
        finally
        {
            try
            {
                using var _ = await Http.DeleteAsync(messageUrl); // sonst räumt der Bot nach 30 s auf
            }
            catch
            {
                // nicht schlimm
            }
        }
    }

    /// <summary>Über die Stats-Website: Anfrage-JSON hinschicken, die Antwort steht direkt im Body.</summary>
    private static async Task<T?> AskWebsiteAsync<T>(string url, string content, Func<string?, T?> parse, TimeSpan timeout)
        where T : class
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var response = await Http.PostAsync(url, new StringContent(content, Encoding.UTF8, "application/json"), cancel.Token);
            var text = await response.Content.ReadAsStringAsync();
            switch ((int)response.StatusCode)
            {
                case 429:
                    ConnectionStatus.Report(ConnectionStatus.State.Busy, "Website bremst gerade – gleich nochmal");
                    return null;
                case 404:
                    ConnectionStatus.Report(ConnectionStatus.State.Error, "URL ungültig – im Discord /autojoin neu holen");
                    return null;
                case 503 or 504:
                    ConnectionStatus.Report(ConnectionStatus.State.NoReply, "Bot gerade offline – gleich nochmal");
                    return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                ConnectionStatus.Report(ConnectionStatus.State.Error, $"Website-Fehler {(int)response.StatusCode}");
                CrewStatsPlugin.Logger.LogWarning($"Bot-Anfrage: Website hat abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }
            var reply = parse(text);
            if (reply == null)
            {
                ConnectionStatus.Report(ConnectionStatus.State.NoReply, "Antwort unlesbar (alte Website-Version?)");
                return null;
            }
            ConnectionStatus.Report(ConnectionStatus.State.Ok, "Bot verbunden (über die Website)");
            return reply;
        }
        catch (Exception exc)
        {
            ConnectionStatus.Report(ConnectionStatus.State.Error, "Website nicht erreichbar");
            CrewStatsPlugin.Logger.LogWarning($"Bot-Anfrage über die Website fehlgeschlagen: {exc.Message}");
            return null;
        }
    }

    private static StringContent Body(string username, string content) =>
        new(JsonSerializer.Serialize(new
        {
            username,
            content,
            flags = 4096, // keine Benachrichtigung im Channel
            allowed_mentions = new { parse = Array.Empty<string>() },
        }), Encoding.UTF8, "application/json");

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.2, 5));
    }
}

/// <summary>Verbindung zum Bot für das Status-Symbol: letztes Ergebnis einer Anfrage bzw. eines Runden-Uploads.</summary>
internal static class ConnectionStatus
{
    public enum State { Unknown, Ok, Busy, NoReply, Error }

    private static readonly object Lock = new();
    private static State _state = State.Unknown;
    private static string _message = "noch nicht geprüft";
    private static DateTime _since = DateTime.UtcNow;

    public static void Report(State state, string message)
    {
        lock (Lock)
        {
            if (_state != state) _since = DateTime.UtcNow;
            _state = state;
            _message = message;
        }
    }

    public static (State State, string Message, DateTime Since) Current
    {
        get
        {
            lock (Lock) return (_state, _message, _since);
        }
    }
}
