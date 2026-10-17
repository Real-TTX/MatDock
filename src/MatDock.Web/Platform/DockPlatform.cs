using System.Text.Json;
using MatDock.Core;
using MatDock.Core.Notifications;
using MatDock.Core.Operations;
using MatDock.Core.Status;
using MatPlatform;

namespace MatDock.Web.Platform;

/// <summary>
/// MatDock as an app of the platform contract (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, settings, buttons), what its values are and what the buttons do. The values come from the
/// services of MatDock.Core (<see cref="InstanceStatusService"/>, <see cref="NotificationSettingsService"/>); this class is the web layer and never
/// touches the database itself. The settings are the notification settings (the page Settings / Notifications shows the same values).
/// Texts are German, or English when the caller asks for it with <c>Accept-Language</c>.
/// </summary>
public sealed class DockPlatform(
    InstanceStatusService status,
    NotificationSettingsService notifications,
    OperationRegistry operations,
    ILogger<DockPlatform> logger) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatDock in the platform (MatDesign AppRegistry) and the default client id in MatIdentity.</summary>
    public const string AppId = "matdock";

    public const string Accent = "#1d78be";

    /// <summary>The fields of the platform form and the property of <see cref="NotificationSettingsInput"/> each one is.</summary>
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["notifyOnFailure"] = nameof(NotificationSettingsInput.NotifyOnFailure),
        ["smtpEnabled"] = nameof(NotificationSettingsInput.SmtpEnabled),
        ["smtpHost"] = nameof(NotificationSettingsInput.SmtpHost),
        ["smtpPort"] = nameof(NotificationSettingsInput.SmtpPort),
        ["smtpUsername"] = nameof(NotificationSettingsInput.SmtpUsername),
        ["smtpPassword"] = nameof(NotificationSettingsInput.SmtpPassword),
        ["smtpFrom"] = nameof(NotificationSettingsInput.SmtpFrom),
        ["smtpTo"] = nameof(NotificationSettingsInput.SmtpTo),
        ["webhookEnabled"] = nameof(NotificationSettingsInput.WebhookEnabled),
        ["webhookUrl"] = nameof(NotificationSettingsInput.WebhookUrl),
    };

    /// <summary>1 while "run all backups now" runs (one at a time).</summary>
    private static int _backupsRunning;

    /// <summary>The MatApiDefinition of MatDock (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(
            AppId,
            "MatDock",
            T("Docker-Verwaltung für Server: Stacks, Container, Volumes und Sicherungen", "Docker management for servers: stacks, containers, volumes and backups"),
            AppVersion.Informational,
            "/img/icon.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("environments", "environmentsUnreachable", "stacks", "containersRunning", "containersStopped", "containersUnhealthy", "images", "hostDisk"),
            new MatSection("tiles", ["lastBackup", "lastBackupAt", "nextBackup", "backupFailures24h", "backupSize"], T("Sicherungen", "Backups")),
            MatSection.Actions("open", "stacks", "backups", "runBackups", "pruneImages"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Benachrichtigungen", "Notifications"), "notifyOnFailure"),
            MatSection.Form(T("E-Mail", "E-mail"), "smtpEnabled", "smtpHost", "smtpPort", "smtpUsername", "smtpPassword", "smtpFrom", "smtpTo"),
            MatSection.Form("Webhook", "webhookEnabled", "webhookUrl"));

        o.Tile("environments", T("Umgebungen", "Environments"), icon: "server");
        o.Tile(new MatStateItem("environmentsUnreachable", T("Nicht erreichbare Umgebungen", "Unreachable environments")) { Icon = "alert", WarnAbove = 0 });
        o.Tile("stacks", "Stacks", icon: "layers");
        o.Tile("containersRunning", T("Laufende Container", "Running containers"), icon: "play");
        o.Tile("containersStopped", T("Gestoppte Container", "Stopped containers"), icon: "stop");
        o.Tile(new MatStateItem("containersUnhealthy", T("Container mit Health-Fehler", "Unhealthy containers")) { Icon = "alert", ErrorAbove = 0 });
        o.Tile("images", "Images", icon: "image");
        o.Tile(new MatStateItem("hostDisk", T("Festplatte des Docker-Hosts", "Disk of the Docker host"), "meter")
        {
            Icon = "hard-drive",
            Unit = "bytes",
            Max = JsonSerializer.SerializeToElement("hostDiskTotal"),
        });
        o.Tile(new MatStateItem("lastBackup", T("Letzte Sicherung", "Last backup"), "status") { Icon = "archive" });
        o.Tile("lastBackupAt", T("Letzte Sicherung um", "Last backup at"), "time", icon: "clock");
        o.Tile("nextBackup", T("Nächste geplante Sicherung", "Next scheduled backup"), "time", icon: "calendar");
        o.Tile(new MatStateItem("backupFailures24h", T("Fehlgeschlagene Sicherungen (24 h)", "Failed backups (24 h)")) { Icon = "x-circle", ErrorAbove = 0 });
        o.Tile("backupSize", T("Gespeicherte Sicherungen", "Stored backups"), icon: "database", unit: "bytes");

        o.Field(new MatField("notifyOnFailure", T("Bei Fehlern benachrichtigen", "Notify on failures"), "bool")
        {
            Hint = T("Ergebnisse von Sicherungen und Zeitplänen per E-Mail und/oder Webhook.", "Results of backups and schedules by e-mail and/or webhook."),
        });
        o.Field(new MatField("smtpEnabled", T("E-Mail senden", "Send e-mail"), "bool"));
        o.Field(new MatField("smtpHost", T("Mailserver", "Mail server"), "text") { Required = true, Max = 255, ShowIf = MatField.When(("smtpEnabled", true)) });
        o.Field(new MatField("smtpPort", "Port", "number") { Required = true, Min = 1, Max = 65535, Placeholder = "587", ShowIf = MatField.When(("smtpEnabled", true)) });
        o.Field(new MatField("smtpUsername", T("Benutzername", "User name"), "text") { Max = 255, ShowIf = MatField.When(("smtpEnabled", true)) });
        o.Field(new MatField("smtpPassword", T("Passwort", "Password"), "secret") { ShowIf = MatField.When(("smtpEnabled", true)) });
        o.Field(new MatField("smtpFrom", T("Absender", "Sender"), "email") { Required = true, Max = 255, ShowIf = MatField.When(("smtpEnabled", true)) });
        o.Field(new MatField("smtpTo", T("Empfänger", "Recipients"), "text")
        {
            Required = true,
            Max = 1000,
            Hint = T("Mehrere Adressen mit Komma oder Semikolon trennen.", "Separate several addresses with a comma or semicolon."),
            ShowIf = MatField.When(("smtpEnabled", true)),
        });
        o.Field(new MatField("webhookEnabled", T("Webhook aufrufen", "Call a webhook"), "bool"));
        o.Field(new MatField("webhookUrl", T("Webhook-Adresse", "Webhook address"), "url")
        {
            Required = true,
            Max = 2000,
            Placeholder = "https://",
            ShowIf = MatField.When(("webhookEnabled", true)),
        });

        o.LinkAction("open", T("MatDock öffnen", "Open MatDock"), "external", "/");
        o.LinkAction("stacks", "Stacks", "layers", "/Stacks");
        o.LinkAction("backups", T("Sicherungen", "Backups"), "archive", "/Backups");
        o.PostAction("runBackups", T("Alle Sicherungen jetzt ausführen", "Run all backups now"), "play",
            T("Alle aktiven Sicherungszeitpläne jetzt ausführen?", "Run all enabled backup schedules now?"));
        o.PostAction("pruneImages", T("Ungenutzte Images entfernen", "Prune dangling images"), "broom",
            T("Alle Images ohne Namen (dangling) auf allen Umgebungen löschen?", "Delete all dangling (untagged) images on every environment?"), danger: true);

        o.Role("admin", "Administrator");
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
        => (await status.CheckHealthAsync(ct)).Select(c => new MatHealthCheck(c.Name, c.Status, c.Message)).ToList();

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await status.GetSnapshotAsync(ct);
        var (level, text) = s.LastBackupOk switch
        {
            true => ("ok", s.LastBackupText ?? call.T("Erfolgreich", "Succeeded")),
            false => ("error", s.LastBackupText ?? call.T("Fehlgeschlagen", "Failed")),
            null => ("info", call.T("Noch keine Sicherung", "No backup yet")),
        };

        return new Dictionary<string, MatStateValue>
        {
            ["environments"] = new(s.Environments),
            ["environmentsUnreachable"] = new(s.EnvironmentsUnreachable, s.EnvironmentsUnreachable > 0 ? "warn" : null),
            ["stacks"] = new(s.Stacks),
            ["containersRunning"] = new(s.ContainersRunning),
            ["containersStopped"] = new(s.ContainersStopped),
            ["containersUnhealthy"] = new(s.ContainersUnhealthy, s.ContainersUnhealthy > 0 ? "error" : null),
            ["images"] = new(s.Images),
            ["hostDisk"] = new(s.HostDiskUsedBytes),
            ["hostDiskTotal"] = new(s.HostDiskTotalBytes),
            ["lastBackup"] = new(level, level, text),
            ["lastBackupAt"] = new(Time(s.LastBackupUtc)),
            ["nextBackup"] = new(Time(s.NextBackupUtc)),
            ["backupFailures24h"] = new(s.BackupFailures24h, s.BackupFailures24h > 0 ? "error" : null),
            ["backupSize"] = new(s.BackupBytes),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var settings = await notifications.GetAsync(ct);
        return new Dictionary<string, object?>
        {
            ["notifyOnFailure"] = settings.NotifyOnFailure,
            ["smtpEnabled"] = settings.SmtpEnabled,
            ["smtpHost"] = settings.SmtpHost,
            ["smtpPort"] = settings.SmtpPort,
            ["smtpUsername"] = settings.SmtpUsername,
            // Never the password itself: MatPlatform sends {"isSet": bool} for a secret whatever comes here.
            ["smtpPassword"] = new MatSecret(!string.IsNullOrEmpty(settings.EncryptedSmtpPassword)),
            ["smtpFrom"] = settings.SmtpFrom,
            ["smtpTo"] = settings.SmtpTo,
            ["webhookEnabled"] = settings.WebhookEnabled,
            ["webhookUrl"] = settings.WebhookUrl,
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var input = NotificationSettingsInput.From(await notifications.GetAsync(ct));
        var errors = new Dictionary<string, string>();

        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "notifyOnFailure" or "smtpEnabled" or "webhookEnabled" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    var on = value.GetBoolean();
                    _ = key switch
                    {
                        "notifyOnFailure" => input.NotifyOnFailure = on,
                        "smtpEnabled" => input.SmtpEnabled = on,
                        _ => input.WebhookEnabled = on,
                    };
                    break;
                case "smtpPort" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port):
                    input.SmtpPort = port;
                    break;
                case "smtpPassword" when value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    var password = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    input.SmtpPassword = string.IsNullOrEmpty(password) ? null : password;
                    input.ClearSmtpPassword = string.IsNullOrEmpty(password);
                    break;
                case "smtpHost" or "smtpUsername" or "smtpFrom" or "smtpTo" or "webhookUrl" when value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    _ = key switch
                    {
                        "smtpHost" => input.SmtpHost = text,
                        "smtpUsername" => input.SmtpUsername = text,
                        "smtpFrom" => input.SmtpFrom = text,
                        "smtpTo" => input.SmtpTo = text,
                        _ => input.WebhookUrl = text,
                    };
                    break;
                case "notifyOnFailure" or "smtpEnabled" or "webhookEnabled":
                    errors[key] = call.T("Bitte true oder false angeben.", "Please send true or false.");
                    break;
                case "smtpPort":
                    errors[key] = call.T("Bitte eine ganze Zahl angeben.", "Please send a whole number.");
                    break;
                case var _ when Fields.ContainsKey(key):
                    errors[key] = call.T("Bitte einen Text angeben.", "Please send a text.");
                    break;
                default:
                    errors[key] = call.T("Diese Einstellung gibt es nicht.", "There is no such setting.");
                    break;
            }
        }

        // The rules of the settings page, reported under the keys of the platform form.
        foreach (var (property, error) in NotificationSettingsService.Validate(input))
        {
            errors.TryAdd(Fields.FirstOrDefault(f => f.Value == property).Key ?? property, error);
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        await notifications.UpdateAsync(input, ct);
        logger.LogInformation("Notification settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return new Dictionary<string, string>();
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "runBackups":
                return await RunBackupsAsync(call, ct);

            case "pruneImages":
                var (ok, removed, reclaimed, errors) = await status.PruneDanglingImagesAsync(ct);
                logger.LogInformation("Dangling images pruned through the platform API by {Caller}: {Removed}", call.Caller, removed);
                var freed = reclaimed.Count > 0 ? call.T($", {string.Join(" + ", reclaimed)} frei", $", freed {string.Join(" + ", reclaimed)}") : string.Empty;
                var message = call.T($"{removed} Image(s) entfernt{freed}.", $"{removed} image(s) removed{freed}.");
                return ok ? new MatActionResult(true, message) : new MatActionResult(false, message + " " + string.Join("; ", errors));

            default:
                return null;
        }
    }

    private async Task<MatActionResult> RunBackupsAsync(MatPlatformCall call, CancellationToken ct)
    {
        var count = await status.CountBackupSchedulesAsync(ct);
        if (count == 0)
        {
            return new MatActionResult(false, call.T("Es gibt keinen aktiven Sicherungszeitplan.", "There is no enabled backup schedule."));
        }

        if (Interlocked.CompareExchange(ref _backupsRunning, 1, 0) != 0)
        {
            return new MatActionResult(false, call.T("Die Sicherungen laufen schon.", "The backups are already running."));
        }

        logger.LogInformation("All backup schedules started through the platform API by {Caller}", call.Caller);
        operations.Start("backups", null, "platform:backups", async context =>
        {
            try
            {
                var (ran, failed) = await context.Services.GetRequiredService<InstanceStatusService>().RunAllBackupsAsync(context.CancellationToken);
                context.Log($"{ran} backup schedule(s) run, {failed} failed.");
                return failed == 0 ? OperationOutcome.Success() : OperationOutcome.Failure($"{failed} of {ran} backup schedule(s) failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _backupsRunning, 0);
            }
        });

        // The result shows up in /api/state (last backup, failures).
        return new MatActionResult(true, call.T($"Gestartet: {count} Sicherungszeitplan/-pläne.", $"Started: {count} backup schedule(s)."));
    }

    private static DateTimeOffset? Time(DateTime? utc)
        => utc is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
}
