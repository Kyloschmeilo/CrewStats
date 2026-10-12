using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Rollenwünsche aus Discord (/rollenwunsch für alle, /rolle-zuweisen für Admins), Config [Rollenwuensche] Enabled.
/// Nur als Host, nur in Online-Lobbys im normalen Modus (kein Hide &amp; Seek, kein Freeplay):
///   1. Start-Knopf: die Mod schickt die Friendcodes der Lobby (verschlüsselt) über den Webhook an den Bot. Der Bot
///      schreibt die offenen Wünsche dieser Spieler in genau diese Nachricht; die Mod liest sie und löscht sie.
///   2. Offen statt heimlich: der Host schreibt vor dem Start in den Chat, wie viele Rollen per Discord kommen.
///   3. Rollenvergabe (RoleManager.SelectRoles): die gewürfelten Rollen werden erst gesammelt, dann nach
///      <see cref="RoleWishPlanner"/> getauscht und erst danach an alle verschickt. Anzahl Impostor und
///      Spezialrollen bleiben wie eingestellt.
///   4. Rückmeldung an den Bot: erfüllt / nicht möglich (XP zurück) / nicht dabei (Wunsch bleibt offen).
/// Ohne Wünsche greift die Mod nicht in die Rollenvergabe ein. Ins Log kommen nur Anzahlen, keine Rollen.
/// Protokoll ↔ Bot: services/RoleWishes.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class RoleWishes
{
    private sealed record Wish(long Id, string FriendCode, string RoleName, bool Admin);

    private sealed record Captured(PlayerControl Player, RoleTypes Role, bool CanOverride);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxWishAge = TimeSpan.FromMinutes(2);

    // Zwischen Hintergrund-Abruf und Spiel-Thread geteilt
    private static readonly object Lock = new();
    private static int _generation; // jeder Start-Knopf / jede Rollenvergabe macht ältere Abrufe ungültig
    private static List<Wish>? _ready;
    private static DateTime _readyAt;
    private static bool _announced; // Chat-Hinweis ist raus – erst dann werden die Wünsche angewendet
    private static string? _chatHint;

    // Nur auf dem Spiel-Thread
    private static bool _capturing;
    private static int _captureFrame;
    private static List<Wish>? _active;
    private static readonly List<Captured> CapturedRoles = new();
    private static readonly HashSet<long> AppliedIds = new(); // falls eine Rückmeldung verloren geht: nie doppelt

    public static bool Enabled => CrewStatsPlugin.RoleWishesEnabled.Value;

    // ==========================================
    // 1. ABHOLEN (Start-Knopf)
    // ==========================================

    /// <summary>GameStartManager.BeginGame – der Host hat auf Start gedrückt (Countdown läuft).</summary>
    public static void OnStartPressed()
    {
        if (!Enabled || !IsNormalOnlineHost()) return;
        var url = RoundUploader.WebhookUrl;
        var token = url != null ? WishCrypto.TokenOf(url) : null;
        if (url == null || token == null || GameData.Instance == null) return;

        var players = new List<string>();
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null || info.Disconnected) continue;
            var code = Normalize(info.FriendCode);
            if (code.Length > 0 && !players.Contains(code)) players.Add(code);
        }
        if (players.Count == 0) return;

        int generation;
        lock (Lock)
        {
            generation = ++_generation;
            _ready = null;
            _announced = false;
            _chatHint = null;
        }
        Task.Run(() => FetchAsync(BaseUrl(url), token, players, generation));
    }

    private static async Task FetchAsync(string url, string token, List<string> players, int generation)
    {
        List<Wish>? wishes;
        try
        {
            wishes = await RequestAsync(url, token, players);
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Rollenwünsche: Abruf fehlgeschlagen: {exc.Message}");
            return;
        }
        if (wishes == null) return;

        bool current;
        lock (Lock)
        {
            current = generation == _generation;
            if (current)
            {
                _ready = wishes;
                _readyAt = DateTime.UtcNow;
                _chatHint = ChatHint(wishes);
                _announced = _chatHint == null; // ohne Wünsche gibt es nichts anzukündigen
            }
        }
        if (current)
        {
            CrewStatsPlugin.Logger.LogInfo($"Rollenwünsche: {wishes.Count} für diese Lobby abgeholt.");
        }
        else if (wishes.Count > 0)
        {
            // Zu spät für diese Runde (Rollen schon vergeben) – beim Bot wieder freigeben, sie gelten dann für die nächste
            await SendResultsAsync(url, token, wishes.Select(w => Result(w.Id, "absent")).ToList());
        }
    }

    /// <summary>Anfrage senden, auf die Antwort des Bots warten (er bearbeitet unsere Nachricht), Nachricht löschen.</summary>
    private static async Task<List<Wish>?> RequestAsync(string url, string token, List<string> players)
    {
        var request = WishCrypto.Seal(WishCrypto.Request, JsonSerializer.Serialize(new { players }), token);
        using var posted = await Http.PostAsync(url + "?wait=true", Body(request));
        if (!posted.IsSuccessStatusCode)
        {
            CrewStatsPlugin.Logger.LogWarning($"Rollenwünsche: Discord hat die Anfrage abgelehnt: {(int)posted.StatusCode} {posted.ReasonPhrase}");
            return null;
        }
        string messageId;
        using (var json = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
            messageId = json.RootElement.GetProperty("id").GetString() ?? "";
        var messageUrl = $"{url}/messages/{messageId}";

        try
        {
            var deadline = DateTime.UtcNow + ReplyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                using var response = await Http.GetAsync(messageUrl);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(RetryAfter(response));
                    continue;
                }
                if (!response.IsSuccessStatusCode) continue;

                string? content;
                using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    content = json.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
                var reply = WishCrypto.Open(WishCrypto.Reply, content, token);
                if (reply != null) return ParseWishes(reply);
            }
            CrewStatsPlugin.Logger.LogWarning("Rollenwünsche: keine Antwort vom Bot (läuft er?) – diese Runde ohne Wünsche.");
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

    private static List<Wish> ParseWishes(string json)
    {
        var wishes = new List<Wish>();
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.GetProperty("wishes").EnumerateArray())
        {
            wishes.Add(new Wish(
                item.GetProperty("id").GetInt64(),
                Normalize(item.GetProperty("fc").GetString()),
                item.GetProperty("role").GetString() ?? "",
                item.TryGetProperty("admin", out var admin) && admin.GetBoolean()));
        }
        return wishes;
    }

    // ==========================================
    // 2. CHAT-HINWEIS (Spiel-Thread)
    // ==========================================

    /// <summary>
    /// Jeden Frame (HudManager.Update): den Hinweis des letzten Abrufs in den Lobby-Chat schreiben. Läuft die Runde
    /// schon, bevor er raus ist, werden die Wünsche diesmal nicht angewendet (nichts passiert heimlich).
    /// </summary>
    public static void Tick()
    {
        if (Volatile.Read(ref _chatHint) == null) return;
        try
        {
            var client = AmongUsClient.Instance;
            var local = PlayerControl.LocalPlayer;
            if (client == null || !client.AmHost || local == null) return;
            if (client.GameState != InnerNetClient.GameStates.Joined) return; // Runde läuft schon – zu spät
            string? hint;
            lock (Lock)
            {
                hint = _chatHint;
                _chatHint = null;
            }
            if (hint == null) return;
            local.RpcSendChat(hint);
            lock (Lock) _announced = true;
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Rollenwünsche: Chat-Hinweis fehlgeschlagen: {exc}");
        }
    }

    private static string? ChatHint(List<Wish> wishes)
    {
        var wished = wishes.Count(w => !w.Admin);
        var assigned = wishes.Count(w => w.Admin);
        if (wished + assigned == 0) return null;
        var parts = new List<string>();
        if (wished > 0) parts.Add(wished == 1 ? "1 Wunsch" : $"{wished} Wünsche");
        if (assigned > 0) parts.Add($"{assigned} vom Admin festgelegt");
        return $"CrewStats: Rollen per Discord in dieser Runde - {string.Join(", ", parts)}.";
    }

    // ==========================================
    // 3. ROLLENVERGABE (Spiel-Thread)
    // ==========================================

    /// <summary>RoleManager.SelectRoles (Prefix): gibt es Wünsche, werden die gewürfelten Rollen erst gesammelt.</summary>
    public static void BeforeSelectRoles()
    {
        _capturing = false;
        _active = null;
        CapturedRoles.Clear();

        List<Wish>? wishes;
        bool usable;
        lock (Lock)
        {
            wishes = _ready;
            usable = wishes != null && _announced && DateTime.UtcNow - _readyAt <= MaxWishAge;
            _ready = null;
            _chatHint = null;
            _announced = false;
            _generation++; // ein noch laufender Abruf kommt zu spät
        }
        if (wishes == null) return;
        var alreadyApplied = wishes.Where(w => AppliedIds.Contains(w.Id)).ToList();
        if (alreadyApplied.Count > 0) ReportResults(alreadyApplied.Select(w => Result(w.Id, "applied")).ToList()); // Rückmeldung ging verloren
        wishes = wishes.Where(w => !AppliedIds.Contains(w.Id)).ToList();
        if (wishes.Count == 0) return;
        if (!usable || !Enabled || !IsNormalHost())
        {
            // Nicht angekündigt oder zu alt: diesmal nicht – beim Bot wieder freigeben (gilt dann für die nächste Runde)
            ReportAbsent(wishes);
            return;
        }

        _active = wishes;
        _capturing = true;
        _captureFrame = Time.frameCount;
    }

    /// <summary>PlayerControl.RpcSetRole (Prefix): false = noch nicht senden, erst sammeln.</summary>
    public static bool OnRpcSetRole(PlayerControl player, RoleTypes role, bool canOverride)
    {
        if (!_capturing || player == null) return true;
        if (Time.frameCount != _captureFrame)
        {
            // Die Rollenvergabe ist ohne Postfix zu Ende gegangen (Fehler im Spiel) – Gesammeltes normal verschicken
            Release();
            return true;
        }
        var index = CapturedRoles.FindIndex(c => c.Player.PlayerId == player.PlayerId);
        var captured = new Captured(player, role, canOverride);
        if (index >= 0) CapturedRoles[index] = captured;
        else CapturedRoles.Add(captured);
        return false;
    }

    /// <summary>RoleManager.SelectRoles (Postfix): Wünsche einplanen und alle Rollen verschicken.</summary>
    public static void AfterSelectRoles()
    {
        if (!_capturing) return;
        _capturing = false;
        var captured = CapturedRoles.ToList();
        CapturedRoles.Clear();
        var wishes = _active ?? new List<Wish>();
        _active = null;

        var roles = captured.Select(c => c.Role).ToArray();
        List<object> results;
        try
        {
            results = Plan(captured, wishes, roles);
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Rollenwünsche: Fehler – Runde läuft mit den gewürfelten Rollen: {exc}");
            roles = captured.Select(c => c.Role).ToArray();
            results = wishes.Select(w => Result(w.Id, "absent")).ToList();
        }
        finally
        {
            // Immer alle Rollen verschicken – sonst hätte niemand eine Rolle
            for (var i = 0; i < captured.Count; i++)
            {
                try
                {
                    captured[i].Player.RpcSetRole(roles[i], captured[i].CanOverride);
                }
                catch (Exception exc)
                {
                    CrewStatsPlugin.Logger.LogError($"Rollenwünsche: Rolle konnte nicht gesetzt werden: {exc}");
                }
            }
        }

        ReportResults(results);
    }

    /// <summary>Schreibt die neuen Rollen in <paramref name="roles"/> und liefert die Rückmeldung für den Bot.</summary>
    private static List<object> Plan(List<Captured> captured, List<Wish> wishes, RoleTypes[] roles)
    {
        if (captured.Count == 0)
        {
            // Das Spiel hat die Rollen anders vergeben als erwartet (Spiel-Update?) – Wünsche erstatten lassen
            CrewStatsPlugin.Logger.LogWarning("Rollenwünsche: Rollenvergabe nicht erkannt – Wünsche werden erstattet.");
            return wishes.Select(w => Result(w.Id, "failed", "failed")).ToList();
        }
        var options = GameOptionsManager.Instance.CurrentGameOptions;
        var planner = new RoleWishPlanner(
            captured.Select(c => (c.Player.PlayerId, (int)c.Role)),
            role => RoleManager.IsImpostorRole((RoleTypes)role),
            role => MaxCount(options, (RoleTypes)role),
            (int)RoleTypes.Crewmate,
            (int)RoleTypes.Impostor);

        var playerByCode = new Dictionary<string, byte>();
        foreach (var c in captured)
        {
            var code = Normalize(c.Player.Data != null ? c.Player.Data.FriendCode : null);
            if (code.Length > 0) playerByCode.TryAdd(code, c.Player.PlayerId);
        }

        var results = new List<object>();
        int applied = 0, failed = 0, absent = 0;
        foreach (var wish in wishes.OrderBy(w => w.Admin ? 0 : 1).ThenBy(w => w.Id))
        {
            if (!playerByCode.TryGetValue(wish.FriendCode, out var playerId))
            {
                results.Add(Result(wish.Id, "absent"));
                absent++;
                continue;
            }
            if (!Enum.TryParse<RoleTypes>(wish.RoleName, out var role) || !Enum.IsDefined(typeof(RoleTypes), role))
            {
                results.Add(Result(wish.Id, "failed", "unknownRole"));
                failed++;
                continue;
            }
            if (RoleManager.IsGhostRole(role))
            {
                results.Add(Result(wish.Id, "failed", "notAllowed"));
                failed++;
                continue;
            }

            switch (planner.Apply(playerId, (int)role))
            {
                case RoleWishPlanner.Outcome.Applied:
                    results.Add(Result(wish.Id, "applied"));
                    AppliedIds.Add(wish.Id);
                    applied++;
                    break;
                case RoleWishPlanner.Outcome.Disabled:
                    results.Add(Result(wish.Id, "failed", "disabled"));
                    failed++;
                    break;
                case RoleWishPlanner.Outcome.Absent:
                    results.Add(Result(wish.Id, "absent"));
                    absent++;
                    break;
                default:
                    results.Add(Result(wish.Id, "failed", "noSlot"));
                    failed++;
                    break;
            }
        }

        var planned = planner.Result;
        for (var i = 0; i < roles.Length; i++) roles[i] = (RoleTypes)planned[i].Role;
        CrewStatsPlugin.Logger.LogInfo($"Rollenwünsche: {applied} erfüllt, {failed} nicht möglich, {absent} nicht in der Runde.");
        return results;
    }

    /// <summary>Wie oft es eine Spezialrolle laut Lobby-Einstellungen höchstens gibt (0 = aus).</summary>
    private static int MaxCount(IGameOptions options, RoleTypes role)
    {
        try
        {
            var count = options.RoleOptions.GetNumPerGame(role);
            var chance = options.RoleOptions.GetChancePerGame(role);
            return chance > 0 ? Math.Max(0, count) : 0;
        }
        catch
        {
            return 0; // Rolle kennt die Options-Version nicht
        }
    }

    /// <summary>Spielende/Verlassen: nichts mehr sammeln, nichts Altes mehr anwenden.</summary>
    public static void Reset()
    {
        if (_capturing) Release();
        lock (Lock)
        {
            _ready = null;
            _announced = false;
            _chatHint = null;
            _generation++;
        }
    }

    /// <summary>Notfall: gesammelte Rollen unverändert verschicken (die Wünsche bleiben beim Bot offen).</summary>
    private static void Release()
    {
        _capturing = false;
        _active = null;
        var captured = CapturedRoles.ToList();
        CapturedRoles.Clear();
        foreach (var c in captured)
        {
            try
            {
                c.Player.RpcSetRole(c.Role, c.CanOverride);
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogError($"Rollenwünsche: Rolle konnte nicht gesetzt werden: {exc}");
            }
        }
    }

    // ==========================================
    // 4. RÜCKMELDUNG
    // ==========================================

    private static object Result(long id, string status, string? reason = null) =>
        reason == null ? new { id, status } : new { id, status, reason };

    private static async Task SendResultsAsync(string url, string token, List<object> results)
    {
        if (results.Count == 0) return;
        var content = WishCrypto.Seal(WishCrypto.Result, JsonSerializer.Serialize(new { results }), token);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await Http.PostAsync(url, Body(content));
                if (response.IsSuccessStatusCode) return;
                CrewStatsPlugin.Logger.LogWarning($"Rollenwünsche: Rückmeldung abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests) await Task.Delay(RetryAfter(response));
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogWarning($"Rollenwünsche: Rückmeldung fehlgeschlagen: {exc.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }
    }

    private static void ReportAbsent(List<Wish> wishes) => ReportResults(wishes.Select(w => Result(w.Id, "absent")).ToList());

    private static void ReportResults(List<object> results)
    {
        var url = RoundUploader.WebhookUrl;
        var token = url != null ? WishCrypto.TokenOf(url) : null;
        if (url == null || token == null) return;
        Task.Run(() => SendResultsAsync(BaseUrl(url), token, results));
    }

    // ---------- intern ----------

    /// <summary>Webhook-URL ohne Anhängsel (…/api/webhooks/&lt;id&gt;/&lt;token&gt;).</summary>
    private static string BaseUrl(string url) => url.Split('?', '#')[0].TrimEnd('/');

    /// <summary>Webhook-Nachricht: kein Ping, keine Benachrichtigung im Channel.</summary>
    private static StringContent Body(string content) =>
        new(JsonSerializer.Serialize(new
        {
            username = "CrewStats",
            content,
            flags = 4096,
            allowed_mentions = new { parse = Array.Empty<string>() },
        }), Encoding.UTF8, "application/json");

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.2, 5));
    }

    private static string Normalize(string? friendCode) => (friendCode ?? "").Trim().ToLowerInvariant();

    private static bool IsNormalHost()
    {
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost) return false;
        if (client.NetworkMode == NetworkModes.FreePlay && !CrewStatsPlugin.RecordFreeplay.Value) return false;
        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        return options != null && options.GameMode is GameModes.Normal or GameModes.NormalFools;
    }

    private static bool IsNormalOnlineHost() =>
        IsNormalHost() && AmongUsClient.Instance.NetworkMode == NetworkModes.OnlineGame;
}
