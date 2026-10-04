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
        var positions = originals
            .Where(t => t.transform.parent == parent && t.gameObject.activeSelf)
            .Select(t => t.transform.localPosition)
            .ToList();

        var vanillaRows = RowsOf(positions);
        var added = false;
        foreach (var toggle in Toggles)
        {
            if (parent.Find(toggle.ObjectName) != null) continue;
            var position = NextFreeSlot(positions);
            positions.Add(position);
            CreateOne(template, parent, position, toggle);
            added = true;
        }
        if (added) FitRows(parent, originals, vanillaRows);
    }

    // Maße des Spiel-Rasters (aus einem Screenshot gemessen, relativ zum Reihenabstand):
    private const float ButtonHeight = 0.77f; // Höhe eines Schalters
    private const float AvailableHeight = 1.84f; // Platz von der Oberkante Reihe 1 bis kurz über „Leave Game“
    private const float GapFactor = 0.92f; // Schalterhöhe im Verhältnis zum neuen Reihenabstand

    /// <summary>
    /// Kommt durch unsere Schalter eine weitere Reihe dazu, ist darunter kein Platz (dort liegt im
    /// Spiel „Leave Game“). Dann wird das ganze Raster gleichmäßig enger und kleiner gemacht, sodass
    /// alle Reihen in den Platz der ursprünglichen passen.
    /// </summary>
    private static void FitRows(Transform parent, List<ToggleButtonBehaviour> originals, List<float> vanillaRows)
    {
        var buttons = originals
            .Where(t => t.transform.parent == parent && t.gameObject.activeSelf)
            .Select(t => t.transform)
            .Concat(Toggles.Select(t => parent.Find(t.ObjectName)).Where(t => t != null))
            .ToList();
        var rows = RowsOf(buttons.Select(b => b.localPosition).ToList());
        if (vanillaRows.Count < 2 || rows.Count <= vanillaRows.Count) return;

        var step = vanillaRows[0] - vanillaRows[1]; // ursprünglicher Reihenabstand
        var newStep = AvailableHeight * step / (rows.Count - 1 + GapFactor);
        var newHeight = GapFactor * newStep;
        var scale = newHeight / (ButtonHeight * step);
        var top = vanillaRows[0] + ButtonHeight * step / 2; // Oberkante der ersten Reihe bleibt

        foreach (var button in buttons)
        {
            var row = rows.IndexOf(Round(button.localPosition.y));
            var position = button.localPosition;
            button.localPosition = new Vector3(position.x, top - newHeight / 2 - row * newStep, position.z);
            button.localScale *= scale;
        }
        CrewStatsPlugin.Logger.LogInfo($"Menü-Schalter auf {rows.Count} Reihen verteilt (Größe {scale:P0}).");
    }

    private static float Round(float value) => (float)Math.Round(value, 2);

    /// <summary>Verschiedene Reihen (y-Werte), oben zuerst.</summary>
    private static List<float> RowsOf(List<Vector3> positions) =>
        positions.Select(p => Round(p.y)).Distinct().OrderByDescending(y => y).ToList();

    private static void CreateOne(ToggleButtonBehaviour template, Transform parent, Vector3 position, Toggle toggle)
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
            return;
        }
        button.OnClick = new Button.ButtonClickedEvent();
        button.OnClick.AddListener((Action)(() =>
        {
            toggle.Set(!toggle.Get()); // BepInEx speichert die Config sofort
            Refresh(toggle, text, background, rollover);
        }));
        Refresh(toggle, text, background, rollover);

        CrewStatsPlugin.Logger.LogInfo($"Schalter \"{toggle.Label}\" im Menü bei {position}");
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
