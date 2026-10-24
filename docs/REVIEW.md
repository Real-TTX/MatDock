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
| restic backups (backup, restore, check, prune) | ✅ | A backup target kind: every backup file is a snapshot (S3, B2, Azure, GCS, REST server, path); retention forgets, the schedule *Maintain restic repositories* prunes and checks |
| Alerts by e-mail/webhook on container events, health, updates | ✅ | *Container died*, *Container unhealthy*, updates available, vulnerable images, offline/deploy/backup/sync; mail through MatSMTP when Mattiverse sets it |
| Local users, roles | ✅ | Two roles (Admin, User), enforced; a user can be limited to some environments (section 8) |
| OIDC | ✅ | Through MatIdentity; any other OIDC provider is not wanted — MatIdentity is the family's identity |
| 2FA | — | Not in MatDock: MatIdentity does it for every Mat app; the local sign-in is the fallback (throttled) |
| Activity/audit log across environments | ✅ | System → Activity |

## 3. Plan for what is still missing

| # | What | Where | Effort | Risks |
|---|---|---|---|---|
| 1 | ~~restic as a backup target kind~~ – done, see section 7 | `ResticBackupStorage`, `ScheduleAction.ResticMaintenance` | L | – |
| 2 | ~~Move the UI onto MatUi~~ – done, see section 5 | all pages | L | – |
| 3 | ~~Update history and pinning~~ – done, see section 8 | `StackUpdateRun`, `Stack.PinnedServices`, `TagVersion` | M | – |
| 4 | ~~Per-environment rights~~ – done, see section 8 | `User.RestrictEnvironments`, `UserEnvironment`, `EnvironmentService` | M | – |
| 5 | The CIFS password of a new volume is still on the `docker volume create` command line (Docker takes it only as an `--opt`; it is in the volume's options anyway) | – | – | Not fixable in MatDock; documented |
| 6 | ~~Ad-hoc container `run` from a form~~ – done, see section 8 | `ContainerRunCommands`, `Containers/Create` | M | – |

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

## 6. Against Portainer

The second benchmark: Portainer, the best-known Docker UI. MatDock aims at the same single-host, agentless,
compose-first use (homelab or small business), not at Portainer's orchestration/enterprise breadth.

✅ has it · ◐ partly · ✖ missing · — not wanted (why)

| Portainer | MatDock | |
|---|---|---|
| Hosts over socket, SSH, Docker API (TCP/TLS) | ✅ | agentless SSH is the point; host keys verified |
| Edge agents / an agent per host | — | not wanted — MatDock needs nothing on the hosts |
| Kubernetes management | — | not wanted — MatDock is the Docker engine |
| Swarm services, scaling, secrets, configs | — | not wanted — single engine, compose |
| Containers: list, status, logs, exec, stats, inspect | ✅ | logs with search/timestamps, terminal, file browser, 24 h resources |
| Container pause/unpause and kill | ✅ | next to start/stop/restart, by state |
| Container create/run from a form | ✅ | *New container* (image, name, ports, env, volumes, restart policy, network); a one-off, not managed: a stack is the way for anything that should stay |
| Compose stacks, editor, env, git deploy, webhooks | ✅ | beats Portainer: highlighted YAML + `.env`, `compose config` check, SSH git |
| App templates / one-click apps | ✅ | the Store, plus *Adopt* for a discovered stack |
| Images: list, prune, layers | ✅ | + update tracking, rollback, Trivy CVE scan |
| Image pull | ✅ | *Pull* onto the selected environment |
| Image build / import-export | ✖ | build belongs in CI (git-deployed stacks build on the host); not planned |
| Volumes: list, create, remove, browse, backup | ✅ | browser + migration + backup — beyond Portainer |
| Networks: list, detail, remove, prune, map | ✅ | + a network map per environment |
| Network create, connect/disconnect a container | ✅ | create (bridge/macvlan/ipvlan, internal); connect/disconnect on the detail page |
| Registries: credentials + browse | ✅ | |
| Users/roles | ◐ | Admin/User, enforced; teams and per-object RBAC not wanted (homelab/small team) |
| Per-environment rights | ✅ | a user is limited to chosen environments, enforced in one place (section 8) |
| Auth: internal, LDAP, OAuth/OIDC | ◐ | local + MatIdentity OIDC; generic LDAP/OIDC not wanted |
| REST API / automation | ✅ | `/api/v1` (stacks, backups, operations), `X-Api-Key` |
| Dashboard, events, alerts, audit log | ✅ | |

Shipped this round: MatUi 0.2.0 → 0.3.2 (toolkit copy, no app changes); container pause/unpause/kill; image pull;
network create and connect/disconnect. Still open to match Portainer where it is wanted: the ad-hoc container `run`
form (section 3), everything else above is ✅ or deliberately —.

## 7. restic as a backup target

A target kind next to *Local* and *SMB*: `BackupTargetType.Restic`, a repository (S3, B2, Azure, GCS, Swift, a REST server or a path inside the
MatDock container), its password and the backend's access keys. restic encrypts and deduplicates, so repeated backups of the same volume cost
little space and a lost NAS or a stolen bucket exposes nothing.

**How it fits.** The storage seam of the backups is file based (`OpenWrite/OpenRead/Delete/List/Test`), so `ResticBackupStorage` maps a backup
file to a snapshot: writing pipes the volume's tar stream into `restic backup --stdin --stdin-filename <name>` (snapshot tagged
`mdfile:<name>`, host `matdock`), reading is `restic dump --tag … latest /<name>`, listing is `restic snapshots --json`, deleting forgets the
snapshot. Volume backups, stack bundles, the Targets browser, restore, retention and downloads work unchanged. Snapshots made by anything else
in the same repository (another host or no `mdfile:` tag) are never listed, read or forgotten.

**Safety.**
- restic is started without a shell (an argument list) with an empty environment plus the repository, its password and the access keys:
  nothing on the command line (where `ps` shows it), none of MatDock's own variables.
- Only known backends are accepted (`s3:`, `b2:`, `rest:`, `azure:`, `gs:`, `swift:`, an absolute path without `..`). `rclone:` (starts a
  program) and `sftp:` (needs an SSH key inside MatDock) are refused. Of the environment only the backends' key variables pass (`AWS_*`, `B2_*`,
  `AZURE_*`, `GOOGLE_*`, `OS_*`, `ST_*`, `RESTIC_REST_USERNAME/PASSWORD`): `RESTIC_PASSWORD_COMMAND` would run a program.
- Password and keys are encrypted at rest and never rendered back (blank on edit keeps them); the repository is shown without `user:pass@`.
  Error messages never repeat a line of the keys.
- The type of a target cannot change (its backups were written the old way). A write that breaks half way kills restic instead of saving a
  snapshot of half a tar; a read that ends with a restic error fails instead of looking complete.
- The *Test connection* button creates an empty repository (a new bucket or folder) and says "Wrong repository password" for exit code 12.

**Maintenance.** Deleting a backup (or retention) only *forgets* the snapshot: `prune` is slow on a remote repository and would run after every
step. The schedule action *Maintain restic repositories (prune + check)* frees the space and verifies the repository; one target or all.

**Image.** restic 0.18.0 is copied from the pinned official image into MatDock's (`COPY --from=restic/restic:0.18.0`): a static Go binary, so it
runs on the Debian/Ubuntu runtime; `MatDock:ResticPath` overrides the program.

**Tried for real.** The six round-trip tests run against the real binary when `MATDOCK_TEST_RESTIC` names one (Linux, `mcr.microsoft.com/dotnet/sdk:10.0`
with the binary from the restic image: 72 restic-related tests pass, none skipped); otherwise they are skipped. In the built image with the Docker
socket: a stack with a volume was backed up as a bundle to a restic target (two snapshots in the repository), a file that only the backup
contained was deleted and the restore brought it back, deleting a backup forgot exactly its two snapshots, and the maintenance schedule pruned
the repository (1112 → 1084 KiB). (On this machine Docker Desktop had no free address pool left, so the test stack runs with `network_mode: none`.)

## 8. The rest of the plan

**Container run.** *New container* on the Containers page (admin): image, name, published ports, environment, volumes, restart policy, network.
`ContainerRunCommands` validates each value on its own (image reference, name, `host:container[/proto]` with 1–65535, `KEY=value`, a volume
name or absolute path mapped to an absolute path, `:ro`) and single-quotes each one; an env value with a quote cannot break out (tested). The
container is not managed (no stack record): a stack remains the way for anything that should stay. Admin only, since a mount of `/` is root on
the host. Also pause/unpause, kill, image pull, network create and connect/disconnect (the earlier Portainer round).

**Update history and pinning.**
- *History:* every update run (manual, schedule, API) is a `StackUpdateRun`: when, who started it, the result (updated, no change, failed, rolled
  back), the services whose image changed (image id before → after) and the end of the compose output. The newest 50 per stack are kept; a failing
  write of the history never fails an update. The Update page shows the last 10.
- *Pinning:* `Stack.PinnedServices`. A pinned service is left out of the `compose pull` (a manual update, a schedule, the API), the check shows it
  as pinned and never as an update. The pull step takes the services from `compose config --services` and leaves the pinned out (all pinned =
  no pull); the shell fragment was run in a real `sh` for four cases, among them that a pin `we` does not match the service `web`.
- *Newer version:* for a plain version tag (`1.2.3`, `v1.2`, `2024.10.1`) a hint when the registry has a newer tag written the same way. Only
  information: tags with a suffix (`16-alpine`, `1.2.3-rc1`) and `latest` get none, since their order would be guessed.

**Rights per environment.** A user of the role *User* can be limited to chosen environments (*Users → Edit → Only these environments*); without it
they see all, as before (existing users are unchanged). Administrators are never limited (the flag is cleared for them).
- *One place:* `EnvironmentService.GetAllAsync/GetEnabledAsync/GetAsync` filter by the current user, so the pages, the environment switcher, a URL
  with `envId=…`, the forged cookie of the selector and the log WebSocket all follow without each page knowing. System work (the monitors, the
  schedule runner) and the API with its key have no current user and see everything. An unknown user sees nothing (fail closed).
- *The two places that did not go through it:* the Launchpad (stacks of every environment) and the backup figures on the dashboard now drop what is
  not theirs.
- *Tested at the page level* with a real sign-in as a restricted user (every overview, a parameter, nine detail pages by id, the forged cookie, a
  user without environments, the user editor). Run with the restriction switched off on purpose, 20 of those tests fail: they do test it.
- Not in the scope: rights *per object* (a single stack or volume) and teams; the user list shows the limit (*All*, *n selected*, *None*).

**Tried against real Docker (and what that found).** The built image with the Docker socket, driven through the forms: *Pull* (`hello-world`, and a
reference with `;touch …` which ran nothing), *New container* (Docker showed the restart policy, the port bound to `127.0.0.1` only, an environment
value with a space in one piece, the volume, HTTP 200 on the published port), then Pause (paused, the port stopped answering), Unpause (running
again) and Kill (exit code 137).

This found a bug that was already there: **every Start/Stop/Restart button of the Containers page was answered with "Invalid action."** The page
bound `View` as a non-nullable string; the action forms post no view, which made the model state invalid, and the handler refused. It is fixed
(`View` may be absent), and `ContainerActionTests` post exactly what the page renders and check the docker command that reaches the host, the
buttons per state (running: pause/restart/stop/kill, paused: unpause/stop, stopped: start), an unknown action and an id with shell characters.
With the old declaration put back, 7 of those tests fail.

Networks were tried afterwards. Docker Desktop on this machine had run out of address pools ("all predefined address pools have been fully
subnetted": 56 networks, 22 of them unused leftovers of stopped projects); `docker network prune` freed them. Then, through the forms against real
Docker: *New network* (bridge, with its subnet; and `internal`, which Docker reports as internal), connect a container on the detail page (Docker
shows it on both networks), the delete button disabled while it is connected and a forced delete refused with Docker's own reason ("has active
endpoints"), disconnect, and delete once it is free.

That found a second defect, in the volume form as well as the new network form: when Docker refused (a name that already exists), the form came
back **without a word**. The message went into a TempData that the form never renders. Both forms now show Docker's reason in the form
(`CreateFormTests`; with the old code put back, the two refusal tests fail).

## 9. Audit: what a complete Docker manager still needs (October 2026)

Against Portainer, Dockhand, Komodo and Dockge, from the code (not the docs). Not wanted stays not wanted (Swarm, Kubernetes, agents, teams or
rights per object, LDAP/generic OIDC, 2FA in MatDock). Image build stays open until it is decided.

| # | What | Status |
|---|---|---|
| 1 | Remove a container (with its anonymous volumes, forced), remove all stopped ones, rename | done |
| 2 | Container detail: environment (secrets masked), labels, networks with IPs, command, restart policy and count, health with its log, times, processes, inspect | done |
| 3 | Security headers: CSP, frame-ancestors / X-Frame-Options, nosniff, Referrer-Policy, HSTS over HTTPS | done |
| 4 | Secrets at rest: the `.env` of a stack encrypted in the database and in bundle backups; the key ring protected by a certificate when one is given | done |
| 5 | System view per environment: `docker info`, `docker version`, `system df`, build cache prune, a system cleanup with a preview | done |
| 6 | Container form: command, entrypoint, labels, limits, hostname, user, working directory, capabilities, privileged, devices, GPUs, logging, DNS, extra hosts, health check; `docker update`; duplicate and recreate | planned |
| 7 | Networks: IPAM on create (subnet, gateway, range, IPv6), a fixed IP and aliases on connect | planned |
| 8 | Images: used by, tag, push, save and load | planned |
| 9 | Stacks: per service restart/stop/start/recreate, scale, a redeploy webhook per stack, copy to another environment | planned |
| 10 | Alerts: Discord, Slack, Teams, Telegram, ntfy, Gotify; thresholds for disk, memory and CPU per host, restart loops | planned |
| 11 | Metrics: kept in the database (30 days), per container, a live view in the container detail, `/metrics` for Prometheus | planned |
| 12 | API: containers, images, volumes, networks, environments, update rules; OpenAPI; several named keys (read or full) | planned |
| 13 | Backup of MatDock itself (database and keys, encrypted with a passphrase), download and restore, as a schedule | planned |
| 14 | A read-only role (Viewer) | planned |
| 15 | An events page per environment with filters | planned |
| 16 | Selecting several items of a list for one action (containers, images, volumes, networks) | planned |
| 17 | A search of the whole app in the header bar | planned |
