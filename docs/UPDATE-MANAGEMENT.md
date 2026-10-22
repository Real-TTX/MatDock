# Konzept: Update-Management

Kontrolliertes Aktualisieren von Stack-/Container-Images — sicher (optionales Backup vorher,
Health-Check nachher, automatischer Rollback), flexibel (manuell mit Bestätigung oder automatisch),
registry-übergreifend (Docker Hub, ghcr.io, private) und mit Versions-Pinning/Override.

Baut auf vorhandenen Bausteinen auf: Registry-v2-API (`RegistryApiClient`), Stacks/Compose-Deploy
(`StackService`/`StackCommands`), Volume-/Bundle-Backup, Schedules und `ContainerRegistry`.

## Stand (Oktober 2026)

| Teil | Stand |
|---|---|
| Erkennung bewegter Tags (lokaler Digest gegen Registry-Digest) | umgesetzt (`UpdateService.CheckStackAsync`) |
| Manuelles Update mit optionalem Bundle-Backup vorher | umgesetzt (Stacks → Update, `/api/v1/stacks/{name}/actions/update`) |
| Badge „Update“ in der Stacks-Liste | umgesetzt (letzte Prüfung im Speicher, `UpdateCheckCache`) |
| Zeitgesteuert: nur prüfen und melden, oder anwenden | umgesetzt (Schedule-Aktion *Update stacks*) |
| Health-Gate (90 s, laufend und gesund) und Rollback: das alte Image bekommt vor dem Pull einen eigenen Tag (`:matdock-rollback-<tag>`) und bei einem Fehler seinen Tag zurück, dann neu deployen | umgesetzt (Update-Seite, Schedule-Aktion; die API wie bisher ohne) |
| Update-Historie je Stack (wer, wann, Ergebnis, welche Services mit welchem Image; die letzten 50) | umgesetzt (`StackUpdateRun`, Update-Seite) |
| Pinning je Service: ein gepinnter Service wird beim `compose pull` ausgelassen (manuell, Schedule, API) und nie als Update gemeldet | umgesetzt (`Stack.PinnedServices`) |
| Hinweis auf eine neuere Versions-Tag (`1.2.3` → `1.3.0`, nur reine Versionen, nur Information) | umgesetzt (`TagVersion`) |
| Eigene Version pro Service (Image-Override): MatDock läuft ein anderes Image als die Compose nennt, die Datei (Git oder Editor) bleibt unverändert; Basis-Prüfung, Backup/Restore | umgesetzt (`Stack.ImageOverridesJson`, `matdock-override.yml`, Update-Seite, `PUT /api/v1/stacks/{name}/images/{service}`) |
| Eigene Update-Regeln (global, je Environment, je Stack; Strategie auch je Service): Modus Aus/Melden/Automatisch, Strategie Tag/Minor/Latest/Pinned, Zeitfenster, Intervall, Backup, Rollback, Meldungen | umgesetzt (`UpdatePolicy`, `/Updates/Rules`) |
| Automatik: prüft je Stack nach seiner Regel, meldet eine neue Version einmal, wendet sie im Automatik-Modus im Zeitfenster an | umgesetzt (`UpdateAutomation`, alle 15 Minuten, `UpdateNotice`) |
| Update-Übersicht über alle Environments mit Prüfen und Update-All, Kennzahl im Dashboard | umgesetzt (`/Updates`) |

## 1. Was heißt „Update"?
- **Bewegtes Tag** (`:latest`, `:stable`): gleicher Tag, **neuer Digest** in der Registry → Image geändert.
- **Neue Version** (gepinnt `:1.2.3`): höheres Tag (`:1.3.0`) existiert → Versionssprung verfügbar.
- Optional **Compose-Update** bei Git-Stacks (neuer Commit) — läuft über *Sync*; Update-Management ergänzt die *Image*-Ebene.

## 2. Update-Erkennung
- Pro Service-Image: **laufenden Digest** (`docker image ls --digests` bzw. `image inspect`) mit dem
  **Registry-Digest** von `repo:tag` vergleichen (`RegistryApiClient.GetDigestAsync` per v2-Manifest-HEAD,
  Bearer-Flow vorhanden).
- Gepinnte Versionen: Tag-Liste (`ListTagsAsync`) + Semver-Vergleich → „neuere Version verfügbar".
- Registry-Auswahl automatisch per Image-Host über vorhandene `ContainerRegistry`-Credentials.
- Ergebnis gecacht → Badge in der UI statt Live-Call pro Aufruf.

## 3. Modi (pro Stack)
- **Manuell + Bestätigung** (Default): Diff (alt→neu) + Optionen (Backup/Rollback) → bestätigen.
- **Automatisch** via **Schedules** (neue Aktion `UpdateStack`, Cron) mit hinterlegten Sicherheitsoptionen.
- **Nur benachrichtigen**: Schedule prüft und meldet über die Notification-Kanäle.

## 4. Ablauf eines Updates (sicher)
1. (optional) **Backup vorher** (Bundle-/Volume-Backup).
2. **Aktuelle Digests merken** (Rollback); alte Images bleiben lokal.
3. **Pull** der neuen Images (mit `docker login`).
4. **Redeploy** (`compose up -d`).
5. **Health-Gate**: alle Services running/healthy und N Sekunden stabil.
6. **Erfolg** → Backup je nach Einstellung behalten/verwerfen.
7. **Fehler / kommt nicht hoch** → **Rollback**.

## 5. Rollback & Backup-Aufbewahrung
- Rollback: Redeploy mit gemerkten **alten Digests** (Override auf `repo@sha256:alt`) — schnell (Images noch lokal).
- Backup behalten (wenn gewünscht) + optionaler Volume-Restore aus dem Pre-Update-Backup.
- Update-Läufe werden protokolliert (was, alt→neu, Ergebnis, Rollback).

## 6. Version pinnen / überschreiben
- **Pinnen**: Service auf festes Tag/Digest → Auto-Update lässt ihn in Ruhe.
- **Override**:
  - Inline-Stacks: Image-Tag direkt im YAML setzen.
  - Git-Stacks (Repo unverändert): generierte **Override-Datei**, Deploy mit `-f compose.yml -f matdock-override.yml`.

## 7. Datenmodell (neu)
- `StackUpdatePolicy` (pro Stack): `Mode`, `PreUpdateBackup`, `RollbackOnFailure`, `KeepBackupOnFailure`,
  `HealthGraceSeconds`, `AutoScheduleCron`/verknüpfter Schedules-Task.
- Pro Service (JSON): `PinnedRef` / `OverrideTag`.
- `UpdateCheckResult`-Cache je Stack/Service; `UpdateRun`-Historie.

## 8. UI / Integration
- Stacks-Liste/-Detail: „Updates"-Badge, *Auf Updates prüfen*, *Update* (Dialog mit Diff + Optionen);
  pro Service Pin-/Override-Control.
- Dashboard: Kennzahl „Updates verfügbar".
- Schedules: *Update anwenden* / *Nur prüfen & melden*.
- Registry: wiederverwendet für Digest/Tag-Abfragen + Login.

## 9. Edge Cases
Multi-Arch (Manifest-Listen-Digest), `build:`-Services (übersprungen), Standalone-Container
(v1 Fokus Stacks), Registry-Rate-Limits (via Credentials), private/insecure Registries.

## 10. Phasenplan
1. **Erkennung + manuelles Update**: Digest-Vergleich (bewegte Tags), Badge, „Update" mit optionalem Pre-Backup.
2. **Health-Gate + Rollback** (alte Digests) + Backup-Aufbewahrung.
3. **Auto-Update via Schedules** (anwenden / nur-melden).
4. **Pinning & Override** (Semver für gepinnte Services, Override-Compose für Git-Stacks).

## 11. Image-Override: das Tag der Compose ist nur der Startwert

MatDock kann pro Service ein anderes Image laufen lassen, als die Compose nennt, ohne die Compose zu ändern (weder im Git noch im Editor).

- **Speicherort:** `Stack.ImageOverridesJson`, je Service `{base, image}`. `base` ist das Image, das die Compose beim Setzen nannte (so geschrieben, mit Variablen), `image` das, das MatDock laufen lässt.
- **Wirkung:** Beim Deploy schreibt MatDock `matdock-override.yml` neben die Compose (nur `services.<name>.image`), alle Compose-Befehle (deploy, update, pull, up, down, start, stop, restart, Git-Sync) nennen sie als zweite `-f`-Datei, wenn sie existiert. Ohne Override wird die Datei entfernt und compose läuft wie früher.
- **Compose wird bearbeitet:** Eine Änderung an anderer Stelle lässt den Override in Ruhe. Ändert sich die Image-Zeile des Services (Basis stimmt nicht mehr) oder verschwindet der Service, gewinnt die Compose: der Override wird verworfen und das gemeldet (Meldung beim Speichern, im Deploy-Ausgang). Halb getippte Dateien (kein gültiges YAML, nur `include:`) kosten keinen Override.
- **Backup/Restore:** Das Bundle-Manifest enthält die Overrides (`ImageOverrides`; null = ältere Bundles, ein Restore lässt dann die aktuellen). Ein Restore stellt nur die wieder her, die zur wiederhergestellten Compose passen.
- **Bedienung:** Stacks → Update (Feld je Service, Zurück-Knopf), Hinweis im Compose-Editor, API `PUT /api/v1/stacks/{name}/images/{service}` (`{image}`, leer = zurück), `imageOverrides` im Stack-Detail.
- **Grenze:** Wer auf dem Host direkt `docker compose up` ohne MatDock ausführt, bekommt den Compose-Stand ohne Override.

## 12. Update-Regeln und Automatik

- **Regeln:** `UpdatePolicy` je Geltungsbereich (global, Environment, Stack). Jeder Wert darf leer sein ("wie die weitere Regel"); für einen Stack gewinnt je Wert die engste Regel, dahinter kommt der eingebaute Standard (Modus Aus, Strategie Tag, alle 6 Stunden, Backup und Rollback an, Meldungen an). Das Zeitfenster (Serverzeit, Start/Ende, über Mitternacht möglich, gleich = ganztägig) gilt als Paar.
- **Strategien:** *Tag* (neuer Digest unter demselben Tag), *Minor* (neueste Version mit derselben ersten Zahl), *Latest* (neueste Version), *Pinned* (nie). Je Service in der Regel des Stacks überschreibbar. Versionen sind Tags aus Zahlen und Punkten (mindestens zwei Zahlen); ein Suffix wie `-alpine` ist eine eigene Reihe: `16.4-alpine` zieht nur zu `16.9-alpine`, nie zu `16.9`.
- **Ablauf der Automatik** (`UpdateAutomation`, Hintergrunddienst alle 15 Minuten): je Stack mit Modus Melden oder Automatisch, höchstens so oft wie das Intervall der Regel, wird geplant (`UpdateService.PlanStackAsync`). Neue Images werden einmal gemeldet (`UpdateNotice`, je Stack, Service und Ziel). Im Automatik-Modus innerhalb des Fensters setzt MatDock für Versionen einen Image-Override (Abschnitt 11) und führt das Update mit Backup und Health-Gate der Regel aus (Verlauf: Auslöser `auto`). Dann wird das Ergebnis gemeldet.
- **Fehlschlag:** Ein Update, das nicht zustande kam (Backup oder Pull fehlgeschlagen) oder zurückgerollt wurde, nimmt seinen Override zurück und wird nicht erneut versucht, bis ein neueres Ziel da ist. Bringt es unhealthy Container ohne Rollback hoch, bleibt der Override (der Stack läuft, was er sagt).
- **Rollback und Override:** Ein Rollback deployt den Stack erneut; damit er das alte Image wirklich bringt, setzt er vorher die Overrides der Services zurück, deren Image-Referenz das Update geändert hat.
- **Bedienung:** `/Updates` (Übersicht, Prüfen, Update-All, Regel je Stack), `/Updates/Rules?scope=Global|Environment|Stack` (nur Administratoren), Kennzahl "Updates available" im Dashboard. Der Stand der letzten Prüfung liegt nur im Speicher (`UpdateCheckCache`).
