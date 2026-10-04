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

        foreach (var toggle in Toggles)
        {
            if (parent.Find(toggle.ObjectName) != null) continue;
            var position = NextFreeSlot(positions);
            positions.Add(position);
            CreateOne(template, parent, position, toggle);
        }
    }

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
        static float Round(float value) => (float)Math.Round(value, 2);

        var columns = positions.Select(p => Round(p.x)).Distinct().OrderBy(x => x).ToList();
        var rows = positions.Select(p => Round(p.y)).Distinct().OrderByDescending(y => y).ToList();
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
