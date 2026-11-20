using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrewStats;

/// <summary>
/// Musik im Meeting anhalten (Config <c>[Musik] ImMeetingPausieren</c>, Standard aus, Schalter im F7-Menü):
/// Beginnt ein Meeting, pausiert die Mod alles, was auf dem PC gerade spielt und in der Windows-Medienanzeige
/// steht (Spotify, YouTube im Browser, Apple Music …); ist das Meeting samt Rauswurf vorbei, laufen genau diese
/// Wiedergaben weiter. Was schon vorher pausiert war, bleibt pausiert. Beim Spielende/Verlassen geht die Musik
/// ebenfalls weiter. Wirkt nur auf dem eigenen PC, braucht keinen Host und keinen Bot.
///
/// Umsetzung: Windows-Medien-API (GlobalSystemMediaTransportControlsSessionManager) über ein verstecktes
/// Windows-PowerShell, das die ganze Spielsitzung offen bleibt und Befehle über stdin bekommt – so braucht die
/// DLL keine WinRT-Bibliotheken. Endet, wenn Among Us endet (stdin geht zu).
/// </summary>
internal static class MeetingMusic
{
    private static readonly object Lock = new();
    private static readonly SemaphoreSlim Queue = new(1, 1); // Befehle nacheinander (Pause vor Weiter)
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);

    private static Process? _helper;
    private static bool _broken; // PowerShell/Medien-API nicht verfügbar → für diese Sitzung aus
    private static bool _paused; // wir haben im aktuellen Meeting pausiert
    private static bool _wasMeeting;
    private static bool _errorLogged;
    private static bool _warmedUp;

    private static bool Enabled => CrewStatsPlugin.PauseMusicInMeeting.Value && OperatingSystem.IsWindows();

    /// <summary>Jeden Frame aus HudManager.Update.</summary>
    public static void Tick()
    {
        try
        {
            if (!_warmedUp && Enabled && !_broken && ShipStatus.Instance != null)
            {
                _warmedUp = true; // in der Runde schon starten, damit das erste Meeting ohne Verzögerung pausiert
                Warmup();
            }
            var meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            if (meeting == _wasMeeting)
            {
                // Schalter mitten im Meeting ausgeschaltet → Musik gleich weiter
                if (_paused && !CrewStatsPlugin.PauseMusicInMeeting.Value) Resume();
                return;
            }
            _wasMeeting = meeting;
            if (meeting)
            {
                if (Enabled && !_broken) Pause();
            }
            else
            {
                Resume();
            }
        }
        catch (Exception exc)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            CrewStatsPlugin.Logger.LogError($"Musik-Pause-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    /// <summary>Spielende / Spiel verlassen: angehaltene Musik weiterlaufen lassen.</summary>
    public static void Stop()
    {
        _wasMeeting = false;
        Resume();
    }

    private static void Warmup()
    {
        Task.Run(async () =>
        {
            await Queue.WaitAsync();
            try
            {
                EnsureHelper();
            }
            catch (Exception exc)
            {
                Broken($"konnte nicht starten: {exc.Message}");
            }
            finally
            {
                Queue.Release();
            }
        });
    }

    private static void Pause()
    {
        _paused = true;
        Task.Run(() => SendAsync("pause"));
    }

    private static void Resume()
    {
        if (!_paused) return;
        _paused = false;
        Task.Run(() => SendAsync("play"));
    }

    private static async Task SendAsync(string command)
    {
        await Queue.WaitAsync();
        try
        {
            if (_broken) return;
            var helper = EnsureHelper();
            if (helper == null) return;
            await helper.StandardInput.WriteLineAsync(command);
            await helper.StandardInput.FlushAsync();

            var read = helper.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(ReplyTimeout)) != read)
            {
                CrewStatsPlugin.Logger.LogWarning($"Musik: keine Antwort auf \"{command}\" – Hilfsprozess wird neu gestartet.");
                Kill();
                return;
            }
            var reply = read.Result;
            if (reply == null)
            {
                Kill();
                return;
            }
            if (reply.StartsWith("fatal", StringComparison.Ordinal)) Broken(reply);
            else CrewStatsPlugin.Logger.LogInfo($"Musik: {reply}");
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Musik: \"{command}\" fehlgeschlagen: {exc.Message}");
            Kill();
        }
        finally
        {
            Queue.Release();
        }
    }

    // Nur innerhalb von Queue aufrufen
    private static Process? EnsureHelper()
    {
        lock (Lock)
        {
            if (_helper != null && !_helper.HasExited) return _helper;
            _helper?.Dispose();
            _helper = null;

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                            Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var process = Process.Start(start);
            if (process == null) return null;
            process.ErrorDataReceived += (_, _) => { }; // stderr leeren, damit nichts blockiert
            process.BeginErrorReadLine();
            _helper = process;
            CrewStatsPlugin.Logger.LogInfo("Musik: Hilfsprozess gestartet (pausiert Musik im Meeting).");
            return process;
        }
    }

    private static void Broken(string why)
    {
        _broken = true;
        _paused = false;
        CrewStatsPlugin.Logger.LogWarning($"Musik im Meeting anhalten geht auf diesem PC nicht ({why}) – bis zum Neustart aus.");
        Kill();
    }

    private static void Kill()
    {
        lock (Lock)
        {
            try
            {
                if (_helper != null && !_helper.HasExited) _helper.Kill();
            }
            catch
            {
                // schon beendet
            }
            _helper?.Dispose();
            _helper = null;
        }
    }

    /// <summary>
    /// Windows PowerShell 5.1 (bei Windows 10/11 immer dabei). Liest „pause“/„play“ zeilenweise, antwortet mit einer
    /// Zeile. Gemerkt werden die Sitzungs-Objekte selbst, damit z.B. bei mehreren Browser-Tabs nur der Tab
    /// weiterläuft, der vorher lief.
    /// </summary>
    internal const string Script = """
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function Await($op, [Type]$type) {
        $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
        if (-not $task.Wait(5000)) { throw 'Zeitüberschreitung' }
        $task.Result
    }
    $managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
    $manager = Await ($managerType::RequestAsync()) $managerType
} catch {
    [Console]::Out.WriteLine('fatal ' + $_.Exception.Message)
    [Console]::Out.Flush()
    exit 1
}
$paused = New-Object System.Collections.ArrayList
while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    try {
        if ($line -eq 'pause') {
            $paused.Clear()
            $names = @()
            foreach ($session in $manager.GetSessions()) {
                if ($session.GetPlaybackInfo().PlaybackStatus -eq 'Playing') {
                    if (Await ($session.TryPauseAsync()) ([bool])) {
                        [void]$paused.Add($session)
                        $names += $session.SourceAppUserModelId
                    }
                }
            }
            [Console]::Out.WriteLine('pausiert: ' + $paused.Count + $(if ($names.Count) { ' (' + ($names -join ', ') + ')' } else { '' }))
        } elseif ($line -eq 'play') {
            $count = 0
            foreach ($session in $paused) {
                try {
                    if ($session.GetPlaybackInfo().PlaybackStatus -eq 'Paused') {
                        if (Await ($session.TryPlayAsync()) ([bool])) { $count++ }
                    }
                } catch { }
            }
            $paused.Clear()
            [Console]::Out.WriteLine('weiter: ' + $count)
        } else {
            [Console]::Out.WriteLine('unbekannt: ' + $line)
        }
    } catch {
        [Console]::Out.WriteLine('fehler: ' + $_.Exception.Message)
    }
    [Console]::Out.Flush()
}
""";
}
