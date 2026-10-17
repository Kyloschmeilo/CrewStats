using InnerNet;

namespace CrewStats;

/// <summary>
/// Hausregel „Kein Skip bei Notfall“ (Schalter im ESC-Menü, Config [Regeln] NoSkipOnEmergency):
/// Kam das Meeting über den Notfall-Knopf, nimmt der Host Skip-Stimmen sofort wieder zurück
/// (RpcClearVote – wie bei einem Disconnect). Der Spieler kann dann neu abstimmen; das klappt
/// auch bei Mitspielern ohne Mod. Einen Chat-Hinweis gibt es nicht (Wunsch des Nutzers) – die Regel
/// sagt der Host im Voice an. Gar nicht abzustimmen (Zeit ablaufen lassen) bleibt möglich.
/// </summary>
internal static class NoSkipRule
{
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
    public static void OnMeetingStarted(NetworkedPlayerInfo? body)
    {
        _emergencyMeeting = body == null;
        if (Active) CrewStatsPlugin.Logger.LogInfo("Kein-Skip-Regel aktiv (Notfall-Meeting).");
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
