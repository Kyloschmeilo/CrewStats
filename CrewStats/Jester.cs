using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Jester-Modus für normales Among Us (Config [Jester] Enabled, Standard aus). Nur der Host braucht die Mod –
/// alle anderen spielen ohne Mod. Mit The Other Roles ist er aus (TOR hat einen eigenen Jester).
///   1. Start-Knopf: die Mod fragt den Bot (verschlüsselt, wie bei den Rollenwünschen), wer in der Lobby mit
///      Discord verknüpft ist – nur die können erfahren, dass sie Jester sind.
///   2. Intro: ein zufälliger verknüpfter Crewmate wird Jester (echter Zufall, Chance [Jester] Chance). Der Bot
///      schickt ihm eine DM. Im Spiel bleibt er Crewmate; der Host sieht nicht, wer es ist (auch nicht im Log).
///   3. Wird der Jester rausgewählt, endet das Spiel und nur er gewinnt: andere Spielenden sind kurz gesperrt, der
///      Host gibt dem Jester die Geister-Rolle der Impostor und allen Impostorn die der Crew (dieselben Rollen,
///      die das Spiel jedem Toten gibt) und beendet das Spiel mit „Impostor gewinnen durch Rauswählen“. So zeigt
///      jedes unveränderte Spiel nur den Jester als Gewinner.
///   4. Rundendaten: Jester = Team NEUTRAL, modRole "jester", Sieg = winningTeam NEUTRAL, endReason "JesterWin"
///      (wie der Jester von The Other Roles – der Bot gibt dafür Neutral-Sieg-XP, Wetten sind verloren).
/// Protokoll ↔ Bot: services/RoleWishes.kt (WishProtocol.JESTER*) – beide Seiten gemeinsam ändern!
/// </summary>
internal static class Jester
{
    public const string Request = "jester";
    public const string Reply = "jester-reply";
    public const string Pick = "jester-pick";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxLinkedAge = TimeSpan.FromMinutes(2);

    /// <summary>Nach dem Rauswurf: erst Geister-Rollen setzen, dann (in Ruhe, damit sie sicher ankommen) Spielende.</summary>
    private const float SwapDelay = 0.3f;
    private const float EndDelay = 1.2f;

    // Zwischen Hintergrund-Abruf und Spiel-Thread geteilt
    private static readonly object Lock = new();
    private static int _generation;
    private static List<string>? _linked;
    private static DateTime _linkedAt;

    // Nur auf dem Spiel-Thread
    private static byte? _jester;
    private static bool _winPending;
    private static float _swapAt, _endAt;
    private static bool _swapped, _ended;
    private static readonly Dictionary<byte, string> RolesBeforeWin = new();

    private static bool Enabled => CrewStatsPlugin.JesterEnabled.Value;

    /// <summary>Solange der Jester-Sieg läuft: kein anderes Spielende (LogicGameFlowNormal.CheckEndCriteria).</summary>
    public static bool WinPending => _winPending;

    private static bool Usable() => Enabled && !TorBridge.Loaded && RoleWishes.IsNormalOnlineHost();

    // ==========================================
    // 1. START-KNOPF: wer ist verknüpft?
    // ==========================================

    public static void OnStartPressed()
    {
        lock (Lock)
        {
            _generation++;
            _linked = null;
        }
        if (!Usable() || GameData.Instance == null) return;
        var url = RoundUploader.WebhookUrl;
        var token = url != null ? WishCrypto.TokenOf(url) : null;
        if (url == null || token == null) return;

        var players = new List<string>();
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null || info.Disconnected) continue;
            var code = JesterRules.Normalize(info.FriendCode);
            if (code.Length > 0 && !players.Contains(code)) players.Add(code);
        }
        if (players.Count == 0) return;

        int generation;
        lock (Lock) generation = _generation;
        Task.Run(() => FetchLinkedAsync(RoleWishes.BaseUrl(url), token, players, generation));
    }

    private static async Task FetchLinkedAsync(string url, string token, List<string> players, int generation)
    {
        try
        {
            var request = WishCrypto.Seal(Request, JsonSerializer.Serialize(new { players }), token);
            using var posted = await Http.PostAsync(url + "?wait=true", RoleWishes.Body(request));
            if (!posted.IsSuccessStatusCode)
            {
                CrewStatsPlugin.Logger.LogWarning($"Jester: Discord hat die Anfrage abgelehnt: {(int)posted.StatusCode} {posted.ReasonPhrase}");
                return;
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
                        await Task.Delay(RoleWishes.RetryAfter(response));
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) continue;
                    string? content;
                    using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                        content = json.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var reply = WishCrypto.Open(Reply, content, token);
                    if (reply == null) continue;

                    var linked = new List<string>();
                    using (var json = JsonDocument.Parse(reply))
                    {
                        foreach (var item in json.RootElement.GetProperty("linked").EnumerateArray())
                            linked.Add(JesterRules.Normalize(item.GetString()));
                    }
                    lock (Lock)
                    {
                        if (generation != _generation) return;
                        _linked = linked;
                        _linkedAt = DateTime.UtcNow;
                    }
                    CrewStatsPlugin.Logger.LogInfo($"Jester: {linked.Count} verknüpfte Spieler in der Lobby.");
                    return;
                }
                CrewStatsPlugin.Logger.LogWarning("Jester: keine Antwort vom Bot (läuft er? neuer Bot nötig) – diese Runde ohne Jester.");
            }
            finally
            {
                try
                {
                    using var _ = await Http.DeleteAsync(messageUrl);
                }
                catch
                {
                    // nicht schlimm, der Bot räumt nach 30 s auf
                }
            }
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Jester: Abruf fehlgeschlagen: {exc.Message}");
        }
    }

    // ==========================================
    // 2. INTRO: Jester auswählen
    // ==========================================

    /// <summary>IntroCutscene.CoBegin – die Rollen stehen fest.</summary>
    public static void OnGameStart()
    {
        ResetRound();
        List<string>? linked;
        bool fresh;
        lock (Lock)
        {
            linked = _linked;
            fresh = linked != null && DateTime.UtcNow - _linkedAt <= MaxLinkedAge;
            _linked = null;
            _generation++;
        }
        if (!Usable() || !fresh || linked == null) return;

        var players = new List<JesterRules.Candidate>();
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            var info = control != null ? control.Data : null;
            if (info == null || info.Disconnected || info.Role == null) continue;
            players.Add(new JesterRules.Candidate(control!.PlayerId, info.FriendCode ?? "", !info.Role.IsImpostor));
        }
        var pick = JesterRules.Pick(players, linked, CrewStatsPlugin.JesterChance.Value, RandomNumberGenerator.GetInt32);
        if (pick == null)
        {
            CrewStatsPlugin.Logger.LogInfo("Jester: diese Runde keiner (Chance, zu wenige Crewmates oder niemand verknüpft).");
            return;
        }
        _jester = pick;
        var friendCode = players.First(p => p.PlayerId == pick.Value).FriendCode;
        var lobby = GameCode.IntToGameName(AmongUsClient.Instance.GameId) ?? "";
        SendPick(JesterRules.Normalize(friendCode), lobby);
        CrewStatsPlugin.Logger.LogInfo("Jester: gewählt, der Bot schickt ihm eine DM (wer es ist, steht nicht im Log).");
    }

    private static void SendPick(string friendCode, string lobby)
    {
        var url = RoundUploader.WebhookUrl;
        var token = url != null ? WishCrypto.TokenOf(url) : null;
        if (url == null || token == null) return;
        var content = WishCrypto.Seal(Pick, JsonSerializer.Serialize(new { fc = friendCode, code = lobby }), token);
        Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var response = await Http.PostAsync(RoleWishes.BaseUrl(url), RoleWishes.Body(content));
                    if (response.IsSuccessStatusCode) return;
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) await Task.Delay(RoleWishes.RetryAfter(response));
                }
                catch (Exception exc)
                {
                    CrewStatsPlugin.Logger.LogWarning($"Jester: Meldung an den Bot fehlgeschlagen: {exc.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        });
    }

    // ==========================================
    // 3. RAUSWURF → SIEG
    // ==========================================

    /// <summary>PlayerControl.Exiled (Postfix, auch beim Host): war es der Jester, gewinnt er.</summary>
    public static void OnExiled(PlayerControl player)
    {
        if (_jester == null || _winPending || player == null || player.PlayerId != _jester.Value) return;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        _winPending = true;
        _swapAt = Time.time + SwapDelay; // nach der Geister-Rolle, die das Spiel dem Rausgewählten gibt
        _endAt = Time.time + EndDelay;
        CrewStatsPlugin.Logger.LogInfo("Jester: rausgewählt – er gewinnt, Spielende.");
    }

    /// <summary>HudManager.Update.</summary>
    public static void Tick()
    {
        if (!_winPending || _ended) return;
        if (!_swapped && Time.time >= _swapAt)
        {
            _swapped = true;
            SwapRoles();
        }
        if (_swapped && Time.time >= _endAt)
        {
            _ended = true;
            try
            {
                GameManager.Instance.RpcEndGame(GameOverReason.ImpostorsByVote, false);
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogError($"Jester: Spielende fehlgeschlagen – Spiel läuft normal weiter: {exc}");
                _winPending = false; // normale Spielenden wieder zulassen
            }
        }
    }

    /// <summary>Siegerbild: nur der Jester steht im Team der Gewinner (Impostor), alle Impostor in dem der Crew.</summary>
    private static void SwapRoles()
    {
        RolesBeforeWin.Clear();
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            var info = control != null ? control.Data : null;
            if (info == null || info.Disconnected || info.Role == null) continue;
            RoleTypes? ghost = control!.PlayerId == _jester ? RoleTypes.ImpostorGhost
                : info.Role.IsImpostor ? RoleTypes.CrewmateGhost
                : null;
            if (ghost == null) continue;
            RolesBeforeWin[control.PlayerId] = info.RoleType.ToString();
            try
            {
                control.RpcSetRole(ghost.Value, true);
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogError($"Jester: Rolle fürs Siegerbild nicht gesetzt: {exc}");
            }
        }
    }

    // ==========================================
    // 4. RUNDENDATEN + ANZEIGE
    // ==========================================

    /// <summary>GameRecorder.OnGameEnd (ohne The Other Roles), vor dem Senden.</summary>
    public static void ApplyToRound(RoundRecord round)
    {
        if (_jester == null) return;
        JesterRules.Apply(round, _jester.Value, _ended, RolesBeforeWin);
    }

    /// <summary>EndGameManager.SetEverythingUp (nur bei dir als Host zu sehen – die anderen haben die Mod nicht).</summary>
    public static void ShowEndScreen(EndGameManager manager)
    {
        if (!_ended || manager == null || manager.WinText == null) return;
        manager.WinText.text = "Der Jester gewinnt!";
        manager.WinText.color = new Color(0.77f, 0.58f, 0.93f);
    }

    /// <summary>Neue Runde / Verlassen.</summary>
    public static void ResetRound()
    {
        _jester = null;
        _winPending = false;
        _swapped = false;
        _ended = false;
        RolesBeforeWin.Clear();
    }

    /// <summary>Spiel verlassen: auch einen noch laufenden Abruf verwerfen.</summary>
    public static void Reset()
    {
        ResetRound();
        lock (Lock)
        {
            _linked = null;
            _generation++;
        }
    }
}
