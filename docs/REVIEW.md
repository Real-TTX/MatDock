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
dynamic inline styles (gauge widths). MatDock has its own stylesheet, not MatUi (see plan item 8).

## 2. Against Dockhand

✅ has it · ◐ partly · ✖ missing · — not wanted (why)

| Dockhand | MatDock | |
|---|---|---|
| Containers start/stop/restart, status, resources, ports | ✅ | The lists refresh themselves every 20–30 s |
| Terminal in the browser (exec) | ✅ | bash when the container has it; also on the local socket and behind a Docker API |
| Log streaming with search and filter | ✅ | Plus a whole stack at once and timestamps |
| File browser in containers (browse, upload, download, edit) | ✅ | `docker exec`; containers without a shell through `docker cp` (no delete/rename there) |
| Compose stacks, YAML editor, environment variables | ✅ | Highlighted YAML and `.env` side by side, a check on save and *Check* by compose on the host; a visual editor is not wanted (the compose file is the source Mattiverse writes too) |
| Deploy from Git, webhooks, auto-sync | ✅ | HTTPS with a token or SSH with a key (host key saved at the first clone) |
| Images: tags, sizes, cleanup | ✅ | Prune dangling or all unused |
| Image update tracking, automatic updates | ✅ | Badge in the Stacks list, schedule *Update stacks*, health gate with rollback to the old images |
| Layer inspection | ✅ | Image detail page |
| CVE scanning (Grype/Trivy) | ✅ | Trivy on the image's host, results per image and in the list, schedule *Scan images* |
| Volume browser | ✅ | |
| Network graph | ✅ | Network map per environment |
| Hosts over socket and SSH | ✅ | Agentless SSH is MatDock's point; host keys verified |
| Hosts over TCP/TLS | ✅ | Docker API with TLS client certificates (or plain TCP, with a warning) |
| Hosts with an agent | — | Not wanted: MatDock exists to need nothing on the hosts |
| Per-host dashboard: live CPU, memory, disk | ✅ | Refreshes itself, a line of the last 24 h; CPU is load per core |
| Cron schedules: prune, updates, cleanup, backups | ✅ | |
| restic backups (backup, restore, check, prune) | ✖ | Tar archives to local/SMB with retention and restore exist; planned (below) |
| Alerts by e-mail/webhook on container events, health, updates | ✅ | *Container died*, *Container unhealthy*, updates available, vulnerable images, offline/deploy/backup/sync; mail through MatSMTP when Mattiverse sets it |
| Local users, roles | ◐ | Two fixed roles (Admin, User), enforced; per-environment rights not planned (homelab/small team) |
| OIDC | ✅ | Through MatIdentity; any other OIDC provider is not wanted — MatIdentity is the family's identity |
| 2FA | — | Not in MatDock: MatIdentity does it for every Mat app; the local sign-in is the fallback (throttled) |
| Activity/audit log across environments | ✅ | System → Activity |

## 3. Plan for what is still missing

| # | What | Where | Effort | Risks |
|---|---|---|---|---|
| 1 | **restic** as a backup target kind (encrypted, deduplicated, S3/B2/SFTP): backup = `restic backup --stdin` of the volume tar stream, restore, `check` and `forget --prune` as schedule actions | `BackupTargetType.Restic`, new `ResticBackupStorage` beside `SmbBackupStorage`, `ScheduleAction.ResticMaintenance` | L | The restic binary in the image (or a restic container on the host); repository password handling; long operations |
| 2 | **Move the UI onto MatUi** – see section 5 | all pages | L | see there |
| 3 | Update history and pinning: keep the update runs (old/new digest, result, rollback) and let a service be pinned to a digest so automatic updates leave it alone; newer version tags (semver) next to moving tags | `UpdateService`, a table `StackUpdateRun`, Stacks/Update | M | Semver of tags is guesswork for many images (`latest`, `16-alpine`) |
| 4 | Per-environment rights (a user sees and controls only some environments) | `User` ↔ `DockerEnvironment`, `EnvSelection`, every list | M | Every page and the API filter by it; only worth it beyond a small team |
| 5 | The CIFS password of a new volume is still on the `docker volume create` command line (Docker takes it only as an `--opt`; it is in the volume's options anyway) | – | – | Not fixable in MatDock; documented |

## 4. Implemented

**First round:** alerts (*Container died*, *Container unhealthy*, *Send a notification*), the schedule *Update stacks*, logs of a whole
stack with timestamps, self-refreshing pages, the image detail with layers and *prune all unused*, the file browser inside containers,
the compose check, the activity log, the network map, SSH host key verification, *Back up now* for a volume, the terminal on the local
socket.

**Second round:**

1. Mail through MatSMTP (MatPlatform 1.3.0): with `MATSMTP_*` every mail goes through it, the server settings are fixed on the page and
   in `/api/configure`; mail now goes out with MailKit (System.Net.Mail has no implicit TLS).
2. The stack `.env` goes to the host over stdin, never on the command line.
3. *Check* in the stack editor (`docker compose config` on the host in a temporary folder); compose and `.env` side by side.
4. `MatDock__TrustedProxies` for the `X-Forwarded-*` headers.
5. Adopt reads only a `.yml/.yaml` in a project folder.
6. CPU and RAM of the last 24 h per environment on the dashboard.
7. Updates with a health gate and rollback (the old image keeps a tag of its own during the update, since the containerd image store
   drops an image without a name); fixed on the way: *Update now* never worked (the form lost the stack id).
8. The container file browser without a shell, through `docker cp`.
9. Git over SSH with a key (LibGit2Sharp's ssh transport replaced by one on SSH.NET).
10. Environments over a Docker API (TCP with TLS client certificates).
11. Vulnerability scans with Trivy (detail page, list column, schedule *Scan images*).

Everything was tried against real Docker on this PC (local registry for the rollback, a scratch container, an OpenSSH + git server,
docker:dind with TLS, Trivy) besides the tests. Tests: 410 at the start, 542 now (Windows and Linux, `mcr.microsoft.com/dotnet/sdk:10.0`);
build without warnings. Migrations: `AddActivityLog`, `AddEnvironmentHostKey`, `AddGitSshKey`, `AddEnvironmentDockerApi`, `AddImageScan`.

## 5. Moving the UI onto MatUi

**Assessment.** MatDock does not use MatUi: it has its own stylesheet (`matdock.css`, about 850 lines) and script (`matdock.js`). Its class
names grew from the same roots – 16 of its 28 `mat-*` classes exist in MatUi (`mat-btn`, `mat-field`, `mat-input`, `mat-select`, `mat-tabs`,
`mat-badge`, `mat-switch` …) – but they are defined differently, and both stylesheets are global. Loading `matui.css` therefore changes every page
at once, and the shell (menu, top bar, theme button, environment switch) is one layout for all pages. A page-by-page move behind a switch
would mean two design systems side by side on the same class names; that is not safe, so the move was **not started**. It is a step of its
own, done on a branch and released as a whole:

| Step | What | Where |
|---|---|---|
| 1 | Copy MatUi (`matui.css`, `matui.js`, `matui.boot.js`, `matui-icons.svg`; CODE-03), `data-app="matdock"` on `<html>`. The registry has MatDock's accent as `#1d78be`; MatDock still uses Docker blue `#2496ed` – decide which one stays (or record it as `FoundAccent`) | `wwwroot/`, `_Layout.cshtml`, MatDesign `AppRegistry.cs` |
| 2 | The shell: `mat-nav` with groups (Overview: Dashboard, Environments, Apps · Docker: Stacks, Containers, Images, Volumes, Networks · Operations: Sync, Schedules, Backups · System), the user menu at the foot with *Sign out* last, the three-state theme button (`data-theme-save-url` when it is kept per user), a breadcrumb and the page head; the environment switch becomes a context choice in the switcher (SHELL-15) | `_Layout.cshtml`, `Support/EnvSelection.cs` |
| 3 | The list and form helpers render MatUi markup: `<mat-table>` → `mat-toolbar` (search field, Filter and Sort as dialogs, chips for what is in effect) + `mat-table` + `mat-pager`; `<mat-field>` → MatUi field with hint and error; `<mat-form-buttons>` → `mat-btn-row` | `Controls/*TagHelper.cs` – most pages follow without edits |
| 4 | Own components to MatUi: `card` → `mat-card`, `alert` → `mat-alert`, `pill` → `mat-pill`/`mat-tag`, `stat` → `mat-stat`, `empty-state` → `mat-empty`, the CPU/RAM gauges → `mat-meter` with `data-value` (removes the last inline `style` widths), `data-label` on every table cell (TABLE-02) | all pages |
| 5 | Dialogs: `data-confirm` and the prompts of the file browser use the toolkit's dialogs (`MatUi.confirm`/`prompt`); the confirm/prompt code of `matdock.js` goes | `matdock.js`, Volumes/Files |
| 6 | What only MatDock has keeps its own classes with an app prefix: log viewer, terminal, network map, file browser, code editor, host lines | `matdock.css` (shrinks to these) |
| 7 | Delete the rest of `matdock.css`: its colour variables (`--mat-*` tokens instead), the gradient buttons, lifting cards, upper-case labels (COLOR-06) | `matdock.css` |
| 8 | Check: MatDesign *Identity → Audit*, `node tools/matui-check.js`, screenshots light/dark/390 px of every page, `PageMarkupTests` extended (no `style=` attributes, `data-label` in tables) | tests, `docs/images` |

**Effort** L (several days); **risk** visual regressions on every page – nothing changes for `/api/v1`, `/api` or the data. Steps 1–3 carry
most of the gain (the shell and the list/edit patterns of the whole family); 4–7 are mechanical after that.
