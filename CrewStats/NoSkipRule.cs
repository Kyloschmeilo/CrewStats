using InnerNet;

namespace CrewStats;

/// <summary>
/// Hausregel „Kein Skip bei Notfall“ (Schalter im ESC-Menü, Config [Regeln] NoSkipOnEmergency):
/// Kam das Meeting über den Notfall-Knopf, nimmt der Host Skip-Stimmen sofort wieder zurück
/// (RpcClearVote – wie bei einem Disconnect). Der Spieler kann dann neu abstimmen; das klappt
/// auch bei Mitspielern ohne Mod. Beim Meeting-Start schreibt der Host einen Hinweis in den Chat.
/// Gar nicht abzustimmen (Zeit ablaufen lassen) bleibt möglich.
/// </summary>
internal static class NoSkipRule
{
    private const string ChatHint = "Notfall-Meeting: Skippen ist nicht erlaubt - bitte jemanden wählen!";

    private static bool _emergencyMeeting;

    public static bool Enabled
    {
        get => CrewStatsPlugin.NoSkipOnEmergency.Value;
        set => CrewStatsPlugin.NoSkipOnEmergency.Value = value;
    }

    private static bool Active
    {
        get
        {
            var client = AmongUsClient.Instance;
            return Enabled && _emergencyMeeting && client != null && client.AmHost;
        }
    }

    /// <summary>PlayerControl.StartMeeting – läuft bei allen; ohne Leiche = Notfall-Knopf.</summary>
    public static void OnMeetingStarted(NetworkedPlayerInfo? body) => _emergencyMeeting = body == null;

    /// <summary>MeetingHud.Start – Hinweis in den Chat (nur der Host).</summary>
    public static void OnMeetingHudShown()
    {
        if (!Active) return;
        var local = PlayerControl.LocalPlayer;
        if (local != null) local.RpcSendChat(ChatHint);
        CrewStatsPlugin.Logger.LogInfo("Kein-Skip-Regel aktiv (Notfall-Meeting).");
    }

    /// <summary>
    /// MeetingHud.CastVote (läuft beim Host für jede Stimme). false = Stimme nicht zählen.
    /// </summary>
    public static bool AllowVote(MeetingHud meeting, PlayerId voter, PlayerId suspect)
    {
        if (!Active || suspect.Value != PlayerVoteArea.SkippedVote) return true;
        meeting.RpcClearVote(voter); // Abstimmungs-Buttons beim Spieler wieder freigeben
        CrewStatsPlugin.Logger.LogInfo($"Skip-Stimme von Spieler {voter.Value} abgelehnt (Notfall-Meeting).");
        return false;
    }
}
