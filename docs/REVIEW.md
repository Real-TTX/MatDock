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

Left as it is, on purpose: the Proxy page adds a route with a small form above the list (a quick action, not CRUD).
The dynamic inline styles (gauge widths) and the own stylesheet are gone since the move to MatUi (section 5).

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
| 2 | ~~Move the UI onto MatUi~~ – done, see section 5 | all pages | L | – |
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
12. Found on the way: the result of the connection test was never saved since the list lost its test button (environments stayed
    *Unknown*, the detected sudo/rootless access was lost) – saving and the health check save it again; stack bundles at a target were
    all shown as *Foreign*.
13. README screenshots of a demo installation (invented hosts and stacks).

Everything was tried against real Docker on this PC (local registry for the rollback, a scratch container, an OpenSSH + git server,
docker:dind with TLS, Trivy) besides the tests. Tests: 410 at the start, 543 now (Windows and Linux, `mcr.microsoft.com/dotnet/sdk:10.0`);
build without warnings. Migrations: `AddActivityLog`, `AddEnvironmentHostKey`, `AddGitSshKey`, `AddEnvironmentDockerApi`, `AddImageScan`.

## 5. The UI on MatUi

**Done** (branch `feature/review`). MatDock now runs on the MatUi toolkit like the other engines (Mattiverse, MatIdentity, MatSMTP, Matcad, Matddns):

| Step | What | Where |
|---|---|---|
| 1 | MatUi 0.2.0 copied from MatDesign (`matui.css`, `matui.js`, `matui.boot.js`, `matui-icons.svg`; CODE-03); `scripts/update-matui.ps1` fetches a newer one. `data-app="matdock"` gives the registry's accent `#1d78be` (also the theme colour and the manifest); Docker blue is gone | `wwwroot/matui`, `Support/Toolkit.cs`, `Support/Ui.cs` |
| 2 | The shell: `mat-nav` with groups (Dashboard, Environments, Apps · Docker · Operations · Administration), the environment as a switcher menu, the user menu with *Profile* and *Sign out* last, the three-state theme button, breadcrumb and top-bar actions; the layout renders the page heading (`Title`, `Heading`, `Sub`, `Crumbs`). Sign-in, setup and error pages are `mat-auth` cards (`_Bare`) | `_Layout`, `_Head`, `_Bare` |
| 3 | The helpers render MatUi markup: `<mat-table>` → `mat-list` with `mat-toolbar`, `mat-table-wrap`, `mat-pager`, `mat-actions`; `<mat-field>` → field with `mat-error`; `<mat-form-buttons>` → `mat-btn-row`; tabs, pills, the validation summary | `Controls/`, `_ValidationSummary` |
| 4 | Every page on MatUi components: cards, alerts, pills/tags, stats, empty states, `mat-kv`, meters with `data-value` instead of inline widths, `data-label` on every table cell, toasts for “done”; the inline scripts moved to files (`stack-forms.js`) | all pages |
| 5 | Dialogs from the toolkit: `data-confirm` and `MatUi.prompt` (file explorer); dependent fields with `data-show-if`; MatDock's own theme, confirm and dependent-field code is gone from `matdock.js` | `matdock.js` |
| 6–7 | `matdock.css` went from 843 lines of its own design to ~235 lines of what only MatDock has (environment tiles, usage meters, stack/container tiles, log viewer, terminal, code editor, file explorer, network map), all `md-` prefixed and with the toolkit's tokens only | `matdock.css` |
| 8 | `UiTests` (like Matddns): every page renders without inline style, script or handlers, icons only from the sprite (inlined once), `data-label` on table cells, the accent equals MatDesign's registry and the manifest, `matdock.css` has no colours or variables of its own. Screenshots light/dark/390 px checked; README screenshots refreshed | `tests/…/UiTests.cs`, `docs/images` |

Behaviour notes: “done” messages are toasts now; hidden dependent fields are not posted (a sync job keeps its cron when its schedule is switched
off); the offline page uses the toolkit too. `/api/v1` and `/api` are unchanged.
