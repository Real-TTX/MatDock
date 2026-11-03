<div align="center">

<img src="src/MatDock.Web/wwwroot/img/logo.svg" width="96" alt="MatDock" />

# MatDock

**A Docker manager for your servers – in the browser.**

Environments over SSH or the local socket, stacks, containers, volumes, live logs, a web
terminal and real backups. One container, no cloud, no agents on the hosts.

</div>

![The dashboard: environments, stacks, updates and running containers at the top, the apps with their status below](docs/images/dashboard.png)

---

## What this is about

Portainer and the like are great – but running many small servers means one agent per host,
another UI per box, and backups that are somebody else's problem. MatDock connects to your
hosts over plain **SSH** (or the **local Docker socket**) – nothing to install on the target –
and puts a single dashboard on top of all of them: stacks, containers, volumes, images and
live logs across every environment, with a global switcher to look at one host or **all at
once**. On top of that it does what most managers leave out: **volume migration between hosts**
and **scheduled, restorable backups** – single volumes or whole stacks.

## At a glance

**Environments**
- Connect over **SSH** (password or key), the **local Docker socket**, or a **Docker API over TCP** with TLS client
  certificates – no agent on the host
- **Host stats** per environment: CPU load, RAM and disk, running containers, Docker version
- A **global environment selector** in the sidebar – one host or **"All environments"**, every
  page follows it
- Connection test with a clear result; secrets stored **encrypted**; the **SSH host key** is saved at the first
  connection and a server that shows another one is refused (forget it on the environment after a reinstall)
- A **system view** per environment: what `docker info` and `docker version` say, the disk usage per kind, a **cleanup**
  (stopped containers, images, networks, build cache – never volumes) and **usage charts** of the host (6 h to 30 days)
- An **events page** (`docker events` of one or all environments): window, type, only problems, a container, search

**Dashboard**
- Environment tiles with **live CPU / RAM / disk** usage (the dashboard, environments, containers and stack pages
  refresh themselves every 20–30 s while open) and a line of the last 24 h, **top containers** by load, a feed of
  recent **container events** (created / started / stopped) and the **backup status**

**Stacks & containers**
- **Managed and discovered** Compose stacks side by side – list or tiles, **expandable to the
  container cards** with per-service actions
- Add a stack from the **Compose editor**, from **Git** (HTTPS with a token, or SSH with a key; the Git server's host key
  is saved at the first clone), or from an **App template**
- Deploy / redeploy / stop, aggregated **resources** per stack; per **service** restart, stop, start, recreate and **scale**;
  a **redeploy webhook** per stack (for a CI); **copy** a stack to another environment
- **Deploy options** kept with the stack: pull first, **build** (with the newest base images, without the cache – BuildKit is in the
  image), re-create every container, remove orphans. A deploy runs in the background with a **live console** (search, follow, wrap,
  copy, download), and every deploy goes into the stack's **deploy history**: who or what started it (a person, the webhook, a Git sync,
  a schedule, an update, a rollback, a restore, the API, MCP), the options, the commit, how long it took, what compose did and its log
- **Secrets** per stack: encrypted in the database, shown by name only, never written to the host – a deploy, a restart or a recreated
  service hands them to compose as environment variables (in bundle backups and copies too)
- A **live check** of the compose file while you type: what compose will refuse (host ports twice, unknown volumes and networks,
  `depends_on`), what is risky (a port another stack already publishes, a database on every interface, a password in the file, the
  Docker socket, privileged, a misspelt key, a variable without a value) and what is better practice – with one-click fixes
- **Git stacks**: an env file of the repository besides its `.env`, variables that override it, **auto-sync** on a schedule that deploys
  only when the compose file's folder changed, and the **push webhook** (GitHub, Gitea, Forgejo signatures, GitLab token) that answers
  `skipped` when there is nothing new
- **Import** compose projects from a folder of the host (several at once); taken-over stacks keep their relative paths (`./data`) and
  their `.env` pointing to the original folder, so the data stays where it is
- **Tags** (a catalog with colours, from labels `matdock.tags` too) to filter the stacks, and an **icon** per stack: a toolkit icon,
  the logo of a known app (selfh.st, fetched by the server) or your own image
- A **compose file from a running container** – to copy or to keep as a stack (its secrets become variables)
- Container list with **short status** (hover for detail), **ports as clickable tags** and the **public addresses** of Traefik and
  caddy-docker-proxy labels (or `matdock.url`), combined
  **CPU/RAM**, and actions: **open, shell, logs, volumes, start/stop/restart**; tick several for **start, stop, restart or
  remove at once** (images, volumes and networks too)
- A **container detail** page: environment (secrets masked), networks with their addresses, mounts, health with its log,
  processes, the raw inspect, **usage charts**; rename, remove, change memory/CPU/restart (`docker update`), **duplicate**
  and **recreate** with the newest image
- **New container** from a full form: command, entrypoint, labels, limits, hostname, user, capabilities, devices, GPUs,
  logging, DNS, extra hosts, health check, a fixed IP and aliases
- A **file browser inside a running container** (the same as for volumes: browse, edit, upload, download, create,
  rename, duplicate, delete; it runs `docker exec`; a container without a shell (distroless, scratch) is browsed, read and
  written through `docker cp`)

**Images**
- List across environments with size, age and whether a container uses it; a **detail page** with tags, digest,
  platform, entrypoint, ports, labels and the **layers** (`docker image history`); prune dangling or **all unused** images
- Which containers use an image; **tag**, **push** (signed in to its registry), **export** as a `.tar.gz` and **import** one
  (for a host without a registry)
- **Vulnerability scan** with Trivy, run as a container on the image's own host (its database is kept in the volume
  `matdock-trivy-cache` there): findings per image on the detail page, critical/high in the list, and the schedule action
  *Scan images* that scans the images of the running containers and notifies about critical and high findings; a scan downloads
  as CSV, JSON or SARIF

**Networks**
- List, details with the attached containers, prune; a **network map** per environment: the networks and the
  containers on them, so you see which services can reach each other
- Create with **IPAM** (subnet, gateway, range, IPv6); connect a container with a **fixed IP** and **aliases**

**Volumes**
- List across environments, **migrate a volume from one host to another** (tar over SSH)
- **File explorer** in the volume: browse, **edit text files**, image **preview**, **upload**,
  **download**, create folder/file, rename, duplicate, delete
- Create volumes, backup and restore

**Backups**
- **Scheduled jobs** (cron) with retention, plus a full **history**
- **Targets**: the local data volume, a **NAS via SMB**, a **WebDAV folder** (Nextcloud, ownCloud, a NAS; streamed, the folder is made when missing) or a **restic repository** (encrypted, deduplicated, on S3/B2/Azure/GCS, a REST server or a path) – browse the archives actually present
  at a target and **restore even backups written by another/older MatDock**
- **Full stack bundles**: all volumes **+ the compose definition + image references** in one
  backup, restored as a running stack (images are pulled again on restore)

**Operations**
- **Live log viewer** (streaming `docker logs -f`): pause, follow, **live filter**, **search**, timestamps – for one
  container or a **whole stack** at once
- **Web terminal** (xterm.js) – a shell on an SSH host or straight into a container (bash when it has one, else sh;
  on the local Docker socket too)
- **Apps**: reusable Compose blueprints with icons, installed as a stack in one click
- Users & roles, notifications (**SMTP, webhook, Discord, Slack, Teams, Mattermost, Telegram, Signal, ntfy, Gotify, Pushover,
  Bark, Apprise**), Git credentials, tags, API keys –
  all under **System**
- An **activity log** (System → Activity): every action of a user and every changing call of the API, with user,
  target, environment and result, searchable, kept for 90 days
- A **search** in the header bar (`/` or `Ctrl+K`): containers, stacks, images, volumes, networks, environments and pages
- **Metrics**: hosts and containers every 5 minutes in the database (30 days, hourly after two days), `/metrics` for Prometheus
- **MCP server** at `/mcp`: an AI client (Claude Code, Claude Desktop) looks at and runs the hosts with an API key; a read key gets only the tools that change nothing (see [MCP](#mcp-matdock-for-an-ai-client))
- **Backup of MatDock itself** (System → MatDock backup): the database and the keys in one file encrypted with a passphrase
  (AES-256-GCM), as a download or a schedule; a restore takes its place at the next start and keeps what it replaced

**Schedules and alerts**
- Cron schedules for prune (volumes, images, networks), status reports, health alerts, volume and stack backups, Git sync and
  **starting and stopping stacks** (a test stack off at night)
- **Image updates**: the schedule *Update stacks* checks the registries for newer images of the tags in use and notifies,
  or pulls and redeploys (optionally after a stack backup); with the health gate the containers must be up and healthy
  within 90 s, otherwise the images from before the update get their tags back and the stack is deployed again; the
  Stacks list marks stacks with updates
- **Update rules**: MatDock keeps its own version per service and ignores the tag of the compose file after the start (the file
  stays as it is; an image override runs another image). A rule per scope (default, environment, stack) sets the mode (off, notify,
  automatic), the strategy (the same tag with a new digest, the newest minor or latest version of the series, or pinned; per service
  too), a time window, how often to look, a backup before, rollback and notifications. A new version is announced once; in automatic
  mode it is applied within the window and a failed update is not tried again. **Updates** lists all stacks with Check all and Update all.
  An automatic update can wait a **minimum age** of a new image and **hold back a new image with vulnerabilities** (scanned with Trivy
  before the pull); labels `matdock.tag.include` / `.exclude` (also `dockhand.*`, `wud.*`) narrow the version tags, and the update page
  links the **release notes** (`org.opencontainers.image.source`)
- **Alerts**: event schedules for *container died* (an error exit or out of memory, not a stop), *container unhealthy*,
  a **restart loop** (n starts in m minutes), a **host over a limit** (CPU, memory, disk; global or per environment, said once
  when crossed and once when back), environment offline and failed deploys, backups and syncs – with *Send a notification*
  the event itself is the message; switched on per event under System → Notifications
- Three roles: **Viewer** sees what a user sees and changes nothing; **User** also starts/stops/restarts containers;
  **Admin** does the rest (environments, stacks, volumes, files, backups, schedules, terminal, settings). The sign-in is
  locked for 15 minutes after 5 wrong passwords for one account (or 20 from one address)
- **Light and dark**, works well on **mobile**, no cloud and no third-party services

## Screenshots

All screenshots show a demo installation with invented hosts and stacks.

### Stacks – expandable to the containers, with aggregated resources

![Stacks of two environments; the gitea row is expanded to its db and gitea containers, the staging stack is stopped](docs/images/stacks.png)

Managed and discovered Compose projects of every environment in one list. A row expands to show its containers as
cards – each with status, ports and logs/shell/restart/stop – and the row itself sums up the stack's CPU and RAM,
with its actions (redeploy, stop, edit, images, containers, volumes, down, delete) at the end.

### Containers – status, ports and resources at a glance

![Container list of both environments with status, port tags, RAM and CPU, a checkbox per row and the actions on the right](docs/images/containers.png)

Short status with the full text on hover, published ports as tags that open the app in a new
tab, and CPU + RAM as one compact column. Shell, logs, volumes, restart, pause, stop and remove per
row, several rows at once through the checkboxes, *Remove stopped* below; the list refreshes itself while it is open.

### One container in detail

![Container detail of gitea-gitea-1: actions on top, tabs for environment, networks, mounts, processes and inspect, the overview below](docs/images/container-detail.png)

Status, image, stack, ports, resources, restart policy, command and limits of one container – with restart, logs,
events, shell, files, duplicate, stop and remove on top and the environment variables (secrets masked), networks,
mounts, processes and the raw inspect in the tabs.

### Images: layers and vulnerabilities

![The vulnerabilities of gitea/gitea:1.22 after a Trivy scan: counts per severity, then CVE, package, installed and fixed version](docs/images/image-scan.png)

Tags, digest, platform and layers of every image, and a Trivy scan on the image's own host – the list shows the
critical and high findings of the last scan, a schedule scans the running images and reports new ones.

### Networks: who can reach whom

![Network map of homelab: gitea_default and proxy on the left, the containers on the right; gitea-gitea-1 is on both](docs/images/network-map.png)

Every network with its containers; a container on several networks (thick border) is how services of different
networks meet.

### Volumes: migration, backups and a file explorer

| File explorer | Volumes |
|---|---|
| ![File explorer in the volume gitea_gitea-data with its folders git, gitea and ssh](docs/images/files.png) | ![Volume list of both environments with mount point and used/unused](docs/images/volumes.png) |

Browse a volume like a folder – edit, preview, upload and download – switch volumes from the
dropdown, or migrate a whole volume to another host.

### Backups you can actually restore

![The archives at a restic target "NAS (restic)": stack bundles and their volume archives, each with restore, download and delete](docs/images/backups-targets.png)

Every archive present at a target – local, SMB or restic, including ones written by an older or a different MatDock
instance to the same NAS – ready to restore. Whole stacks are backed up as a bundle (volumes +
compose + image references) and come back as a running stack.

### Live logs

![Live logs of the whole gitea stack (db and gitea) with the search "Starting" highlighted, hit 1 of 6](docs/images/logs.png)

`docker logs -f` in the browser – of one container or a whole stack: follow, pause, timestamps, filter to matching
lines, and search with highlighting and next/previous.

### Updates

![Updates of every stack: the rule (Notify or Auto in a window 03–05 h), the result of the last check and when it was checked](docs/images/updates.png)

Which stacks have newer images, under which rule (off, notify, automatic in a time window, with backup and rollback),
checked per stack or all at once; *Update all* takes every stack with a pending update.

### Schedules and the activity log

| Schedules | Activity |
|---|---|
| ![Schedules: alerts on events, nightly stack backups, prune, a weekly vulnerability scan and image updates](docs/images/schedules.png) | ![Activity log: container restarts by a user and stack changes through the API](docs/images/activity.png) |

Updates with a health gate, vulnerability scans, prune, backups and alerts on crashed containers, restart loops or a
host over a limit; every action of a user and every API call is in the activity log.

### Apps and environments

| Apps | Environments |
|---|---|
| ![The app store: templates for Gitea, Jellyfin, Nextcloud, Paperless-ngx, Uptime Kuma and Vaultwarden to install as a stack](docs/images/apps.png) | ![Environments as a list: two Docker API hosts with status, RAM/CPU/disk, containers and Docker version](docs/images/environments.png) |

### Dark theme and narrow screens

| Dark | Phone | Menu |
|---|---|---|
| ![Dashboard in the dark theme](docs/images/dashboard-dark.png) ![Stacks in the dark theme](docs/images/stacks-dark.png) | ![Containers on a phone: every row as a card](docs/images/mobile-containers.png) | ![The sidebar as an overlay on a phone](docs/images/mobile-nav.png) |

## Quick start

Ready-made images are published to the GitHub Container Registry:

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matdock:release` (same image: `:latest`) | `main` | releases |
| `ghcr.io/real-ttx/matdock:<version>`, e.g. `:0.1.42-20261010` | `main` | pinning one release |
| `ghcr.io/real-ttx/matdock:nightly` | `dev` | the newest features |

`latest` is pushed together with `release` by every release build (older releases only carry `release` and
their version tag).

### 1. Just run it

```yaml
name: matdock

services:
  web:
    image: ghcr.io/real-ttx/matdock:release
    restart: unless-stopped
    ports:
      - "4455:4455"
    volumes:
      - data:/data
      # Optional: manage THIS host's Docker directly (a "local" environment)
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  data:
```

```bash
docker compose up -d
```

Open **http://localhost:4455**: the first visit opens a setup wizard that creates the administrator. (For a headless
install set `MatDock__Admin__Seed` and the two settings below it, see [Settings that matter](#settings-that-matter).)
The `data` volume (`matdock_data`) keeps the database, the configuration, the backups and the session keys, so an update is
just `docker compose pull && docker compose up -d`. Before this naming the volume was called `matdock-data`: to keep its
data, run `docker volume create matdock_data` and copy it over
(`docker run --rm -v matdock-data:/from -v matdock_data:/to alpine cp -a /from/. /to/`).

Without Compose:

```bash
docker run -d --name matdock -p 4455:4455 \
  -v matdock_data:/data -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/real-ttx/matdock:release
```

**Updating MatDock from MatDock.** MatDock can manage the stack it runs in: adopt it (*Stacks*, the external stack `matdock`)
or add it with the same name, and *Update* or *Redeploy* it like any other. MatDock knows its own container (it is marked
*This MatDock*), so the last step does not run in MatDock itself, where `compose up` would stop the process that runs it
and leave MatDock down. A short-lived helper container from MatDock's own image runs it a few seconds later, once the page has
answered. The page waits and loads again when MatDock is back, and the deploy history gets the helper's result after
the restart. A restart goes the same way. Stop, down and remove of MatDock's own stack or container are refused, because
nobody could start it again: do those on the host. This works through the local socket and over SSH, but not over a Docker
API connection (tcp). A MatDock started with a plain `docker run` (no compose) can be restarted from MatDock, but not re-created.

### 2. Add your hosts

MatDock reaches your servers over SSH – nothing to install there. In the UI:
*Environments → New environment*, enter host, user and a password or private key, run the
**connection test**, save. The sidebar switcher then flips every page between a single host and
**all of them at once**.

The socket mount above is only needed if you also want to manage the machine MatDock runs on as
a **local** environment; remove it for an SSH-only setup.

**Another MatDock as a bridge.** One MatDock can manage the environments of another one: on server2
make a key under *System → API keys* with *May: Bridge*. On server1 add an environment of the type *Another MatDock
(bridge)* with server2's address (`https://server2:4455`), that key and the environment there (`local`, or a host
server2 reaches over SSH). Server1 sends its Docker commands over HTTPS, and server2 runs them on that environment and
streams the answer back, so stacks, containers, volumes, backups and updates work as on any environment. Server2
can itself use a bridge, so chains work (`server1 → server2 → server3`, at most four bridges, so loops are cut).
The connection test learns from server2 how Docker is called there (sudo, a rootless socket). A self-signed
certificate is trusted by its SHA-256 fingerprint, which the test names. A bridge key opens only the bridge,
and no other key opens it, because it is as much as a shell with the Docker socket. Server1 also protects
the MatDock at the other end: it is not stopped from server1, and an update of its stack goes through a
helper container there. A terminal does not go through a bridge: open it on server2.

### 3. As a Windows app

Every build also produces a **self-contained Windows executable** (`win-x64`, no .NET install
needed) as a CI artifact – handy for running MatDock straight on a workstation.

### 4. From source

```bash
docker compose up -d --build                              # release build
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build    # dev stack + SQLite web viewer
```

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `MatDock__Admin__Seed` | `false` | `true` = create the initial administrator from the two settings below on an empty database (headless installs); `false` = the setup wizard asks on the first visit |
| `MatDock__Admin__Username` / `__Password` | `admin` / `admin` | Initial administrator – **only with `MatDock__Admin__Seed=true`**, first start only; the password must be changed on the first login |
| `MatDock__Api__Key` | – | Enables the REST API (see [API](#api-for-mattiverse)); without a key the API answers `503` |
| `MatDock__Environments__EnsureLocal` | `false` | `true` = create a *Local* environment `local` at start (if none exists and the Docker socket answers) |
| `MatDock__DataPath` | `/data` | Data directory (SQLite, config, keys, backups, local stack files) |
| `MatDock__TrustedProxies` | – | Comma list of the reverse proxies (addresses or networks, `172.18.0.0/16`) whose `X-Forwarded-For`/`-Proto` count. Unset = every sender is believed (as before); set it when MatDock can be reached without the proxy |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Standard ASP.NET Core environment |
| `MAT_API_KEY` | – | The key of the platform API (`/api/state`, `/api/configure`, `/api/actions`, see [Platform API](#platform-api-and-sign-in-through-matidentity)); Mattiverse sets it. Unset = the engine key `MatDock__Api__Key` is the key there too; neither = only a token of MatIdentity |
| `MATIDENTITY_AUTHORITY` | – | Address of MatIdentity (`https://id.example.com`). Set = sign-in through MatIdentity; unset = only the local sign-in, as before |
| `MATIDENTITY_CLIENT_ID` | `matdock` | The client of MatDock in MatIdentity (also the audience of the tokens accepted on `/api`) |
| `MATIDENTITY_CLIENT_SECRET` | – | Its secret |
| `MATIDENTITY_INTERNAL_URL` | – | Where MatDock reaches MatIdentity from inside Docker (`http://matidentity:2300`); the browser keeps using `MATIDENTITY_AUTHORITY` |
| `MATSMTP_HOST`, `_PORT`, `_USERNAME`, `_PASSWORD`, `_FROM`, `_TLS` | – | Mail through MatSMTP (Mattiverse sets them): set = every mail of MatDock (notifications, alerts, the test mail) goes through MatSMTP with this sender; *Settings → Notifications* shows the server as fixed (the own server settings stay stored for when the variables are gone) and `/api/configure` marks those fields `fixed`. `_TLS` is `none`, `starttls` or `tls` |

### The `/data` volume

```
/data
├─ matdock.db            SQLite database
├─ config/               JSON configuration
├─ dataprotection-keys/  session & secret keys (survive restarts)
├─ backups/              local backup archives
└─ stacks/<name>/        compose + .env of the stacks deployed to the local environment
```

Stacks on SSH hosts keep their files on the host in `~/.matdock/stacks/<name>`.

## API (for Mattiverse)

MatDock is one of the engines of [Mattiverse](https://github.com/Real-TTX/Mattiverse): Mattiverse installs apps as
MatDock stacks and uses MatDock's backups and schedules, so everything stays visible and editable here. The contract is
`docs/ENGINES.md` in the Mattiverse repository.

- **Key:** set `MatDock__Api__Key`, or make named keys under System → API keys (*read*: only `GET`; *everything*; with an
  optional expiry; only their SHA-256 is stored, the key is shown once). Every call sends one as `X-Api-Key` or as
  `Authorization: Bearer …` (compared in constant time). No key at all = `503`, wrong or missing key = `401`, a read key on a
  change = `403`. The session cookie plays no role; the activity log names a named key. Errors are `{"error": "…"}`.
- **Description:** `GET /api/v1/openapi.json` (OpenAPI, open).
- **Environment:** calls work on the *Local* environment (the mounted Docker socket) unless `?environment=<id or name>`
  says otherwise. `PUT /api/v1/environments/local` or `MatDock__Environments__EnsureLocal=true` creates it.
- **Long actions** answer `202 {"operationId": "…"}`; poll `GET /api/v1/operations/{id}?offset=<chars read>` for the
  output (streamed while it runs), `status` (`running|succeeded|failed`), `error` and `result`. Operations live in
  memory for an hour after they finish.
- **Managed by:** stacks and schedules created through the API carry `managedBy` (e.g. `mattiverse`). The Stacks and
  Schedules pages show a **"Managed by Mattiverse"** badge (tooltip: *Created by Mattiverse. Manual changes may be
  overwritten.*); editing such an object in MatDock keeps the value.

| Method and path | |
|---|---|
| `GET /api/v1/status` | `ok`, `app`, `version`, `apiVersion` (2), `environments` |
| `GET /api/v1/environments` | every environment with type, host, status, Docker version and its last reading (CPU, memory, disk, running containers) |
| `GET /api/v1/containers` | the containers with state, stack, service, ports, CPU and memory |
| `GET /api/v1/containers/{name}` | the inspect: state, restart policy, environment (secrets masked), labels, networks, mounts |
| `POST /api/v1/containers/{name}/actions/{action}` | `start`, `stop`, `restart`, `pause`, `unpause`, `kill` |
| `DELETE /api/v1/containers/{name}?force=&volumes=` | remove (a running one only with `force`) |
| `GET /api/v1/containers/{name}/logs?tail=200` | the last lines as text |
| `GET /api/v1/images`, `POST /api/v1/images/pull` (`{reference}`), `DELETE /api/v1/images?reference=&force=` | images with the containers that use them |
| `GET /api/v1/volumes`, `DELETE /api/v1/volumes/{name}` | volumes with `inUse`; one in use is never removed (`409`) |
| `GET /api/v1/networks`, `DELETE /api/v1/networks/{name}` | networks with `predefined` and `inUse` |
| `GET /api/v1/events?hours=24&type=&container=` | the events of the engine |
| `GET /api/v1/update-rules`, `GET`/`PUT /api/v1/update-rules/{global/0, environment/{id}, stack/{id}}` | the update rules `{mode, strategy, windowStartHour, windowEndHour, checkEveryHours, preBackup, rollback, notifyAvailable, notifyResult, serviceStrategies}`; `null` = as the wider rule says |
| `PUT /api/v1/environments/local` | ensure the Local environment `local` → `{id, name, created}`; `409` without a Docker socket |
| `GET /api/v1/stacks?managedBy=` | managed and discovered stacks with `state` (`running/stopped/partial/unhealthy/missing`) and containers |
| `GET /api/v1/stacks/{name}` | the same plus `composeYaml` and `envContent` |
| `PUT /api/v1/stacks/{name}` | upsert `{composeYaml, envContent, managedBy, deploy, pull}`; `200 {stack}` or with `deploy` `202 {stack, operationId}` |
| `POST /api/v1/stacks/{name}/actions/{action}` | `start`, `stop`, `restart`, `pull`, `redeploy`, `update` (`{backupFirst}`; result `{changed}`) → `202` |
| `PUT /api/v1/stacks/{name}/images/{service}` | run another image than the compose file names `{image}` (empty = the compose file's again), applied by the next deploy or update; `imageOverrides` in the stack detail |
| `DELETE /api/v1/stacks/{name}?volumes=false` | `compose down` (`--volumes`), then the stack is removed from MatDock → `202` |
| `GET /api/v1/stacks/{name}/logs?service=&tail=300` | `{text}` |
| `POST /api/v1/stacks/{name}/backups` | bundle backup `{stopContainers, note, targetId}` → `202`, result `{backupId}` |
| `GET /api/v1/backups?stack=` | the stack bundles, newest first |
| `POST /api/v1/backups/{id}/restore` | `{backupCurrentFirst}`; re-creates the stack when it is gone → `202` |
| `DELETE /api/v1/backups/{id}` | `204` |
| `GET /api/v1/backups/{id}/download` | the bundle (manifest + volume archives) as one `.tar` |
| `GET /api/v1/backup-schedules`, `PUT`/`DELETE /api/v1/backup-schedules/{name}` | bundle schedules `{stack, managedBy, frequency, time, weekday, retention, stopContainers, enabled}`; `time` is UTC |

A stack bundle holds all volumes of the stack, its compose file, its `.env` and the image references; bundle schedules
appear on the Schedules page (action *Backup stacks*) and keep the newest scheduled bundles per stack.

**Prometheus:** `GET /metrics` with a key (a read key is enough; a scrape job sends it with `authorization: {credentials: …}`)
answers the last reading of the sampler (every 5 minutes): `matdock_environment_up`, `matdock_host_cpu_percent`,
`matdock_host_memory_percent`, `matdock_host_disk_percent`, `matdock_containers_running`, `matdock_container_up`,
`matdock_container_cpu_percent`, `matdock_container_memory_percent`, `matdock_container_memory_bytes` and
`matdock_reading_age_seconds`, with the labels `environment`, `container`, `stack` and `image`.

**Redeploy webhook:** `POST /webhooks/stacks/{token}` (switched on per stack on its Updates page; the address is the secret)
pulls and redeploys the stack with the backup and the health check of its update rule.

## MCP: MatDock for an AI client

MatDock is an **MCP server** at `/mcp` (streamable HTTP, stateless), so an AI client such as Claude Code or Claude Desktop can look at and run
your Docker hosts with the same keys as the API. Make a key under System → API keys (*Read* for looking only, *Everything* to act), then:

```bash
claude mcp add --transport http matdock https://matdock.example.com/mcp --header "Authorization: Bearer mdk_…"
```

- **Rights:** a *Read* key sees and calls only the tools that change nothing; a call of another one is refused. The engine key `MatDock__Api__Key`
  works as a full key. Without a key `/mcp` answers `401`.
- **Audit:** every call of a tool that changes something is in the activity log (System → Activity, marked *MCP*) with the key's name, its target,
  environment and result.
- **Environment:** every tool that works on a host takes `environment` (id or name); with only one enabled environment it may be left out, with
  several it has to be named, so nothing lands on the wrong host.
- **Long actions** (deploy, stack actions, backups, restores) wait up to `waitSeconds` (default 120) and answer the output; one that still runs
  answers an operation id for `get_operation`.
- **Secrets:** environment variables of containers are masked as on the pages; the `.env` of a stack reaches only a full key (the REST API
  `GET /api/v1/stacks/{name}` keeps it from a read key too).
- Serve it over **HTTPS** (a reverse proxy): the key travels in a header.

| Tools that read | Tools that change |
|---|---|
| `get_status`, `get_engine_info`, `list_events`, `get_metrics`, `test_environment` | `set_environment_enabled`, `cleanup_engine` (never volumes) |
| `list_containers`, `inspect_container`, `container_logs`, `container_processes` | `container_action` (start, stop, restart, pause, unpause, kill), `remove_container`, `run_container`, `update_container`, `recreate_container`, `exec_in_container` |
| `list_images`, `list_volumes`, `list_networks` | `pull_image`, `remove_image`, `create_volume`, `remove_volume` (not while in use), `create_network`, `remove_network`, `connect_network` |
| `list_stacks`, `get_stack`, `stack_logs`, `check_stack_updates` | `deploy_stack`, `stack_action` (start, stop, restart, pull, redeploy, update), `stack_service_action` (incl. scale), `set_stack_image`, `remove_stack` |
| `get_operation`, `list_backups`, `list_schedules`, `list_update_rules`, `list_activity` | `backup_stack`, `restore_backup`, `run_schedule`, `set_update_rule` |

## Platform API and sign-in through MatIdentity

Besides its engine API, MatDock speaks the platform contract of the Mat apps (MatDesign `docs/PLATFORM.md`, shared code
MatPlatform in `src/MatDock.Web/MatPlatform/`, a copy – never edit it here), so Mattiverse can show, watch and set it up
without knowing it beforehand. It lives next to `/api/v1` and never under it; `/api/v1` is unchanged.

| Endpoint | Access | |
|---|---|---|
| `GET /api` | anonymous, rate limited | the MatApiDefinition: pages, tiles, settings, actions, roles (German or English by `Accept-Language`), `ETag` |
| `GET /api/health` | anonymous, rate limited | `ok` / `degraded` (`200`) or `down` (`503`); the Docker `HEALTHCHECK` calls it |
| `GET /api/state`, `/api/state/{key}` | key or token | the values of the tiles |
| `GET`/`PUT /api/configure` | key or token | the few settings; a secret is only ever `{"isSet": bool}` |
| `POST /api/actions/{id}` | key or token | run an action → `{"ok", "message"}` (`409` when it cannot run now) |

- **Key:** `X-Api-Key` = `MAT_API_KEY`, or the engine key `MatDock__Api__Key` when `MAT_API_KEY` is not set (one key for a
  caller that only knows the engine key). `/api/v1` takes `MatDock__Api__Key` or a named key, not `MAT_API_KEY`. Without a key `401`.
  With MatIdentity on, a bearer token of MatIdentity for the audience `matdock` works too.
- **Tiles:** environments (and the unreachable ones), stacks, containers running / stopped / unhealthy, images, the
  disk of the local Docker host (used of total), the last backup (status and time), the next scheduled backup, failed
  backups in the last 24 hours and the size of the stored backups. Docker is asked only on reachable environments,
  with a short timeout, and the answers are cached for 30 seconds.
- **Health:** `down` when the database cannot be read; `degraded` when an enabled environment is unreachable or there is
  no active administrator.
- **Settings:** the notification settings (the same as *Settings → Notifications*): notify on failures, e-mail (server,
  port, user, password, sender, recipients) and the webhook. With MatSMTP the server, port, user, password and sender are
  `fixed` (a change answers `400`); on/off and the recipients stay editable.
- **Actions:** open MatDock, Stacks, Backups; *Run all backups now* (every enabled backup schedule, started in the
  background, the result shows in the tiles); *Prune dangling images* on every reachable environment (asks first).
- **Roles:** `admin`, `user`.

**Sign-in through MatIdentity** (only with `MATIDENTITY_AUTHORITY`; without it nothing changes): the sign-in page goes
straight to MatIdentity (authorization code with PKCE). Redirect address `/signin-oidc`, signed-out address
`/signout-callback-oidc`, back-channel logout `/signout-backchannel` (`POST`, a logout token signed by MatIdentity: the
MatDock session opened with that MatIdentity session (`sid`) ends, or every session of the person for `sub` alone; the next
request of it lands on the sign-in page; written to the activity log as `MatIdentity`). The local sign-in stays at `/Account/Login?local=1` (a way in when MatIdentity is down).
The person is matched by `sub`; the first sign-in creates the user (no password). An existing account whose user name
equals the person's `preferred_username` or e-mail is **not** linked by the name alone: the sign-in page asks to sign in
once with that account's password (`?local=1&link=1`, or cancel), then it is linked. Several matches, or a match linked to
another person, are refused. The claim `role` (`admin` / `user` / `viewer`) sets the role at every sign-in, never demoting the last
administrator; without it MatDock keeps managing roles itself. Signing out ends the session at MatIdentity too.

## How it is built

- **ASP.NET Core 10** (Razor Pages) with **EF Core** on SQLite
- Docker access without an agent: **SSH.NET** opens a channel to the host and runs the Docker
  CLI; a **local** environment talks to the mounted socket instead – both behind one interface
- Volume migration & backups stream a **tar over SSH** (no temp files on the host); backup
  targets are the local volume, **SMB** (SMBLibrary, no OS mount) or **restic** (each backup file is one snapshot; restic runs without a shell, its keys never on a command line)
- The web terminal is **xterm.js** over a WebSocket ⇄ SSH PTY; live logs stream the same way
- The interface is **plain JavaScript** – no framework, no build step; secrets are encrypted
  with **DataProtection**

## Status

| Milestone | Content | Status |
|---|---|---|
| Foundation | Docker, CI, versioning, auth, roles, sessions | ✅ |
| Environments | SSH + local socket, host stats, global selector | ✅ |
| Volumes | Listing, migration between hosts, file explorer | ✅ |
| Backups | Scheduled jobs, retention, history, SMB and restic targets, restore | ✅ |
| Containers & stacks | Managed + discovered stacks, containers, live logs, terminal | ✅ |
| Backup v2 | Browse/restore archives at a target, full stack bundles | ✅ |
| Apps | Compose templates with icons, one-click install | ✅ |
| Review | Security fixes, alerts, scheduled updates, activity log, network map, container files – see [docs/REVIEW.md](docs/REVIEW.md) | ✅ |
| Review 2 | MatSMTP, update rollback, CVE scans, Docker API hosts, Git over SSH, files without a shell | ✅ |
| MatUi | The UI on the MatUi toolkit like the other engines (shell, components, dialogs, accent of the registry) | ✅ |
| Review 3 | restic targets, update history and pinning, container run, rights per environment, network create/connect, pause/kill, image pull, MatUi 0.3 | ✅ |
| Update management | Image overrides, update rules per scope, automation, Updates overview with Update all | ✅ |
| Audit (complete manager) | Container detail/remove/duplicate/recreate, security headers, secrets at rest, system view, IPAM, image tag/push/export, service actions and scale, webhooks, stack copy, alert channels and limits, metrics and Prometheus, API v2 with named keys and OpenAPI, MatDock backup, Viewer role, events, multi-select, search – see [docs/REVIEW.md](docs/REVIEW.md) section 9 | ✅ |

## Trying a change locally

`./scripts/localtest.ps1` builds the current source and starts a **separate** test instance (`matdock-localtest`, own data volume, never the
live one) on `http://127.0.0.1:4470`, signed in with `tester` / `Tester123!` (Development mode seeds it). It mounts the Docker socket, so its
*local* environment is this machine's Docker and what you click is real; because of that it listens on `127.0.0.1` only. `-Fresh` starts with
empty data, `-Stop` removes it, `-Port` changes the port. `-MatSmtp <container>` sends its mail through a running MatSMTP (relay user
`matdock-localtest`, made through the management API, removed again by `-Stop -MatSmtp <container>`): Settings → Notifications then shows the server as fixed.
Mind where that MatSMTP delivers; use a recipient like `test@example.invalid`.

## Branches & versioning

| Branch | Purpose | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly-<build>-<yyyyMMdd>` |
| local | – | `local-<yyyyMMdd>` |

`Major`/`Minor` live in [`Directory.Build.props`](Directory.Build.props); the build number comes
from the GitHub action. Images are published to the GitHub Container Registry, and a
self-contained Windows EXE is attached to every build.
