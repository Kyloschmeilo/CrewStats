using System;
using System.Collections.Generic;
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

    // Läuft auf dem Spiel-Thread: hier alles lesen, gesendet wird im Hintergrund
    public static void OnGameStart()
    {
        var client = AmongUsClient.Instance;
        if (client == null) return;
        if (client.NetworkMode == NetworkModes.FreePlay && !CrewStatsPlugin.RecordFreeplay.Value) return;

        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        if (options == null || options.GameMode is not (GameModes.Normal or GameModes.NormalFools)) return;

        var ints = new Dictionary<string, int>();
        foreach (Int32OptionNames name in Enum.GetValues(typeof(Int32OptionNames)))
        {
            int value = 0;
            if (name != Int32OptionNames.Invalid && Try(() => options.TryGetInt(name, out value))) ints[name.ToString()] = value;
        }

        var floats = new Dictionary<string, float>();
        foreach (FloatOptionNames name in Enum.GetValues(typeof(FloatOptionNames)))
        {
            float value = 0;
            if (name != FloatOptionNames.Invalid && Try(() => options.TryGetFloat(name, out value)))
                floats[name.ToString()] = (float)Math.Round(value, 3);
        }

        var bools = new Dictionary<string, bool>();
        foreach (BoolOptionNames name in Enum.GetValues(typeof(BoolOptionNames)))
        {
            bool value = false;
            if (name != BoolOptionNames.Invalid && Try(() => options.TryGetBool(name, out value))) bools[name.ToString()] = value;
        }

        var roles = new Dictionary<string, object>();
        foreach (RoleTypes role in Enum.GetValues(typeof(RoleTypes)))
        {
            if (Array.IndexOf(SkippedRoles, role) >= 0) continue;
            try
            {
                roles[role.ToString()] = new
                {
                    count = options.RoleOptions.GetNumPerGame(role),
                    chance = options.RoleOptions.GetChancePerGame(role),
                };
            }
            catch
            {
                // Rolle kennt die Options-Version nicht – einfach weglassen
            }
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            crewstats = "settings",
            v = 1,
            capturedAt = DateTime.UtcNow,
            lobbyCode = client.NetworkMode == NetworkModes.FreePlay ? "FREEPLAY" : GameCode.IntToGameName(client.GameId) ?? "",
            map = GameRecorder.MapName(options.MapId),
            players = GameData.Instance != null ? GameData.Instance.PlayerCount : 0,
            maxPlayers = options.MaxPlayers,
            numImpostors = options.NumImpostors,
            ints,
            floats,
            bools,
            roles,
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
