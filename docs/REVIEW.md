# MatDock review (October 2026)

A review of the whole app (every page in light, dark and at 390 px, the code, the security) against what MatDock is
for — the Docker engine of a homelab or a small business, driven by people in the browser and by Mattiverse through
`/api/v1` — and a comparison with Dockhand. Branch `feature/review`; `/api/v1` is unchanged (its pinning tests pass).

## 1. Found and fixed

**Security**

| Finding | Fix |
|---|---|
| The role *User* could create and edit environments (a *Local* one is MatDock's own Docker socket), create volumes with any driver options (`type=none,o=bind,device=/`), migrate them and restore, download or delete backups: together a way to read and write any file of any host as root | `/Environments/Edit`, `/Environments/Migrate`, `/Volumes/Create` and `/Backups` are admin-only; the links are hidden for users. *User* = look around, start/stop/restart containers, read logs |
| The connection test reused the stored SSH password or key for whatever host the form named: a test against another host handed it over | The stored secret goes only to the same host, port and user; saving another endpoint without a new secret drops the old one |
| SSH host keys were never checked (a man in the middle got the password and the hosts) | Trust on first use: the SHA256 fingerprint is saved at the first good connection, another key is refused with a clear message; *Forget* on the environment after a reinstall |
| A file name with a quote in a volume ran as script in the file browser (`onclick="…'@e.Name'"`: the browser decodes the attribute before it runs it) | No inline event handlers anywhere (data attributes + `matdock.js`); a test keeps the pages free of them |
| No limit on password guessing (the documented seed is `admin/admin`) | 5 wrong passwords per account and address, or 20 per address, lock the sign-in for 15 minutes |
| A bundle restored under a new name could replace another stack | Refused |
| A git repository could make MatDock read its own files into a stack (symlinked compose, `../` icon path) | Compose, sidecar and icon are read only inside the clone and never through a link |

Checked and fine: the shell quoting of every docker command (allow-list regexes, POSIX quoting, `/bin/sh -c` with an
argument list), path normalisation in the file browsers and backups, antiforgery on every form, the `Origin` check of
both WebSockets, the constant-time API key, secrets never rendered back, `returnUrl` handling.

**Bugs**

- The dashboard read backup figures from the old `BackupSchedule` table that nothing writes since the Schedules took
  over (next/last run empty) – it reads the backup tasks and bundles now.
- Two restore confirmations never appeared (`data-confirm` on a button was ignored) – the button's question counts.
- Deleting the copy of a sync job's timing from the Schedules list left the job without a schedule – not offered there.
- Downloads from the file browser stopped at 512 KB (they used the editor's read) – the whole file is streamed.
- Navigation: Backups was marked on Schedules, System not on Git repos; "Environment" tiles showed `local@local:0` for a
  Local environment ("Docker socket" now).

**Clean-up**

- Texts: German leftovers (error page, notifications, terminal, aria labels) in English; about 40 hints and confirms
  shortened or removed (implementation talk such as "compose pull + up -d", tar/SSH/busybox boxes, repeated hints);
  status messages name the container instead of its id.
- Page patterns (MatDesign): "New …" below every list (Stacks had it twice), *Delete* on the edit pages in the order
  Save, Back, space, Delete, *Back* instead of *Cancel*, no Back duplicated in the header, System tabs on all System
  pages, empty states where there were none (Apps, Update, filtered Stacks, Schedules, System lists).
- 105 static inline styles became classes; the inline scripts of the Containers page and the menu went to `matdock.js`.
- Dead code removed: the old backup and sync schedulers (not registered since the Schedules run everything),
  `BackupScheduleService`, its runner and input, `SyncJobService.RunDueAsync`.
- `docs/UPDATE-MANAGEMENT.md` has a status table.

Left as it is, on purpose: the Proxy page adds a route with a small form above the list (a quick action, not CRUD);
dynamic inline styles (gauge widths). MatDock has its own stylesheet, not MatUi (see plan item 9).

## 2. Against Dockhand

✅ has it · ◐ partly · ✖ missing · — not wanted (why)

| Dockhand | MatDock | |
|---|---|---|
| Containers start/stop/restart, status, resources, ports | ✅ | The lists refresh themselves every 20–30 s (new) |
| Terminal in the browser (exec) | ✅ | bash when the container has it; now also on the local Docker socket (it said "not supported yet") (new) |
| Log streaming with search and filter | ✅ | Plus a whole stack at once and timestamps (new) |
| File browser in containers (browse, upload, download, edit) | ✅ | New: the volume browser on a container (`docker exec`, needs a shell; plan 5) |
| Compose stacks, YAML editor, environment variables | ◐ | Highlighted YAML + `.env` editor, check on save (new); side by side and a visual editor missing (plan 7) |
| Deploy from Git, webhooks, auto-sync | ✅ | HTTPS token credentials only (plan 8) |
| Images: tags, sizes, cleanup | ✅ | Prune dangling or all unused (new) |
| Image update tracking, automatic updates | ✅ | Badge in the Stacks list, schedule *Update stacks* (notify or apply, backup first) (new); no health gate/rollback (plan 2) |
| Layer inspection | ✅ | Image detail page (new) |
| CVE scanning (Grype/Trivy) | ✖ | Plan 3 |
| Volume browser | ✅ | |
| Network graph | ✅ | Network map per environment (new) |
| Hosts over socket and SSH | ✅ | Agentless SSH is MatDock's point; host keys verified (new) |
| Hosts over TCP/TLS | ◐ | Only as an auto-detected `DOCKER_HOST` behind SSH (plan 4) |
| Hosts with an agent | — | Not wanted: MatDock exists to need nothing on the hosts |
| Per-host dashboard: live CPU, memory, disk | ✅ | Refreshes itself (new); CPU is load-based, no history (plan 10) |
| Cron schedules: prune, updates, cleanup, backups | ✅ | |
| restic backups (backup, restore, check, prune) | ✖ | Tar archives to local/SMB with retention and restore exist; plan 11 |
| Alerts by e-mail/webhook on container events, health, updates | ✅ | *Container died*, *Container unhealthy*, updates available (new), plus offline/deploy/backup/sync |
| Local users, roles | ◐ | Two fixed roles (Admin, User), now enforced; per-environment rights not planned (homelab/small team) |
| OIDC | ✅ | Through MatIdentity; any other OIDC provider is not wanted — MatIdentity is the family's identity |
| 2FA | — | Not in MatDock: MatIdentity does it for every Mat app; the local sign-in is the fallback (now throttled) |
| Activity/audit log across environments | ✅ | System → Activity (new) |

## 3. Plan for what is missing (priority order)

| # | What | Where | Effort | Risks |
|---|---|---|---|---|
| 1 | **Secrets off the command line**: send the stack `.env` (and the CIFS password of a new volume) over stdin instead of `echo <base64>` in the command, which `ps` on the host shows | `StackCommands` (deploy/git sync script), `VolumeCommands` create; stdin is already used for uploads (`IHostCommand.CreateInputStream`) | S | Changes the deploy command for every stack: test SSH and local; keep `/api/v1` operation output the same |
| 2 | **Updates with a health gate and rollback**: remember the image digests before an update, wait N seconds for running/healthy, redeploy the old digests (`repo@sha256:…` override file) when it fails; keep a short update history | `UpdateService.RunUpdateAsync`, new `matdock-override.yml` in `StackCommands`, `ScheduleOptions` (grace seconds), Stacks/Update page | M | Override files must not leak into git-backed stacks; Mattiverse expects `{changed}` unchanged |
| 3 | **CVE scan of an image**: run Trivy on the host (`docker run --rm -v /var/run/docker.sock:/var/run/docker.sock aquasec/trivy image --format json <ref>`), store the summary (critical/high/medium) per image digest, show it on the image page and as a column; optional schedule action *Scan images* that notifies on new criticals | new `Core/Images/ImageScanService`, `ScheduleAction.ScanImages`, Images/Detail | M | Trivy downloads its DB (~100 MB, needs internet, slow first run); run in the background as an operation; cache by digest |
| 4 | **TCP/TLS hosts**: a connection type *Docker API (TCP/TLS)* with host, port and client certificate/key/CA; commands run locally with `DOCKER_HOST=tcp://…` and `DOCKER_TLS_VERIFY`, certificates written to a temp dir per call | `ConnectionType`, `EnvironmentService`, `LocalHostSession` (environment of the process), Environments/Edit | M | Certificates are secrets (encrypt like SSH keys); the terminal and file browser need the same path |
| 5 | **Files of a container without a shell** (distroless): fall back to `docker cp` (tar over stdout/stdin) for list/read/write when `sh` is missing | `VolumeFileService` (detect exit 126/127), `VolumeFileCommands` | M | Listing via tar of a directory can be big; limit depth |
| 7 | **Compose editor side by side with `.env`** on wide screens, and `docker compose config` on the host as a second check before deploy | Stacks/Edit (CSS grid), `StackCommands` | S | `compose config` needs the files on the host first (write to a temp dir) |
| 8 | **SSH key credentials for Git** (listed as "future") | `GitCredential`, `GitRepositoryService` (LibGit2Sharp SSH needs libssh2 – check the build) | M | LibGit2Sharp's SSH support differs per platform |
| 9 | **Move the UI onto MatUi** (shell, toolbar, dialogs, `data-label` tables) – MatDock has its own stylesheet, so the MatDesign rules are followed by hand | all pages, `_Layout`, `matdock.css` | L | Large visual change; do it page by page behind the same markup helpers (`mat-table`, `mat-field`) |
| 10 | Host history: keep the CPU/memory/disk samples of the health monitor for 24 h and draw a sparkline per environment | `EnvironmentHealthMonitor`, dashboard | S | Database growth (ring buffer in memory is enough) |
| 11 | **restic** as a backup target kind (encrypted, deduplicated, S3/B2/SFTP): backup = `restic backup` of the volume tar stream (`--stdin`), restore, `check` and `forget --prune` as schedule actions | `BackupTargetType.Restic`, new `ResticBackupStorage`, `ScheduleAction.ResticMaintenance` | L | Restic binary in the image; repository password handling; long-running operations |
| 12 | Trusted proxies configurable (today `X-Forwarded-*` is accepted from anyone, which weakens the API rate limit) | `Program.cs` `ForwardedHeadersOptions` + `MatDock__KnownProxies` | S | Wrong setting breaks HTTPS detection behind the proxy |
| 13 | Adopt of a discovered stack reads its compose through a directory from container labels: restrict to the compose working dir | `StackCommands.ReadProjectFile` | S | – |
| 14 | README screenshots show the UI before this review | `docs/images` (skill *screenshots*) | S | – |

## 4. Implemented in this round (besides the fixes above)

1. Alerts: *Container died* (error exit or out of memory, not a stop) and *Container unhealthy* from `docker events`,
   once a minute and only while a schedule waits for them; schedule action *Send a notification* (the event is the
   message).
2. Image updates: schedule action *Update stacks* (check and notify, or pull and redeploy with an optional backup
   first); the Stacks list marks stacks with newer images.
3. Logs of a whole stack and timestamps.
4. Self-refreshing dashboard, environments, containers and stack pages.
5. Image detail with layers; prune all unused images.
6. File browser inside containers.
7. Compose check in the stack editor.
8. Activity log (System → Activity), UI actions and API calls, 90 days.
9. Network map per environment.
10. SSH host key verification; manual *Back up now* for a volume.
11. The terminal into a container on the local Docker socket (the Mattiverse setup had none), with bash when present.

Tests: 410 before, 488 after (Windows and Linux, `mcr.microsoft.com/dotnet/sdk:10.0`); build without warnings.
Two migrations: `AddActivityLog`, `AddEnvironmentHostKey`.
