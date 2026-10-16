# CrewStats – Among-Us-Mod für den Among Us Manager

Eine kleine Mod (BepInEx) für **Among Us auf Steam**, die mit unserem Discord-Bot zusammenarbeitet:

- 📊 **Rundenstatistik** – nach jeder Runde bekommt der Bot automatisch Sieger, Kills, Meetings und
  Votes (XP, `/stats`, Wetten werden ausgewertet). Aufgezeichnet wird nur, wenn du **Host** bist.
- 🔇 **Discord-Auto-Mute** – während der Runde sind die Lebenden stumm + taub und die Toten reden,
  im Meeting reden die Lebenden. An/aus im Spiel unter **ESC → Allgemein → Auto-Mute**.
- ⚙️ **`/rundensettings`** – der Bot zeigt die Lobby-Einstellungen der letzten Runde.
- 🎮 **Lobby-Ankündigung** – als Host erscheint deine Lobby automatisch im Discord (Code, Map,
  Spielerzahl live), während der Runde ist das Wettbüro gesperrt. Abschalten: `Announce = false`
  im Abschnitt `[Lobby]` der Config.
- 🚫 **Kein Skip bei Notfall** (Hausregel, nur als Host) – bei Meetings über den Notfall-Knopf
  wird „Überspringen“ abgelehnt, man muss jemanden wählen. An/aus unter **ESC → Allgemein**.
  Den Hinweis im Chat schreibt die Mod nur, wenn der Host im Spiel **Free Chat** eingestellt hat.
- 🎭 **Rollenwünsche** (nur als Host) – im Discord kann sich jeder mit `/rollenwunsch` für 20 XP
  (2× pro Spieleabend) eine Rolle für die nächste Runde wünschen, Admins legen mit `/rolle-zuweisen`
  Rollen fest. Drückt der Host auf Start, holt die Mod die Wünsche der Spieler in der Lobby ab und
  schreibt in den Chat, wie viele Rollen per Discord kommen. Anzahl Impostor und Spezialrollen bleiben
  wie eingestellt; was nicht klappt, gibt die XP zurück. Der Host braucht dafür im Spiel **Free Chat**
  (mit „Nur Quick Chat“ bleiben die Wünsche für eine spätere Runde offen). Abschalten: `Enabled = false` im Abschnitt
  `[Rollenwuensche]` der Config.
- 🏅 Aus den Runden berechnet der Bot außerdem **Highlights**, **Erfolge** (`/erfolge`),
  **Rivalen** (`/stats`) und einen **Spieleabend-Recap** (`/recap`).

Während der Runde zeigt die Mod **nichts** an – sie verschafft keinen heimlichen Vorteil. Rollenwünsche
kann jeder nutzen, und der Host kündigt sie vor dem Start im Chat an.

## Installieren

1. Unter [**Releases**](../../releases/latest) die Datei **`CrewStats-Installer.bat`** herunterladen.
2. Doppelklicken. Windows warnt evtl. vor einer unbekannten Datei → **Weitere Informationen → Trotzdem ausführen**.
3. Enter drücken – der Installer findet Among Us, installiert BepInEx (64-Bit) und die Mod.
4. Die **Webhook-URL** brauchst du nur, wenn du hostest – du bekommst sie vom Server-Admin.
   Sie ist geheim, bitte nicht weitergeben.
5. Im Discord einmal deinen Friendcode verknüpfen: `/verknuepfen`

Der erste Spielstart dauert 1–2 Minuten. Danach **aktualisiert sich die Mod selbst**: Gibt es eine
neue Version, wird sie beim Spielstart geladen und ist ab dem nächsten Start aktiv (Hinweis im Hauptmenü).

## Deinstallieren

Installer nochmal starten → **[2] Deinstallieren**. Danach startet Among Us wieder ganz normal.

## Hinweise

- Nur die **Steam**-Version von Among Us wird unterstützt (ab Version 2026.9.29, 64-Bit).
- Auto-Update abschalten: in `Among Us\BepInEx\config\de.amongusmanager.crewstats.cfg`
  `AutoUpdate = false` setzen.
- Probleme? `Among Us\BepInEx\LogOutput.log` an den Server-Admin schicken.

## Für Entwickler

Quellcode in `CrewStats/`, Installer in `installer/`. Neue Version veröffentlichen:
Version in `CrewStats/CrewStats.csproj` erhöhen, dann `.\release.ps1 "Was ist neu"`.
