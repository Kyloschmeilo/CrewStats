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
- 🏅 Aus den Runden berechnet der Bot außerdem **Highlights**, **Erfolge** (`/erfolge`),
  **Rivalen** (`/stats`) und einen **Spieleabend-Recap** (`/recap`).

Während der Runde zeigt die Mod **nichts** an – sie verschafft keinen Vorteil.

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
