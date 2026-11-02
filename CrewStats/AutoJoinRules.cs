using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CrewStats;

/// <summary>Antwort des Bots auf eine Auto-Join-Anfrage (siehe <see cref="AutoJoin"/>).</summary>
/// <remarks>Region/Server: Server-Region des Hosts (Name + Adresse), leer bei Host-Mods vor 1.7.1 oder altem Bot.</remarks>
internal sealed record LobbyInfo(string State, string Code, string Map, int Players, int Max, string Host,
    string Region = "", string Server = "");

/// <summary>Eine Server-Region, wie sie im Spiel zur Auswahl steht (für <see cref="AutoJoinRules.FindRegion"/>).</summary>
internal sealed record RegionEntry(string Name, IReadOnlyList<string> Addresses);

/// <summary>
/// Spielunabhängiger Teil von Auto-Join (getestet in Tests/): Antwort lesen und entscheiden, ob beigetreten wird.
/// JSON-Format ↔ Bot: services/AutoJoin.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class AutoJoinRules
{
    public const string Request = "join";
    public const string Reply = "join-reply";

    public enum Decision { Join, NoLobby, Started, Full, AlreadyJoined, InvalidCode }

    /// <summary>Nachrichtentext der Anfrage.</summary>
    public static string RequestContent() => JsonSerializer.Serialize(new { crewstats = Request, v = 1 });

    /// <summary>Liest die (vom Bot bearbeitete) Nachricht; null, solange der Bot noch nicht geantwortet hat.</summary>
    public static LobbyInfo? ParseReply(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "crewstats") != Reply) return null;
            return new LobbyInfo(
                Str(root, "state") ?? "none",
                (Str(root, "code") ?? "").Trim().ToUpperInvariant(),
                Str(root, "map") ?? "?",
                Int(root, "players"),
                Int(root, "max"),
                Str(root, "host") ?? "",
                (Str(root, "region") ?? "").Trim(),
                (Str(root, "server") ?? "").Trim());
        }
        catch (JsonException)
        {
            return null; // noch unsere eigene Anfrage o.ä.
        }
    }

    /// <summary>
    /// Beitreten? Automatisch nur einmal pro Lobby (wer sie verlässt, bleibt draußen); per Taste (manual) immer.
    /// </summary>
    public static Decision Decide(LobbyInfo lobby, ISet<string> joinedCodes, bool manual)
    {
        if (lobby.State == "started") return Decision.Started;
        if (lobby.State != "open" || lobby.Code.Length == 0) return Decision.NoLobby;
        if (!IsValidCode(lobby.Code)) return Decision.InvalidCode;
        if (lobby.Max > 0 && lobby.Players >= lobby.Max) return Decision.Full;
        if (!manual && joinedCodes.Contains(lobby.Code)) return Decision.AlreadyJoined;
        return Decision.Join;
    }

    /// <summary>Among-Us-Codes: 6 Buchstaben (V2) oder 4 Buchstaben (alt).</summary>
    public static bool IsValidCode(string code)
    {
        if (code.Length != 6 && code.Length != 4) return false;
        foreach (var c in code)
            if (c < 'A' || c > 'Z') return false;
        return true;
    }

    /// <summary>
    /// Lobby-Codes gelten nur auf dem Server, auf dem die Lobby erstellt wurde – mit einer anderen Region meldet das
    /// Spiel „Spiel nicht gefunden“. Sucht die Region des Hosts unter den eigenen: zuerst nach Adresse (eindeutig,
    /// Namen sind je nach Region-Mod verschieden), dann nach Name. -1 = nicht vorhanden, null = Host hat keine
    /// Region gemeldet (dann in der aktuellen Region versuchen).
    /// </summary>
    public static int? FindRegion(IReadOnlyList<RegionEntry> available, string region, string server)
    {
        var address = NormalizeAddress(server);
        var name = region.Trim();
        if (address.Length == 0 && name.Length == 0) return null;
        if (address.Length > 0)
            for (var i = 0; i < available.Count; i++)
                foreach (var candidate in available[i].Addresses)
                    if (NormalizeAddress(candidate) == address) return i;
        if (name.Length > 0)
            for (var i = 0; i < available.Count; i++)
                if (string.Equals(available[i].Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>„https://Matchmaker-EU.among.us:443/“ → „matchmaker-eu.among.us“.</summary>
    public static string NormalizeAddress(string? address)
    {
        var text = (address ?? "").Trim().ToLowerInvariant();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];
        var colon = text.LastIndexOf(':');
        if (colon >= 0) text = text[..colon];
        return text;
    }

    /// <summary>Gültige Webhook-URL (…/api/webhooks/&lt;id&gt;/&lt;token&gt;) ohne Anhängsel, sonst null.</summary>
    public static string? NormalizeUrl(string? url)
    {
        var trimmed = (url ?? "").Trim().Split('?', '#')[0].TrimEnd('/');
        if (!trimmed.StartsWith("https://") || !trimmed.Contains("/api/webhooks/")) return null;
        var rest = trimmed[(trimmed.IndexOf("/api/webhooks/", StringComparison.Ordinal) + "/api/webhooks/".Length)..].Split('/');
        return rest.Length == 2 && rest[0].Length > 0 && rest[1].Length > 0 ? trimmed : null;
    }

    private static string? Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
}
