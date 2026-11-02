using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CrewStats;

/// <summary>Zahlen eines Spielers aus dem Discord (ohne Discord-Namen/-IDs – die schickt der Bot bewusst nicht).</summary>
internal sealed record PlayerStats(bool Linked, int Level, int Games, int Wins, int Xp = 0, string Rank = "", int Kills = 0);

/// <summary>Letzte vom Bot ausgewertete Runde des Spielers.</summary>
internal sealed record LastRound(int AgoSeconds, string Role, string Team, bool Won, int Xp, int Kills, int Tasks, int TasksTotal);

internal sealed record StatsReply(PlayerStats? Me, LastRound? Last, IReadOnlyList<PlayerStats> Players, int XpPerLevel);

internal sealed record LinkReply(bool Ok, string Code, int Minutes, bool AlreadyLinked, string? Error);

/// <summary>
/// Spielunabhängiger Teil der Komfort-Funktionen (getestet in Tests/): Anfragen bauen, Antworten des Bots lesen,
/// Texte fürs Overlay. JSON-Format ↔ Bot: services/ModPlayerInfo.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class PlayerInfoRules
{
    public const string StatsRequest = "stats";
    public const string StatsReplyKind = "stats-reply";
    public const string LinkRequest = "link";
    public const string LinkReplyKind = "link-reply";
    public const int MaxPlayers = 15;

    public static string StatsRequestContent(string? me, IEnumerable<string> players) =>
        JsonSerializer.Serialize(new
        {
            crewstats = StatsRequest,
            v = 1,
            me = string.IsNullOrWhiteSpace(me) ? null : me,
            players = players.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxPlayers).ToArray(),
        });

    public static string LinkRequestContent(string friendCode) =>
        JsonSerializer.Serialize(new { crewstats = LinkRequest, v = 1, fc = friendCode });

    /// <summary>Antwort auf eine Stats-Anfrage; null, solange der Bot noch nicht geantwortet hat.</summary>
    public static StatsReply? ParseStats(string? content)
    {
        var root = Parse(content, StatsReplyKind);
        if (root == null) return null;
        var r = root.Value;
        PlayerStats? me = null;
        LastRound? last = null;
        if (r.TryGetProperty("me", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            me = Stats(m);
            if (m.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.Object)
                last = new LastRound(Int(l, "ago"), Str(l, "role") ?? "", Str(l, "team") ?? "", Bool(l, "won"), Int(l, "xp"),
                    Int(l, "kills"), Int(l, "tasks"), Int(l, "tasksTotal"));
        }
        var players = new List<PlayerStats>();
        if (r.TryGetProperty("players", out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var item in p.EnumerateArray())
                players.Add(item.ValueKind == JsonValueKind.Object ? Stats(item) : new PlayerStats(false, 0, 0, 0));
        var perLevel = Int(r, "xpPerLevel");
        return new StatsReply(me, last, players, perLevel > 0 ? perLevel : 50);
    }

    public static LinkReply? ParseLink(string? content)
    {
        var root = Parse(content, LinkReplyKind);
        if (root == null) return null;
        var r = root.Value;
        var code = Str(r, "code") ?? "";
        return new LinkReply(Bool(r, "ok") && code.Length > 0, code, Int(r, "minutes"), Bool(r, "linked"), Str(r, "error"));
    }

    /// <summary>
    /// Gehört die letzte Runde des Bots zu unserer gerade beendeten Runde? Der Bot wertet erst nach dem Spielende aus,
    /// sein „vor N Sekunden“ darf also höchstens so lange her sein wie unser Spielende (+ etwas Spielraum für Uhren).
    /// </summary>
    public static bool IsThisRound(LastRound last, double secondsSinceGameEnd) => last.AgoSeconds <= secondsSinceGameEnd + 30;

    /// <summary>Rollenname für die Anzeige: Spiel-Rollen ("GuardianAngel") und TOR-nameKeys ("niceGuesser") lesbar.</summary>
    public static string RoleName(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "?";
        var text = new StringBuilder();
        for (var i = 0; i < role.Length; i++)
        {
            var c = role[i];
            if (i == 0) text.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c) && char.IsLower(role[i - 1])) text.Append(' ').Append(c);
            else text.Append(c);
        }
        return text.ToString();
    }

    public static string TeamName(string? team) => team switch
    {
        "IMPOSTOR" => "Impostor",
        "NEUTRAL" => "Neutral",
        "CREW" => "Crew",
        _ => "",
    };

    /// <summary>"Level 7 · 30/50 XP" – Fortschritt im aktuellen Level.</summary>
    public static string LevelLine(PlayerStats me, int xpPerLevel)
    {
        var perLevel = Math.Max(1, xpPerLevel);
        var inLevel = ((me.Xp % perLevel) + perLevel) % perLevel;
        return $"Level {me.Level} · {inLevel}/{perLevel} XP";
    }

    public static string WinRate(int wins, int games) => games <= 0 ? "–" : $"{Math.Round(100.0 * wins / games)} %";

    /// <summary>Nur sichtbare Zeichen und höchstens <paramref name="max"/> lang (Namen kommen aus dem Spiel).</summary>
    public static string Short(string? text, int max)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }

    private static PlayerStats Stats(JsonElement e) =>
        new(Bool(e, "linked"), Int(e, "level"), Int(e, "games"), Int(e, "wins"), Int(e, "xp"), Str(e, "rank") ?? "", Int(e, "kills"));

    private static JsonElement? Parse(string? content, string kind)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "crewstats") != kind) return null;
            return root.Clone();
        }
        catch (JsonException)
        {
            return null; // noch unsere eigene Anfrage o.ä.
        }
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
