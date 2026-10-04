using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CrewStats;

/// <summary>
/// Eigene Schalter im Einstellungsmenü (ESC bzw. Zahnrad), Reiter „Allgemein“: Auto-Mute und
/// „Kein Skip bei Notfall“. Jeder ist ein Klon eines vorhandenen Spiel-Schalters, damit er genauso
/// aussieht, und kommt im Raster der anderen Schalter auf den nächsten freien Platz.
/// </summary>
internal static class MenuToggles
{
    private sealed record Toggle(string ObjectName, string Label, Func<bool> Get, Action<bool> Set);

    private static readonly Toggle[] Toggles =
    {
        new("CrewStatsAutoMute", "Auto-Mute", () => AutoMute.Enabled, on => AutoMute.Enabled = on),
        new("CrewStatsNoSkip", "Kein Skip bei Notfall", () => NoSkipRule.Enabled, on => NoSkipRule.Enabled = on),
    };

    private static readonly Color OnColor = new(0f, 1f, 0.16f, 1f);

    public static void Create(OptionsMenuBehaviour menu)
    {
        var originals = new[]
            {
                menu.CensorChatButton, menu.EnableFriendInvitesButton, menu.ColorBlindButton,
                menu.StreamerModeButton, menu.DisableMouseMovement,
            }
            .Where(t => t != null)
            .ToList();
        var template = originals.FirstOrDefault(t => t == menu.StreamerModeButton) ?? originals.FirstOrDefault();
        if (template == null) return;

        var parent = template.transform.parent;
        // Neue Schalter starten im Container des Vorlagen-Schalters, FitRows ordnet sie danach ein
        var positions = originals
            .Where(t => t.transform.parent == parent && t.gameObject.activeSelf)
            .Select(t => t.transform.localPosition)
            .ToList();

        var created = new List<Transform>();
        foreach (var toggle in Toggles)
        {
            if (parent.Find(toggle.ObjectName) != null) continue;
            var position = NextFreeSlot(positions);
            positions.Add(position);
            if (CreateOne(template, parent, position, toggle) is { } button) created.Add(button);
        }

        // Das Raster unten im Menü: jede Reihe ist im Spiel ein eigener Container – deshalb
        // in Weltkoordinaten über alle Schalter hinweg rechnen. „Mouse Movement“ gehört nicht dazu.
        var grid = new[] { menu.CensorChatButton, menu.EnableFriendInvitesButton, menu.ColorBlindButton, menu.StreamerModeButton }
            .Where(t => t != null && t.gameObject.activeSelf)
            .Select(t => t.transform)
            .ToList();
        if (created.Count > 0) FitRows(grid, created);
    }

    // Maße des Spiel-Rasters (aus Screenshots gemessen, relativ zum Reihenabstand):
    private const float ButtonHeight = 0.72f; // Höhe eines Schalters
    private const float AvailableHeight = 1.84f; // Platz von der Oberkante Reihe 1 bis kurz über „Leave Game“
    private const float GapFactor = 0.92f; // Schalterhöhe im Verhältnis zum neuen Reihenabstand

    /// <summary>
    /// Unter dem Raster liegt im Spiel „Leave Game“ – für eine weitere Reihe ist dort kein Platz.
    /// Deshalb wird das ganze Raster gleichmäßig enger und kleiner gemacht, sodass alle Reihen in den
    /// Platz der ursprünglichen passen. Gerechnet wird in Weltkoordinaten (Reihen = eigene Container).
    /// </summary>
    private static void FitRows(List<Transform> vanilla, List<Transform> created)
    {
        var vanillaRows = RowsOf(vanilla.Select(t => t.position).ToList());
        if (vanillaRows.Count < 2)
        {
            CrewStatsPlugin.Logger.LogWarning($"Menü-Raster: nur {vanillaRows.Count} Spiel-Reihe(n) gefunden – Schalter bleiben unverändert.");
            return;
        }
        var step = vanillaRows[0] - vanillaRows[1]; // ursprünglicher Reihenabstand

        // Neue Schalter als eigene Reihe direkt unter dem Spiel-Raster einordnen
        var newRowY = vanillaRows[^1] - step;
        foreach (var button in created)
            button.position = new Vector3(button.position.x, newRowY, button.position.z);

        var buttons = vanilla.Concat(created).ToList();
        var rows = RowsOf(buttons.Select(b => b.position).ToList());
        var newStep = AvailableHeight * step / (rows.Count - 1 + GapFactor);
        var newHeight = GapFactor * newStep;
        var scale = newHeight / (ButtonHeight * step);
        var top = vanillaRows[0] + ButtonHeight * step / 2; // Oberkante der ersten Reihe bleibt

        foreach (var button in buttons)
        {
            var row = rows.IndexOf(Round(button.position.y));
            var position = button.position;
            button.position = new Vector3(position.x, top - newHeight / 2 - row * newStep, position.z);
            button.localScale *= scale;
        }
        CrewStatsPlugin.Logger.LogInfo(
            $"Menü-Raster: Reihen [{string.Join("; ", vanillaRows)}] + 1 → {rows.Count} Reihen, Größe {scale:P0}.");
    }

    private static float Round(float value) => (float)Math.Round(value, 2);

    /// <summary>Verschiedene Reihen (y-Werte), oben zuerst.</summary>
    private static List<float> RowsOf(List<Vector3> positions) =>
        positions.Select(p => Round(p.y)).Distinct().OrderByDescending(y => y).ToList();

    private static Transform? CreateOne(ToggleButtonBehaviour template, Transform parent, Vector3 position, Toggle toggle)
    {
        var clone = Object.Instantiate(template, parent);
        clone.name = toggle.ObjectName;
        clone.transform.localPosition = position;

        var text = clone.Text;
        var background = clone.Background;
        var rollover = clone.Rollover;
        var gameObject = clone.gameObject;
        var button = gameObject.GetComponent<PassiveButton>() ?? gameObject.GetComponentInChildren<PassiveButton>(true);

        // Spiel-Logik des Originals entfernen: eigener Text, eigener Klick
        foreach (var translator in gameObject.GetComponentsInChildren<TextTranslatorTMP>(true))
            Object.DestroyImmediate(translator);
        Object.DestroyImmediate(clone);

        if (button == null)
        {
            CrewStatsPlugin.Logger.LogWarning($"Schalter {toggle.Label}: kein Button gefunden.");
            return null;
        }
        button.OnClick = new Button.ButtonClickedEvent();
        button.OnClick.AddListener((Action)(() =>
        {
            toggle.Set(!toggle.Get()); // BepInEx speichert die Config sofort
            Refresh(toggle, text, background, rollover);
        }));
        Refresh(toggle, text, background, rollover);

        CrewStatsPlugin.Logger.LogInfo($"Schalter \"{toggle.Label}\" im Menü bei {position}");
        return gameObject.transform;
    }

    private static void Refresh(Toggle toggle, TextMeshPro text, SpriteRenderer background, ButtonRolloverHandler? rollover)
    {
        var on = toggle.Get();
        var color = on ? OnColor : Color.white;
        text.text = $"{toggle.Label}: {(on ? "An" : "Aus")}";
        background.color = color;
        if (rollover != null) rollover.ChangeOutColor(color);
    }

    /// <summary>
    /// Die Schalter liegen in einem Raster. Ist die unterste Zeile noch nicht voll, kommt der
    /// neue Schalter dort in die freie Spalte, sonst in eine neue Zeile darunter.
    /// </summary>
    private static Vector3 NextFreeSlot(List<Vector3> positions)
    {
        var columns = positions.Select(p => Round(p.x)).Distinct().OrderBy(x => x).ToList();
        var rows = RowsOf(positions);
        var z = positions.Count > 0 ? positions[0].z : 0f;
        if (columns.Count == 0) return Vector3.zero;

        var bottom = rows.Last();
        var used = positions.Where(p => Round(p.y) == bottom).Select(p => Round(p.x)).ToHashSet();
        var free = columns.FirstOrDefault(x => !used.Contains(x), float.NaN);
        if (!float.IsNaN(free)) return new Vector3(free, bottom, z);

        var rowStep = rows.Count > 1 ? rows[^2] - rows[^1] : 0.5f;
        return new Vector3(columns[0], bottom - rowStep, z);
    }
}
