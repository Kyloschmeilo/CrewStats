# CrewStats-Rundenformat (Version 2)

Nach jeder Runde schickt CrewStats (nur beim **Host**) eine Datei `crewstats-round.json` per Discord-Webhook an den Bot.
Eine Kopie bleibt auf dem PC des Hosts in `Among Us\BepInEx\CrewStats\sent\` (nicht gesendete in `pending\`).
Der Bot speichert jede Runde zusätzlich unverändert in der Tabelle `round_raw` – das ist die Datenquelle für eine
spätere Stats-Website.

Ein vollständiges Beispiel steht in [`beispiel-runde.json`](beispiel-runde.json).

## Grundregeln

- JSON, Feldnamen in camelCase, UTF-8, ohne Einrückung.
- **Zeiten** sind Sekunden seit Rundenstart (Beginn des Intros), auf 0,01 s gerundet (`atSeconds`, `…Seconds`).
  `startedAt`/`endedAt` sind UTC-Zeitpunkte (ISO 8601).
- **Spieler** werden überall über ihre `playerId` (0–254, nur innerhalb der Runde eindeutig) angegeben. Über
  `players[].friendCode` lässt sich ein Spieler rundenübergreifend erkennen (der Bot verknüpft ihn mit Discord).
- **Koordinaten** `x`/`y` sind Spielkoordinaten der jeweiligen Map (Einheiten, 2 Nachkommastellen) – dieselben für
  Kills, Aufgaben, Vents, Meetings und Laufwege, also direkt als Heatmap nutzbar.
- **Räume** (`room`) sind Namen aus dem Spiel-Enum `SystemTypes` (z.B. `Electrical`, `Cafeteria`, `MedBay`); `null`,
  wenn die Position in keinem Raum liegt (Flur ohne eigenen Raum).
- Version 2 hängt nur Felder an. Felder aus Version 1 behalten Name und Bedeutung (der Bot wertet sie aus).
  Fehlt ein Feld oder ist es `null`, war die Info nicht lesbar (z.B. nach einem Among-Us-Update) – Auswertungen müssen
  das aushalten. In Ereignissen fehlen leere Felder ganz.
- Rollen-Mod **The Other Roles** (TOR): Felder mit `mod…` gibt es nur damit.

## Runde (oberste Ebene)

| Feld | Typ | Bedeutung |
|---|---|---|
| `version` | int | Formatversion (2) |
| `gameId` | string | eindeutige Id der Runde (32 Hex-Zeichen) |
| `lobbyCode` | string | Lobby-Code, `FREEPLAY` beim Test |
| `map` / `mapId` | string / int | Map-Name / Id (0 Skeld, 1 MIRA HQ, 2 Polus, 3 dlekS, 4 Airship, 5 Fungle) |
| `startedAt` / `endedAt` | datetime | Start/Ende (UTC) |
| `durationSeconds` | number | Länge in Sekunden (ganze) |
| `endReason` | string | Grund des Spielendes (`GameOverReason`, mit TOR auch `JesterWin` …) |
| `winningTeam` | string | `CREW`, `IMPOSTOR`, `NEUTRAL` (TOR-Sondersieg) oder `UNKNOWN` |
| `mod` | string? | z.B. `TheOtherRoles GMIA 1.4.1` |
| `modGameMode` | string? | TOR-Spielmodus (`Classic`, `Guesser` …) |
| `crewStatsVersion` | string | Version der Mod |
| `gameVersion` | string | Among-Us-Version |
| `server` | string | Region, z.B. `Modded NA` |
| `gameMode` | string | `Normal` oder `NormalFools` |
| `endedBy` | string | `game` (normales Ende) oder `freeplay-exit` |
| `hostPlayerId` | int | wer aufgezeichnet hat |
| `players` | [Spieler] | alle Spieler |
| `kills` | [Kill] | alle Kills (ohne Selbst-Kills) |
| `meetings` | [Meeting] | alle Meetings mit Stimmen |
| `modDeaths` | [TOR-Tod] | alle Tode laut TOR (`reason`: Kill, Exile, Guess, Jailed, Suicide, LoverSuicide …) |
| `settings` | Einstellungen | Lobby-Einstellungen beim Start |
| `modSettings` | {string: string} | TOR-Einstellungen: Option → gewählter Wert (Texte wie im TOR-Menü, ohne Farben) |
| `events` | [Ereignis] | Zeitleiste, nach Zeit sortiert |
| `tracks` | [Laufweg] | Laufwege der Spieler |
| `trackIntervalSeconds` | number | Abstand der Laufweg-Punkte (1 s) |

## Spieler (`players[]`)

| Feld | Typ | Bedeutung |
|---|---|---|
| `playerId`, `name`, `friendCode`, `puid` | | Identität (`puid` = Epic-Konto-Id) |
| `role` / `endRole` | string | Spiel-Rolle beim Start (`Crewmate`, `Engineer`, `Shapeshifter` …) / am Ende, falls anders (`CrewmateGhost` …) |
| `team` | string | `CREW`, `IMPOSTOR`, `NEUTRAL` (TOR; Madmate = `IMPOSTOR`) |
| `modRole` / `modStartRole` | string? | TOR-Rolle am Ende (nameKey, z.B. `sheriff`) / beim Start, falls gewechselt |
| `modifiers` | [string]? | TOR-Modifikatoren (`lover`, `mini`, `madmate` …) |
| `won` | bool | hat gewonnen |
| `isDead`, `disconnected` | bool | Zustand am Ende |
| `deathCause` | string? | `kill`, `ejected`, `disconnect`, `suicide`, mit TOR auch `guess`, `jailed` … |
| `killedBy` | int? | Täter |
| `diedAtSeconds`, `disconnectedAtSeconds` | number? | Zeitpunkte |
| `kills` | int | Anzahl Kills (mit TOR auch Sheriff-/Guesser-Abschüsse) |
| `tasksTotal` / `tasksCompleted` | int | nur Crew |
| `tasks` | [Aufgabe]? | nur Crew: `{ id, type, completed, completedAtSeconds }`, `type` aus `TaskTypes` (z.B. `FixWiring`) |
| `isHost` | bool | ist der aufzeichnende Host |
| `colorId` / `color` | int / string | Farbe (englischer Name: Red, Blue, …) |
| `hat`, `skin`, `visor`, `pet`, `namePlate` | string? | Kosmetik-Ids des Spiels (`null` = keine) |
| `level` | int | Among-Us-Level (Wert des Spiels) |
| `platform` | string | `StandaloneSteamPC`, `StandaloneEpicPC`, `Android`, `IPhone`, `Switch` … |
| `summary` | Kennzahlen | siehe unten |

### Kennzahlen (`players[].summary`)

Von der Mod beim Rundenende berechnet (`RoundStats.cs`) – lassen sich aus den Rohdaten auch selbst nachrechnen.

| Feld | Bedeutung |
|---|---|
| `distance` | gelaufene Strecke (Einheiten) aus dem Laufweg, ohne Sprünge (Vents, Teleports) |
| `aliveSeconds` | Zeit bis Tod/Disconnect bzw. Rundenende |
| `meetingsCalled` / `bodiesReported` | Notfall-Knopf / gemeldete Leichen |
| `votesCast` / `skips` | Stimmen auf einen Spieler / „Überspringen“ |
| `correctVotes` | Stimmen eines Crew-Spielers gegen einen Impostor |
| `ventUses` / `ventSeconds` | Vent-Einstiege / Zeit im Vent |
| `sabotages` | gestartete Sabotagen (soweit zuordenbar) |
| `sabotagesFixed` | beendete Sabotagen, an deren System der Spieler gearbeitet hat |
| `firstTaskSeconds` / `allTasksSeconds` | erste Aufgabe fertig / alle Aufgaben fertig |

## Kill (`kills[]`)

`atSeconds`, `killer`, `victim`, dazu `x`, `y`, `room` (Ort des Opfers = Leiche), `killerRole`, `victimRole`
(TOR-Rolle bzw. Spiel-Rolle zum Zeitpunkt).

## Meeting (`meetings[]`)

| Feld | Bedeutung |
|---|---|
| `atSeconds` / `endedAtSeconds` | Beginn / Abstimmung fertig |
| `caller` | wer das Meeting ausgelöst hat |
| `body` | gemeldete Leiche, `null` = Notfall-Knopf |
| `callerX`, `callerY`, `callerRoom` | wo der Auslöser stand |
| `bodyRoom` | wo die Leiche lag |
| `alive` | lebende Spieler zu Beginn |
| `votes[]` | `{ voter, target, skipped, atSeconds }` – `target` `null` heißt übersprungen (`skipped: true`) oder nicht abgestimmt (`skipped: false`); `atSeconds` = wann abgestimmt wurde |
| `exiled`, `tie` | Rausgewählter / Gleichstand |

## Einstellungen (`settings`)

`maxPlayers`, `numImpostors`, `ints`, `floats`, `bools` (alle Lobby-Optionen unter ihrem Spielnamen, z.B.
`KillCooldown`, `NumEmergencyMeetings`, `ConfirmImpostor`) und `roles` (`{ "Engineer": { count, chance } }`) –
gleiche Namen wie `crewstats-settings.json` für `/rundensettings`.

## Ereignisse (`events[]`)

Jedes Ereignis hat `atSeconds` und `type`. `player` ist der Handelnde, `target` der Betroffene.

| `type` | Felder | Bedeutung |
|---|---|---|
| `kill` | player, target, x, y, room, role | Kill (role = Rolle des Täters) |
| `death` | target, player?, reason | Tod laut TOR, der kein normaler Kill/Rauswurf ist (`guess`, `jailed`, `suicide` …) |
| `meeting` | player, target?, x, y, room, reason | Meeting: `reason` `report` (target = Leiche) oder `emergency` |
| `vote` | player, target? / skipped | Stimme |
| `exile` | target?, tie | Ergebnis der Abstimmung (ohne target: niemand raus) |
| `task` | player, task, x, y, room | Aufgabe fertig (auch von Geistern) |
| `sabotage` | system, player? | Sabotage beginnt: `Reactor`, `Laboratory`, `Oxygen`, `Lights`, `Comms`, `Crash`, `MushroomMixup` |
| `repair` | player, system | Spieler hat an der Sabotage gearbeitet, die gerade beendet wurde |
| `sabotageEnd` | system, reason, durationSeconds | Sabotage vorbei: `fixed`, `meeting` oder `gameEnd` |
| `doors` | room, player? | Türen eines Raums geschlossen |
| `ventEnter` / `ventExit` | player, vent, x, y, room | Vent rein/raus (`vent` = Vent-Id der Map; `reason: meeting`, wenn das Meeting ihn rausgeholt hat) |
| `disconnect` | player | Spieler hat das Spiel verlassen |
| `roleChange` | player, role, previous | TOR-Rolle hat gewechselt (Sidekick, Shifter, Thief …), alle 2 s geprüft |
| `shapeshift` | player, target, reason? | Gestaltwandler (`reason: revert` = zurückverwandelt) |
| `protect` | player, target | Schutzengel schützt jemanden |
| `revive` | player | Spieler wurde wiederbelebt |

`player` bei `sabotage`/`doors` ist der Spieler, der in der Sekunde davor das Sabotage-/Tür-System benutzt hat – bei
Sabotagen durch Rollen-Fähigkeiten kann es fehlen.

## Laufwege (`tracks[]`)

`{ player, points: [t, x, y, t, x, y, …] }` – ein Punkt pro Sekunde, nur solange der Spieler lebt und kein
Meeting läuft (Lücken = Meeting oder Tod). Wird die Datei größer als 7 MB, fehlen die Laufwege (Discord-Grenze 10 MB).

## Fairness

Die Mod zeichnet nur beim Host auf, liest nur, was der Host ohnehin weiß, zeigt im Spiel nichts an und verschickt
alles erst **nach** dem Spielende.
