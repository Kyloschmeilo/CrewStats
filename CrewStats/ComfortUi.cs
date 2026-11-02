using System;
using BepInEx.Configuration;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Anzeige der Komfort-Funktionen mit Unitys einfachem Sofort-GUI (OnGUI): Verbindungsstatus, Update-Hinweis,
/// eigene Stats + Lobby-Info (nur in der Lobby), Rundenzusammenfassung (Lobby/Hauptmenü) und das Einstellungsmenü
/// (Taste F7 oder Klick auf den Status). Während der Runde ist nur das Einstellungsmenü sichtbar, wenn man es
/// selbst öffnet – die Mod zeigt im Spiel nichts an, was einen Vorteil bringt.
/// Läuft auf einem eigenen, unsichtbaren Objekt (CrewStatsPlugin.Load → AddComponent). Jeder Fehler wird einmal
/// geloggt, danach zeichnet die Anzeige nichts mehr – das Spiel läuft normal weiter.
/// </summary>
public class ComfortUi : MonoBehaviour
{
    public ComfortUi(IntPtr ptr) : base(ptr)
    {
    }

    private const string Accent = "#1FBF7A";
    private const string Muted = "#9AA4AE";
    private const string Warn = "#FFD54F";
    private const string Bad = "#FF6B6B";

    private static bool _settingsOpen;
    private static bool _broken;
    private static GUIStyle? _text, _title, _small, _button, _toggle, _big;
    private static int _styleSize;
    private static float S => Mathf.Max(0.75f, Screen.height / 1080f);

    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(CrewStatsPlugin.SettingsKey.Value)) _settingsOpen = !_settingsOpen;
            Comfort.Tick();
        }
        catch (Exception exc)
        {
            Fail(exc);
        }
    }

    private void OnGUI()
    {
        if (_broken) return;
        try
        {
            Styles();
            var where = Comfort.Where;
            if (where != Comfort.Place.Game)
            {
                StatusLine();
                UpdateBanner();
                if (where == Comfort.Place.Lobby)
                {
                    var y = 0.30f * Screen.height;
                    if (CrewStatsPlugin.ShowOverlay.Value && BotClient.PlayerUrl != null) y = OwnStats(y) + 10 * S;
                    if (CrewStatsPlugin.ShowLobbyInfo.Value && BotClient.PlayerUrl != null) LobbyInfo(y);
                }
                if (CrewStatsPlugin.ShowRoundSummary.Value) Summary();
            }
            if (_settingsOpen) Settings();
        }
        catch (Exception exc)
        {
            Fail(exc);
        }
    }

    private static void Fail(Exception exc)
    {
        if (_broken) return;
        _broken = true;
        CrewStatsPlugin.Logger.LogError($"Komfort-Anzeige abgeschaltet (Spiel läuft normal weiter): {exc}");
    }

    // ---------- Bausteine ----------

    private static void Styles()
    {
        var size = Mathf.RoundToInt(15 * S);
        if (_text != null && size == _styleSize) return;
        _styleSize = size;
        _text = new GUIStyle(GUI.skin.label) { fontSize = size, richText = true, wordWrap = true };
        _text.normal.textColor = Color.white;
        _small = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(13 * S) };
        _title = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(13 * S), fontStyle = FontStyle.Bold };
        _title.normal.textColor = new Color(0.12f, 0.75f, 0.48f);
        _big = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(30 * S), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _big.normal.textColor = new Color(0.12f, 0.75f, 0.48f);
        _button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(13 * S), richText = true };
        _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = size, richText = true };
    }

    /// <summary>Dunkler Kasten (die Standard-Box ist halb durchsichtig, doppelt gezeichnet wird sie gut lesbar).</summary>
    private static void Panel(Rect rect)
    {
        GUI.Box(rect, "");
        GUI.Box(rect, "");
        GUI.Box(rect, "");
    }

    private static Rect R(float x, float y, float w, float h) => new(x, y, w, h);

    private static string C(string color, string text) => $"<color={color}>{text}</color>";

    // ---------- Verbindungsstatus (unten links) ----------

    private static void StatusLine()
    {
        if (!CrewStatsPlugin.ShowStatus.Value) return;
        var hasUrl = BotClient.PlayerUrl != null || RoundUploader.WebhookUrl != null;
        var (state, message, _) = ConnectionStatus.Current;
        string dot;
        if (!hasUrl)
        {
            dot = C(Muted, "●");
            message = "kein Bot eingetragen";
        }
        else
        {
            dot = state switch
            {
                ConnectionStatus.State.Ok => C(Accent, "●"),
                ConnectionStatus.State.Busy or ConnectionStatus.State.NoReply => C(Warn, "●"),
                ConnectionStatus.State.Error => C(Bad, "●"),
                _ => C(Muted, "●"),
            };
        }
        var pending = RoundUploader.PendingCount;
        var extra = pending > 0 && RoundUploader.WebhookUrl != null
            ? " · " + C(Warn, pending == 1 ? "1 Runde wartet aufs Senden" : $"{pending} Runden warten aufs Senden")
            : "";
        var text = $"{dot} CrewStats: {message}{extra}  {C(Muted, $"[{CrewStatsPlugin.SettingsKey.Value}]")}";
        var rect = R(10 * S, Screen.height - 34 * S, 620 * S, 28 * S);
        if (GUI.Button(rect, text, _small!)) _settingsOpen = !_settingsOpen;
    }

    // ---------- Update-Hinweis (oben Mitte) ----------

    private static void UpdateBanner()
    {
        var notice = Updater.Notice;
        if (!CrewStatsPlugin.ShowUpdateNotice.Value || notice == null) return;
        var w = 560 * S;
        var rect = R((Screen.width - w) / 2, 8 * S, w, 34 * S);
        Panel(rect);
        GUI.Label(R(rect.x + 10 * S, rect.y + 6 * S, w - 50 * S, 24 * S), C(Warn, notice), _small!);
        if (GUI.Button(R(rect.xMax - 34 * S, rect.y + 5 * S, 26 * S, 24 * S), "x", _button!)) Updater.Dismiss();
    }

    // ---------- Eigene Stats (Lobby, links) ----------

    private static float OwnStats(float y)
    {
        var w = 300 * S;
        var mine = Comfort.Mine;
        var lines = 3;
        var rect = R(10 * S, y, w, (30 + lines * 22) * S);
        Panel(rect);
        GUI.Label(R(rect.x + 10 * S, y + 6 * S, w - 50 * S, 20 * S), "DEINE STATS", _title!);
        if (GUI.Button(R(rect.xMax - 44 * S, y + 5 * S, 36 * S, 20 * S), "neu", _button!)) Comfort.RefreshNow();

        string text;
        if (mine?.Me == null)
        {
            text = C(Muted, ConnectionStatus.Current.State is ConnectionStatus.State.Error or ConnectionStatus.State.NoReply
                ? ConnectionStatus.Current.Message
                : "lädt …");
        }
        else if (!mine.Me.Linked)
        {
            text = $"Nicht mit Discord verknüpft – keine XP.\n{C(Accent, $"{CrewStatsPlugin.SettingsKey.Value} → Mit Discord verknüpfen")}";
        }
        else
        {
            var me = mine.Me;
            var rank = me.Rank.Length > 0 ? $"{PlayerInfoRules.Short(me.Rank, 24)} · " : "";
            var last = mine.Last != null
                ? $"\nZuletzt: {PlayerInfoRules.Short(mine.Last.Role, 22)} ({(mine.Last.Won ? C(Accent, "Sieg") : "Niederlage")}, +{mine.Last.Xp} XP)"
                : "";
            text = $"{rank}{PlayerInfoRules.LevelLine(me, mine.XpPerLevel)}\n" +
                   $"{me.Wins} Siege in {me.Games} Runden ({PlayerInfoRules.WinRate(me.Wins, me.Games)}) · {me.Kills} Kills{last}";
        }
        GUI.Label(R(rect.x + 10 * S, y + 28 * S, w - 20 * S, lines * 22 * S), text, _small!);
        return rect.yMax;
    }

    // ---------- Lobby-Info (Lobby, links unter den eigenen Stats) ----------

    private static void LobbyInfo(float y)
    {
        var rows = Comfort.Lobby;
        if (rows.Count == 0) return;
        var w = 300 * S;
        var lineH = 19 * S;
        var linked = 0;
        foreach (var row in rows)
            if (row.Stats is { Linked: true }) linked++;
        var rect = R(10 * S, y, w, 32 * S + rows.Count * lineH);
        Panel(rect);
        GUI.Label(R(rect.x + 10 * S, y + 6 * S, w - 20 * S, 20 * S), $"IN DER LOBBY · {linked}/{rows.Count} VERKNÜPFT", _title!);
        var ly = y + 28 * S;
        foreach (var row in rows)
        {
            var name = PlayerInfoRules.Short(row.Name, 14) + (row.Me ? C(Muted, " (du)") : "");
            var info = row.Stats switch
            {
                null => C(Muted, "…"),
                { Linked: false } => C(Muted, "nicht verknüpft"),
                var s => $"Lv {s.Level} · {s.Wins} Siege / {s.Games}",
            };
            var mark = row.Stats is { Linked: true } ? C(Accent, "●") : C(Muted, "○");
            GUI.Label(R(rect.x + 10 * S, ly, w - 20 * S, lineH), $"{mark} {name}  {info}", _small!);
            ly += lineH;
        }
    }

    // ---------- Rundenzusammenfassung (rechts) ----------

    private static void Summary()
    {
        var round = RoundSummary.Last;
        if (round == null) return;
        if ((DateTime.UtcNow - round.EndedAt).TotalMinutes > 5)
        {
            RoundSummary.Dismiss();
            return;
        }
        var w = 320 * S;
        var rect = R(Screen.width - w - 10 * S, 0.30f * Screen.height, w, 150 * S);
        Panel(rect);
        GUI.Label(R(rect.x + 10 * S, rect.y + 6 * S, w - 50 * S, 20 * S), "LETZTE RUNDE", _title!);
        if (GUI.Button(R(rect.xMax - 30 * S, rect.y + 5 * S, 22 * S, 20 * S), "x", _button!)) RoundSummary.Dismiss();

        var bot = round.Bot;
        var role = bot != null && bot.Role.Length > 0 ? bot.Role : PlayerInfoRules.RoleName(round.Role);
        var team = PlayerInfoRules.TeamName(bot?.Team.Length > 0 ? bot.Team : round.Team);
        bool? won = bot != null ? bot.Won : round.Won;
        var result = won == null ? "" : won.Value ? C(Accent, "Sieg") : C(Bad, "Niederlage");
        var kills = bot?.Kills ?? round.Kills;
        var tasks = round.TasksTotal > 0 ? $" · Aufgaben {round.Tasks}/{round.TasksTotal}" : "";
        string xp;
        if (bot != null) xp = $"XP: {C(Accent, $"+{bot.Xp}")}";
        else if (round.NotLinked) xp = C(Muted, $"Keine XP – nicht verknüpft ({CrewStatsPlugin.SettingsKey.Value})");
        else if (BotClient.PlayerUrl == null) xp = C(Muted, "XP: kein Bot eingetragen");
        else if (round.BotChecked || (DateTime.UtcNow - round.EndedAt).TotalSeconds > 100)
            xp = C(Muted, "XP: keine Auswertung (Host ohne CrewStats?)");
        else xp = C(Muted, "XP: wird ausgewertet …");

        var text = $"{PlayerInfoRules.Short(role, 30)}{(team.Length > 0 ? C(Muted, $" · {team}") : "")}\n" +
                   $"{(result.Length > 0 ? result + " · " : "")}{(round.Dead ? "tot" : "überlebt")}\n" +
                   $"Kills {kills}{tasks}\n{xp}";
        GUI.Label(R(rect.x + 10 * S, rect.y + 30 * S, w - 20 * S, 115 * S), text, _text!);
    }

    // ---------- Einstellungsmenü (Mitte) ----------

    private static readonly (string Section, string Label, Func<ConfigEntry<bool>> Entry)[] Toggles =
    {
        ("ANZEIGE", "Eigene Stats in der Lobby", () => CrewStatsPlugin.ShowOverlay),
        ("ANZEIGE", "Lobby-Info: wer ist verknüpft", () => CrewStatsPlugin.ShowLobbyInfo),
        ("ANZEIGE", "Rundenzusammenfassung", () => CrewStatsPlugin.ShowRoundSummary),
        ("ANZEIGE", "Verbindungsstatus unten links", () => CrewStatsPlugin.ShowStatus),
        ("UPDATES & SENDEN", "Update-Hinweis", () => CrewStatsPlugin.ShowUpdateNotice),
        ("UPDATES & SENDEN", "Auto-Update installieren", () => CrewStatsPlugin.AutoUpdate),
        ("UPDATES & SENDEN", "Ungesendete Runden automatisch nachsenden", () => CrewStatsPlugin.RetryUploads),
        ("UPDATES & SENDEN", "Auto-Join im Hauptmenü", () => CrewStatsPlugin.AutoJoinAutomatic),
        ("ALS HOST", "Auto-Mute im Discord", () => CrewStatsPlugin.AutoMuteEnabled),
        ("ALS HOST", "Lobby im Discord ankündigen", () => CrewStatsPlugin.AnnounceLobby),
        ("ALS HOST", "Rollenwünsche erfüllen", () => CrewStatsPlugin.RoleWishesEnabled),
        ("ALS HOST", "Rollen selbst würfeln (echter Zufall)", () => CrewStatsPlugin.ShuffleRoles),
        ("ALS HOST", "Hausregel: kein Skip bei Notfall", () => CrewStatsPlugin.NoSkipOnEmergency),
    };

    private static void Settings()
    {
        var w = 520 * S;
        var rowH = 26 * S;
        var h = (70 + Toggles.Length * 26 + 3 * 24 + 150) * S;
        var rect = R((Screen.width - w) / 2, Mathf.Max(10 * S, (Screen.height - h) / 2), w, h);
        Panel(rect);
        GUI.Label(R(rect.x + 14 * S, rect.y + 10 * S, w - 60 * S, 24 * S), $"CREWSTATS {CrewStatsPlugin.Version} · EINSTELLUNGEN", _title!);
        if (GUI.Button(R(rect.xMax - 34 * S, rect.y + 8 * S, 26 * S, 24 * S), "x", _button!)) _settingsOpen = false;

        var y = rect.y + 40 * S;
        var section = "";
        foreach (var (sec, label, entry) in Toggles)
        {
            if (sec != section)
            {
                section = sec;
                GUI.Label(R(rect.x + 14 * S, y, w - 28 * S, 22 * S), C(Muted, sec), _small!);
                y += 24 * S;
            }
            var config = entry();
            var value = GUI.Toggle(R(rect.x + 20 * S, y, w - 40 * S, rowH), config.Value, " " + label, _toggle!);
            if (value != config.Value) config.Value = value; // BepInEx speichert sofort in die .cfg
            y += rowH;
        }

        // Discord verknüpfen
        y += 8 * S;
        GUI.Label(R(rect.x + 14 * S, y, w - 28 * S, 22 * S), C(Muted, "DISCORD"), _small!);
        y += 24 * S;
        if (GUI.Button(R(rect.x + 20 * S, y, 220 * S, 28 * S), "Mit Discord verknüpfen", _button!)) Comfort.RequestLinkCode();
        var pending = RoundUploader.PendingCount;
        if (pending > 0 && GUI.Button(R(rect.x + 250 * S, y, 250 * S, 28 * S), $"{pending} Runde(n) jetzt nachsenden", _button!))
            RoundUploader.RetryNow();
        y += 34 * S;
        var code = Comfort.LinkCode;
        if (code.Length > 0 && DateTime.UtcNow < Comfort.LinkExpires)
        {
            GUI.Label(R(rect.x + 14 * S, y, w - 28 * S, 36 * S), code, _big!);
            y += 38 * S;
        }
        if (Comfort.LinkText.Length > 0) GUI.Label(R(rect.x + 20 * S, y, w - 40 * S, 44 * S), Comfort.LinkText, _small!);
    }
}
