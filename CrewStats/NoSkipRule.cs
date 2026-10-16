using InnerNet;

namespace CrewStats;

/// <summary>
/// Hausregel „Kein Skip bei Notfall“ (Schalter im ESC-Menü, Config [Regeln] NoSkipOnEmergency):
/// Kam das Meeting über den Notfall-Knopf, nimmt der Host Skip-Stimmen sofort wieder zurück
/// (RpcClearVote – wie bei einem Disconnect). Der Spieler kann dann neu abstimmen; das klappt
/// auch bei Mitspielern ohne Mod. Beim Meeting-Start schreibt der Host einen Hinweis in den Chat –
/// nur mit Free Chat (<see cref="GameChat"/>), sonst sieht nur der Host den Hinweis.
/// Gar nicht abzustimmen (Zeit ablaufen lassen) bleibt möglich.
/// </summary>
internal static class NoSkipRule
{
    private const string ChatHint = "Notfall-Meeting: Skippen ist nicht erlaubt - bitte jemanden wählen!";

    private static bool _emergencyMeeting;
    private static bool _hintPending;

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
        _hintPending = false;
    }

    /// <summary>MeetingHud.Start – Hinweis in den Chat (nur der Host).</summary>
    public static void OnMeetingHudShown()
    {
        if (!Active) return;
        CrewStatsPlugin.Logger.LogInfo("Kein-Skip-Regel aktiv (Notfall-Meeting).");
        if (GameChat.FreeChatAllowed)
        {
            _hintPending = true; // geht im nächsten Tick raus, sobald die Sende-Pause vorbei ist
            return;
        }
        GameChat.ShowLocal("Kein Skip bei Notfall ist aktiv. Ohne Free Chat kann die Mod den Hinweis nicht für alle in den Chat schreiben – bitte im Voice ansagen.");
    }

    /// <summary>Jeden Frame (HudManager.Update): den Hinweis senden, solange das Meeting läuft.</summary>
    public static void Tick()
    {
        if (!_hintPending) return;
        try
        {
            var meeting = MeetingHud.Instance;
            if (!Active || meeting == null || meeting.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding)
            {
                _hintPending = false;
                return;
            }
            if (GameChat.TrySend(ChatHint)) _hintPending = false;
        }
        catch (System.Exception exc)
        {
            _hintPending = false;
            CrewStatsPlugin.Logger.LogError($"Kein-Skip-Regel: Chat-Hinweis fehlgeschlagen: {exc}");
        }
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
