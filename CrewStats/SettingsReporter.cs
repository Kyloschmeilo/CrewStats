using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Schickt beim Rundenstart alle Lobby-Einstellungen an den Bot (für /rundensettings).
/// Liest die Optionen generisch über die Options-Enums aus, damit neue Einstellungen aus
/// Spiel-Updates automatisch mitkommen. Funktioniert auch ohne Host zu sein.
/// JSON-Format ↔ Bot: services/GameSettings.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class SettingsReporter
{
    public const string FileName = "crewstats-settings.json";

    private static readonly RoleTypes[] SkippedRoles =
        { RoleTypes.Crewmate, RoleTypes.Impostor, RoleTypes.CrewmateGhost, RoleTypes.ImpostorGhost };

    /// <summary>Alle Lobby-Optionen (auch für die Rundendaten, siehe GameRecorder). Läuft auf dem Spiel-Thread.</summary>
    public static RoundSettings Capture(IGameOptions options)
    {
        var settings = new RoundSettings { MaxPlayers = options.MaxPlayers, NumImpostors = options.NumImpostors };
        foreach (Int32OptionNames name in Enum.GetValues(typeof(Int32OptionNames)))
        {
            int value = 0;
            if (name != Int32OptionNames.Invalid && Try(() => options.TryGetInt(name, out value))) settings.Ints[name.ToString()] = value;
        }

        foreach (FloatOptionNames name in Enum.GetValues(typeof(FloatOptionNames)))
        {
            float value = 0;
            if (name != FloatOptionNames.Invalid && Try(() => options.TryGetFloat(name, out value)))
                settings.Floats[name.ToString()] = (float)Math.Round(value, 3);
        }

        foreach (BoolOptionNames name in Enum.GetValues(typeof(BoolOptionNames)))
        {
            bool value = false;
            if (name != BoolOptionNames.Invalid && Try(() => options.TryGetBool(name, out value))) settings.Bools[name.ToString()] = value;
        }

        foreach (RoleTypes role in Enum.GetValues(typeof(RoleTypes)))
        {
            if (Array.IndexOf(SkippedRoles, role) >= 0) continue;
            try
            {
                settings.Roles[role.ToString()] = new RoleSetting
                {
                    Count = options.RoleOptions.GetNumPerGame(role),
                    Chance = options.RoleOptions.GetChancePerGame(role),
                };
            }
            catch
            {
                // Rolle kennt die Options-Version nicht – einfach weglassen
            }
        }
        return settings;
    }

    // Läuft auf dem Spiel-Thread: hier alles lesen, gesendet wird im Hintergrund
    public static void OnGameStart()
    {
        var client = AmongUsClient.Instance;
        if (client == null) return;
        if (client.NetworkMode == NetworkModes.FreePlay && !CrewStatsPlugin.RecordFreeplay.Value) return;

        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        if (options == null || options.GameMode is not (GameModes.Normal or GameModes.NormalFools)) return;

        var settings = Capture(options);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            crewstats = "settings",
            v = 1,
            capturedAt = DateTime.UtcNow,
            lobbyCode = client.NetworkMode == NetworkModes.FreePlay ? "FREEPLAY" : GameCode.IntToGameName(client.GameId) ?? "",
            map = GameRecorder.MapName(options.MapId),
            players = GameData.Instance != null ? GameData.Instance.PlayerCount : 0,
            maxPlayers = settings.MaxPlayers,
            numImpostors = settings.NumImpostors,
            ints = settings.Ints,
            floats = settings.Floats,
            bools = settings.Bools,
            roles = settings.Roles.ToDictionary(r => r.Key, r => new { count = r.Value.Count, chance = r.Value.Chance }),
        });

        var url = RoundUploader.WebhookUrl;
        if (url == null) return;
        Task.Run(async () =>
        {
            try
            {
                if (await RoundUploader.PostJsonFileAsync(url, json, FileName, "⚙️ Rundeneinstellungen"))
                    CrewStatsPlugin.Logger.LogInfo("Rundeneinstellungen an Discord gesendet");
            }
            catch (Exception exc)
            {
                CrewStatsPlugin.Logger.LogWarning($"Rundeneinstellungen: Senden fehlgeschlagen: {exc.Message}");
            }
        });
    }

    private static bool Try(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }
}
