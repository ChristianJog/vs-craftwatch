# CraftWatch

Diagnose-Mod für Vintage Story 1.22. Beobachtet den Rezeptabgleich und das Crafting-Grid und
protokolliert Abweichungen, um den Fehler „Uncraftable items"
([VintageStory-Issues#9175](https://github.com/anegostudios/VintageStory-Issues/issues/9175))
beim Entstehen zu erfassen. Der Mod greift nicht in das Spielverhalten ein (kein Harmony).

Analyse und Hintergründe: `E:\kDrive\Games & Twitch\Vintage Story (und Voxel Games)\Crafting-Bug 9175`.

## Bauen

Voraussetzung: .NET-10-SDK, Spiel unter `%APPDATA%\Vintagestory`.

    dotnet build -c Release

Ergebnis in `bin\craftwatch\` (`CraftWatch.dll`, `modinfo.json`). Beide Dateien als Zip nach
`%APPDATA%\VintagestoryData\Mods` legen. Anderer Installationsort: `-p:VSPath=...`.

## Log

`%APPDATA%\VintagestoryData\CraftWatch\craftwatch-<Datum>_<Zeit>.log`, eine Datei je Spielstart.
`[S]` ist die Server-Seite, `[C]` die Client-Seite.

| Kennung | Bedeutung |
|---|---|
| `BASELINE` | Grundstand beim Start: Zahl der Rezepte und gemeinsamen Zutaten |
| `GRID` | Inhalt des Crafting-Grids, Ausgabefeld, Ergebnis der eigenen Rechnung |
| `GUI` | Dialog geöffnet oder geschlossen (nur Client) |
| `EVENT` | Pause, Speichern, Spieler kommt oder geht |
| `SHARED` | ein gemeinsamer Zutaten-Stack hat Menge ≠ 1 oder Attribute |
| `ATTRS` | die statischen Standard-Attribute wurden befüllt oder ersetzt |
| `ALARM` | Befund: Zutat ersetzt, Rezeptdaten geändert, Gegenprobe schlägt fehl, Ausgabefeld bleibt trotz passendem Rezept leer |
| `DIAG` | Zerlegung zu einem Alarm |
| `MARK` | von Hand gesetzte Markierung über `/cw <text>` |

Im Spiel schreibt `/cw <text>` eine Markierung und den vollständigen Zustand des eigenen Grids.

## Aufbau

| Datei | Aufgabe |
|---|---|
| `src/CraftWatchSystem.cs` | Einstieg, Ereignisse, Meldung Client → Server, Befehl `/cw` |
| `src/RecipeWatcher.cs` | gemeinsame Zutaten-Stacks, Standard-Attribute, Rezeptdaten, Gegenprobe |
| `src/GridWatcher.cs` | Crafting-Grid eines Spielers, Erkennung der Fehlersignatur |
| `src/Describe.cs` | Textdarstellung ohne Seiteneffekte |
| `src/CwLog.cs` | Logdatei |
