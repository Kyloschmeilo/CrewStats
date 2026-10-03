using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CrewStats;

/// <summary>
/// Schalter „Auto-Mute“ im Einstellungsmenü (ESC bzw. Zahnrad), Reiter „Allgemein“.
/// Ein Klon eines vorhandenen Spiel-Schalters, damit er genauso aussieht, und wird
/// im Raster der anderen Schalter auf den nächsten freien Platz gesetzt.
/// </summary>
internal static class AutoMuteToggle
{
    private const string ObjectName = "CrewStatsAutoMute";
    private static readonly Color OnColor = new(0f, 1f, 0.16f, 1f);

    public static void Create(OptionsMenuBehaviour menu)
    {
        var toggles = new[]
            {
                menu.CensorChatButton, menu.EnableFriendInvitesButton, menu.ColorBlindButton,
                menu.StreamerModeButton, menu.DisableMouseMovement,
            }
            .Where(t => t != null)
            .ToList();
        var template = toggles.FirstOrDefault(t => t == menu.StreamerModeButton) ?? toggles.FirstOrDefault();
        if (template == null) return;

        var parent = template.transform.parent;
        if (parent.Find(ObjectName) != null) return;

        var siblings = toggles.Where(t => t.transform.parent == parent && t.gameObject.activeSelf).ToList();
        var clone = Object.Instantiate(template, parent);
        clone.name = ObjectName;
        clone.transform.localPosition = NextFreeSlot(siblings.Select(t => t.transform.localPosition).ToList());

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
            CrewStatsPlugin.Logger.LogWarning("Auto-Mute-Schalter: kein Button gefunden.");
            return;
        }
        button.OnClick = new Button.ButtonClickedEvent();
        button.OnClick.AddListener((Action)(() =>
        {
            AutoMute.Enabled = !AutoMute.Enabled;
            Refresh(text, background, rollover);
        }));
        Refresh(text, background, rollover);

        CrewStatsPlugin.Logger.LogInfo($"Auto-Mute-Schalter im Menü bei {button.transform.localPosition}");
    }

    private static void Refresh(TextMeshPro text, SpriteRenderer background, ButtonRolloverHandler? rollover)
    {
        var on = AutoMute.Enabled;
        var color = on ? OnColor : Color.white;
        text.text = $"Auto-Mute: {(on ? "An" : "Aus")}";
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
