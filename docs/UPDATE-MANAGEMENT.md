# Konzept: Update-Management

Kontrolliertes Aktualisieren von Stack-/Container-Images — sicher (optionales Backup vorher,
Health-Check nachher, automatischer Rollback), flexibel (manuell mit Bestätigung oder automatisch),
registry-übergreifend (Docker Hub, ghcr.io, private) und mit Versions-Pinning/Override.

Baut auf vorhandenen Bausteinen auf: Registry-v2-API (`RegistryApiClient`), Stacks/Compose-Deploy
(`StackService`/`StackCommands`), Volume-/Bundle-Backup, Schedules und `ContainerRegistry`.

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
