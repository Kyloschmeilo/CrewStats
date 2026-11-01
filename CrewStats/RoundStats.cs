using System;
using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>
/// Reine Rechenfunktionen für die ausführlichen Rundendaten (ohne Spiel-Typen, getestet in Tests/):
/// Ereignis-Typen, Zeitleiste sortieren, Mengen vergleichen (Sabotagen/Türen/Aufgaben), Laufwege und die
/// Kennzahlen je Spieler (<see cref="PlayerSummary"/>).
/// </summary>
internal static class RoundStats
{
    // Ereignis-Typen der Zeitleiste (docs/rundenformat.md)
    public const string Kill = "kill";
    public const string Meeting = "meeting";
    public const string Vote = "vote";
    public const string Exile = "exile";
    public const string Task = "task";
    public const string Sabotage = "sabotage";
    public const string SabotageEnd = "sabotageEnd";
    public const string Repair = "repair";
    public const string Doors = "doors";
    public const string VentEnter = "ventEnter";
    public const string VentExit = "ventExit";
    public const string Disconnect = "disconnect";
    public const string RoleChange = "roleChange";
    public const string Shapeshift = "shapeshift";
    public const string Protect = "protect";
    public const string Revive = "revive";
    public const string Death = "death"; // Tod laut The Other Roles (Guess, Jailed, Suicide …), ohne normale Kills/Rauswürfe

    /// <summary>Laufweg: ein Punkt pro Sekunde.</summary>
    public const double TrackInterval = 1.0;

    /// <summary>Schneller als das (Einheiten/s) ist kein Laufen mehr, sondern Vent, Leiter-Ende, Teleport o.ä.</summary>
    public const double MaxWalkSpeed = 8.0;

    /// <summary>Ab dieser JSON-Größe werden die Laufwege weggelassen (Discord-Webhooks nehmen höchstens 10 MB).</summary>
    public const int MaxBytesWithTracks = 7 * 1024 * 1024;

    /// <summary>Was in <paramref name="now"/> neu ist und was aus <paramref name="before"/> fehlt.</summary>
    public static (List<T> Added, List<T> Removed) Diff<T>(IReadOnlyCollection<T> before, IReadOnlyCollection<T> now)
    {
        var added = now.Where(x => !before.Contains(x)).ToList();
        var removed = before.Where(x => !now.Contains(x)).ToList();
        return (added, removed);
    }

    /// <summary>Koordinate auf 2 Nachkommastellen (reicht für Heatmaps, hält das JSON klein).</summary>
    public static float Coord(float value) => (float)Math.Round(value, 2);

    public static double Seconds(double value) => Math.Round(value, 2);

    /// <summary>Gelaufene Strecke eines Laufwegs [t, x, y, …] ohne Sprünge (Vents, Teleports, Pausen durch Meetings).</summary>
    public static double Distance(IReadOnlyList<float> points)
    {
        double total = 0;
        for (var i = 3; i + 2 < points.Count; i += 3)
        {
            var dt = points[i] - points[i - 3];
            var dx = points[i + 1] - points[i - 2];
            var dy = points[i + 2] - points[i - 1];
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (dt <= 0 || dt > 3 * TrackInterval || d / dt > MaxWalkSpeed) continue;
            total += d;
        }
        return Math.Round(total, 1);
    }

    /// <summary>Sortiert die Zeitleiste stabil nach Zeit (gleiche Zeit: Reihenfolge der Aufzeichnung).</summary>
    public static void SortEvents(RoundRecord round) =>
        round.Events = round.Events.Select((e, i) => (e, i)).OrderBy(x => x.e.AtSeconds).ThenBy(x => x.i).Select(x => x.e).ToList();

    /// <summary>Berechnet für jeden Spieler die Kennzahlen aus Meetings, Ereignissen, Aufgaben und Laufweg.</summary>
    public static void Summarize(RoundRecord round)
    {
        var meetingStarts = round.Meetings.Select(m => m.AtSeconds).OrderBy(t => t).ToList();
        foreach (var player in round.Players)
        {
            var id = player.PlayerId;
            var summary = new PlayerSummary();
            var track = round.Tracks.FirstOrDefault(t => t.Player == id);
            if (track != null) summary.Distance = Distance(track.Points);

            var end = player.DiedAtSeconds ?? player.DisconnectedAtSeconds;
            summary.AliveSeconds = Seconds(Math.Min(end ?? round.DurationSeconds, round.DurationSeconds));

            foreach (var meeting in round.Meetings)
            {
                if (meeting.Caller == id)
                {
                    if (meeting.Body == null) summary.MeetingsCalled++;
                    else summary.BodiesReported++;
                }
                foreach (var vote in meeting.Votes.Where(v => v.Voter == id))
                {
                    if (vote.Target is { } target)
                    {
                        summary.VotesCast++;
                        if (IsCorrectVote(player, round.FindPlayer(target))) summary.CorrectVotes++;
                    }
                    else if (vote.Skipped == true)
                    {
                        summary.Skips++;
                    }
                }
            }

            double? enteredAt = null;
            foreach (var e in round.Events.Where(e => e.Player == id))
            {
                switch (e.Type)
                {
                    case VentEnter:
                        summary.VentUses++;
                        enteredAt ??= e.AtSeconds;
                        break;
                    case VentExit when enteredAt != null:
                        summary.VentSeconds += e.AtSeconds - enteredAt.Value;
                        enteredAt = null;
                        break;
                    case Sabotage:
                        summary.Sabotages++;
                        break;
                    case Repair:
                        summary.SabotagesFixed++;
                        break;
                }
            }
            if (enteredAt != null)
            {
                // im Vent geblieben: bis zum nächsten Meeting bzw. Rundenende
                var until = meetingStarts.FirstOrDefault(t => t > enteredAt.Value);
                summary.VentSeconds += (until > 0 ? until : round.DurationSeconds) - enteredAt.Value;
            }
            summary.VentSeconds = Seconds(Math.Max(0, summary.VentSeconds));

            var done = player.Tasks?.Where(t => t.CompletedAtSeconds != null).Select(t => t.CompletedAtSeconds!.Value).OrderBy(t => t).ToList();
            if (done is { Count: > 0 })
            {
                summary.FirstTaskSeconds = done[0];
                if (player.Tasks!.All(t => t.Completed)) summary.AllTasksSeconds = done[^1];
            }
            player.Summary = summary;
        }
    }

    /// <summary>Richtige Stimme: ein Crew-Spieler wählt einen Impostor (Neutrale zählen weder richtig noch falsch).</summary>
    public static bool IsCorrectVote(PlayerRecord voter, PlayerRecord? target) =>
        target != null && voter.Team == RoundRules.Crew && target.Team == RoundRules.Impostor;

    /// <summary>Entfernt Farb-/Formatierungs-Tags wie &lt;color=#ff0000&gt; aus Texten der Rollen-Mod.</summary>
    public static string StripTags(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '<') depth++;
            else if (c == '>' && depth > 0) depth--;
            else if (depth == 0) result.Append(c);
        }
        return result.ToString().Trim();
    }
}
