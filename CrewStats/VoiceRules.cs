using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CrewStats;

/// <summary>
/// Proximity-Voice der Stats-Website – reiner Teil ohne Spiel (getestet in Tests/VoiceRulesTests.cs): Meldung bauen,
/// Takt wählen, Antwort lesen. Die Website rechnet daraus, wer wen wie laut hört (web/VoiceChat.kt im Bot-Repo);
/// Positionen gehen nie an andere Spieler, nur an die Website. JSON-Format ↔ VoiceReport im Bot (gemeinsam ändern!).
/// </summary>
internal static class VoiceRules
{
    internal sealed record Player(string Fc, string Name, int Color, float X, float Y, bool Dead, bool Vent, bool Mute);

    /// <summary>Mit Zuhörern ~5×/s (Lautstärke folgt der Bewegung), ohne alle 2 s (nur „Lobby gibt es noch“).</summary>
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);

    public const int MaxPlayers = PlayerInfoRules.MaxPlayers; // TOR: bis zu 24 Spieler

    public static TimeSpan Interval(int listeners) => listeners > 0 ? ActiveInterval : IdleInterval;

    /// <summary>„lobby“ | „tasks“ | „meeting“.</summary>
    public static string Phase(bool started, bool normalMode, bool meeting) =>
        !started || !normalMode ? "lobby" : meeting ? "meeting" : "tasks";

    public static string Build(string hostFc, string code, string phase, IEnumerable<Player> players)
    {
        var list = new List<object>();
        foreach (var p in players)
        {
            if (string.IsNullOrWhiteSpace(p.Fc)) continue;
            list.Add(new
            {
                fc = p.Fc.Trim().ToLowerInvariant(),
                name = p.Name,
                color = p.Color,
                x = Math.Round(p.X, 2),
                y = Math.Round(p.Y, 2),
                dead = p.Dead,
                vent = p.Vent,
                mute = p.Mute,
            });
            if (list.Count >= MaxPlayers) break;
        }
        return JsonSerializer.Serialize(new
        {
            crewstats = "voice",
            v = 1,
            fc = hostFc.Trim().ToLowerInvariant(),
            code,
            phase,
            players = list,
        });
    }

    /// <summary>Wie viele gerade im Browser zuhören (Antwort der Website), -1 = unlesbar.</summary>
    public static int Listeners(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return -1;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return -1;
            if (!root.TryGetProperty("crewstats", out var kind) || kind.GetString() != "voice-reply") return -1;
            return root.TryGetProperty("listeners", out var n) && n.TryGetInt32(out var count) ? Math.Max(0, count) : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }
}
