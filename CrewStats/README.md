# CrewStats – Host-only-Mod für Among Us

Zeichnet als **Host** still die Rundendaten auf (Rollen, Kills, Meetings, Votes, Tasks,
Sieger) und schickt sie **nach Rundenende** per Discord-Webhook an den Bot. Außerdem: Auto-Mute,
Lobby-Ankündigung, Hausregel „Kein Skip bei Notfall“ und Rollenwünsche aus Discord.
Nur der Host braucht die Mod. Während der Runde wird nichts angezeigt.

## Bauen
```
dotnet build -c Release
```
Die fertige `CrewStats.dll` wird automatisch nach `<Among Us>/BepInEx/plugins` kopiert
(Pfad in `CrewStats.csproj` → `<AmongUs>`).

## Einrichten
1. Im Discord `/mod-setup kanal:#ergebnisse` ausführen und die Webhook-URL kopieren.
2. Among Us einmal starten und schließen, damit die Config angelegt wird.
3. In `<Among Us>/BepInEx/config/de.amongusmanager.crewstats.cfg` bei `WebhookUrl =` die URL eintragen.
4. Jeder Spieler verknüpft einmal seinen Friendcode: `/verknuepfen friendcode:name#1234`

## Auto-Mute (Discord)
Optional schaltet der Bot die Leute im Voice passend zum Spiel (wie AutoMuteUs):
- **Aufgaben** → Lebende stumm **und taub**, Tote reden miteinander
- **Meeting** (inkl. Rauswurf-Animation) → Lebende reden, Tote stumm (hören aber zu)
- **Spielende / Lobby / Spiel verlassen / Schalter aus** → alle frei

Als tot zählt nur, wer mit `/verknuepfen` verknüpft ist. Die Toten werden nur beim Phasenwechsel
gemeldet – ein Kill mitten in der Runde ändert nichts im Voice, sonst würden die Discord-Symbole
den Tod verraten. Der Bot braucht die Rechte **Mitglieder stummschalten** und **Deafen Members**.

An-/Ausschalten im Spiel: **ESC (bzw. Zahnrad) → Reiter „Allgemein“ → „Auto-Mute: An/Aus“**.
Der Zustand wird in der Config (`[AutoMute] Enabled`) gespeichert, Standard ist **aus**.
Gemutet wird im Voice-Channel des Spielers mit der Mod (muss mit `/verknuepfen` verknüpft sein),
sonst im Among-Us-Channel. Funktioniert auch, wenn man nicht Host ist.

## Rundeneinstellungen (`/rundensettings`)
Bei jedem Rundenstart schickt die Mod alle Lobbyeinstellungen (Kill-Cooldown, Meetings, Tasks,
Rollen …) an den Bot. `/rundensettings` zeigt immer den Stand der letzten Runde, mit Buttons für
die Kategorien. Funktioniert auch, wenn man nicht Host ist.

## Rollenwünsche (`/rollenwunsch`, `/rolle-zuweisen`)
Im Discord wünscht man sich für 20 XP (2× pro Spieleabend) eine Rolle für die nächste Runde, Admins
können Rollen kostenlos festlegen. Nur der Host braucht die Mod:
1. Host drückt **Start** → die Mod schickt die Friendcodes der Lobby über den Webhook an den Bot und
   bekommt die offenen Wünsche dieser Spieler zurück (verschlüsselt, im Channel nicht lesbar).
2. Bei der Rollenvergabe tauscht die Mod die gewürfelten Rollen so, dass die Wünsche passen – Anzahl
   Impostor und Spezialrollen bleiben wie eingestellt. Geht ein Wunsch nicht, bekommt der Spieler die XP zurück.

Abschalten: `[Rollenwuensche] Enabled = false`. Ohne Wünsche greift die Mod nicht in die Rollenvergabe ein.
Im Spiel erscheint kein Hinweis (Chat-Nachrichten haben genervt). Falls doch mal wieder Chat: nie `RpcSendChat`,
wenn der Host „Nur Quick Chat“ hat – der Server kickt ihn sonst als „Hacking“.

## Nach einem Among-Us-Update
In `CrewStats.csproj` die Version von `AmongUs.GameLibs.Steam` auf die neue Spielversion
setzen (steht unten im Hauptmenü) und neu bauen. Verfügbare Versionen:
https://nuget.bepinex.dev/packages/AmongUs.GameLibs.Steam

Seit **2026.9.29** ist Among Us **64-Bit** – BepInEx muss die Variante
`BepInEx-Unity.IL2CPP-win-x64` sein (https://builds.bepinex.dev/projects/bepinex_be).

## Fehlersuche
- Log: `<Among Us>/BepInEx/LogOutput.log` (nach „CrewStats“ suchen)
- Aufgezeichnete Runden: `<Among Us>/BepInEx/CrewStats/pending` (noch nicht gesendet)
  und `.../sent` (erfolgreich gesendet). Nicht gesendete Runden werden beim nächsten Mal nachgeholt.
