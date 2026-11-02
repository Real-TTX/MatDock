using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using MatDock.Core.Templates;
using MatDock.Web.Api;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Stacks;

public class EditModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly StackTemplateService _templateService;
    private readonly GitCredentialService _gitCredentialService;
    private readonly GitRepoService _gitRepoService;
    private readonly OperationRegistry _operations;
    private readonly MatDock.Core.Containers.ContainerService _containers;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
    private readonly GitSyncSchedules _gitSync;
    private readonly TagService _tags;
    private readonly AppLogos _logos;

    public EditModel(
        StackService stackService,
        EnvironmentService environmentService,
        StackTemplateService templateService,
        GitCredentialService gitCredentialService,
        GitRepoService gitRepoService,
        OperationRegistry operations,
        MatDock.Core.Containers.ContainerService containers,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        GitSyncSchedules gitSync,
        TagService tags,
        AppLogos logos)
    {
        _tags = tags;
        _logos = logos;
        _gitSync = gitSync;
        _containers = containers;
        _cache = cache;
        _operations = operations;
        _stackService = stackService;
        _environmentService = environmentService;
        _templateService = templateService;
        _gitCredentialService = gitCredentialService;
        _gitRepoService = gitRepoService;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<GitCredential> GitCredentials { get; private set; } = new();
    public List<GitRepo> SavedRepos { get; private set; } = new();
    public List<StackTemplate> Apps { get; private set; } = new();
    public string? FromTemplateName { get; private set; }

    /// <summary>Owner of an externally managed stack (shown as a hint; a save here keeps it).</summary>
    public string? ManagedBy { get; private set; }

    /// <summary>The images MatDock runs instead of the ones in the compose file (shown as a hint; the file is not changed).</summary>
    public IReadOnlyDictionary<string, MatDock.Core.Stacks.ImageOverride> Overrides { get; private set; } = new Dictionary<string, MatDock.Core.Stacks.ImageOverride>();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    public string? DeployOutput { get; private set; }

    /// <summary>The deploy started from this page (<c>?op=</c>), shown in the console.</summary>
    public OperationConsoleModel? Console { get; private set; }

    /// <summary>The commit of the repository the last deploy or sync of a Git stack saw.</summary>
    public string? LastCommit { get; private set; }

    /// <summary>The icon of the compose file's <c>x-matdock</c> block (shown when MatDock has none of its own).</summary>
    public string? ComposeIcon { get; private set; }

    /// <summary>The tags of the catalog, as suggestions.</summary>
    public List<Tag> TagCatalog { get; private set; } = new();

    /// <summary>One secret variable of the form: a stored one (<see cref="Existing"/>: an empty value keeps it) or a new one.</summary>
    public sealed class SecretRow
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        public bool Existing { get; set; }
        public bool Remove { get; set; }
    }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        [RegularExpression(@"^[a-z0-9][a-z0-9_-]{0,62}\z", ErrorMessage = "Only lowercase letters, digits, _ and -, must start alphanumeric (max. 63 characters).")]
        public string Name { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Environment is required.")]
        public long EnvironmentId { get; set; }

        /// <summary>"inline" (compose editor) or "git" (clone a repository).</summary>
        public string Source { get; set; } = "inline";

        // Inline source. Nullable so a git-backed stack (empty editor) doesn't trip the implicit
        // "non-nullable reference type is required" model validation; ValidateSource enforces it for inline.
        public string? ComposeYaml { get; set; }

        /// <summary>Optional .env file for the inline stack (variable substitution + defaults).</summary>
        public string? EnvContent { get; set; }

        // Git source
        public string? GitRepoUrl { get; set; }
        public string? GitReference { get; set; }
        public string? GitComposePath { get; set; }
        public long? GitCredentialId { get; set; }

        /// <summary>An env file of the repository besides its .env, relative to the compose file (Git stacks).</summary>
        public string? GitEnvFile { get; set; }

        /// <summary>The secret variables: the stored ones (their value only when it changes) and new ones.</summary>
        public List<SecretRow> Secrets { get; set; } = new();

        /// <summary>The tags, comma-separated.</summary>
        public string? Tags { get; set; }

        /// <summary>The icon chosen in MatDock (empty = the compose file's).</summary>
        public string? Icon { get; set; }

        /// <summary>The automatic sync of a Git stack: off, 5, 15, 60 (minutes), daily, or custom (<see cref="GitSyncCron"/>).</summary>
        public string GitSync { get; set; } = "off";

        /// <summary>The cron of a custom automatic sync.</summary>
        public string? GitSyncCron { get; set; }

        /// <summary>The cron the choice stands for, null for none.</summary>
        public string? SyncCron => GitSync switch
        {
            "5" => "*/5 * * * *",
            "15" => "*/15 * * * *",
            "60" => "0 * * * *",
            "daily" => "0 4 * * *",
            "custom" => string.IsNullOrWhiteSpace(GitSyncCron) ? null : GitSyncCron.Trim(),
            _ => null,
        };

        public void SetSync(string? cron)
        {
            GitSync = cron switch
            {
                null or "" => "off",
                "*/5 * * * *" => "5",
                "*/15 * * * *" => "15",
                "0 * * * *" => "60",
                "0 4 * * *" => "daily",
                _ => "custom",
            };
            GitSyncCron = GitSync == "custom" ? cron : null;
        }

        // Deploy options: the stack's defaults, changed per deploy here and kept on save.
        public bool OptPull { get; set; }
        public bool OptBuild { get; set; }
        public bool OptPullBase { get; set; }
        public bool OptNoCache { get; set; }
        public bool OptForceRecreate { get; set; }
        public bool OptRemoveOrphans { get; set; }

        public DeployOptions Options => new()
        {
            Pull = OptPull, Build = OptBuild, PullBase = OptPullBase, NoCache = OptNoCache, ForceRecreate = OptForceRecreate, RemoveOrphans = OptRemoveOrphans,
        };

        public void SetOptions(DeployOptions o)
        {
            OptPull = o.Pull; OptBuild = o.Build; OptPullBase = o.PullBase; OptNoCache = o.NoCache; OptForceRecreate = o.ForceRecreate; OptRemoveOrphans = o.RemoveOrphans;
        }
    }

    public async Task<IActionResult> OnGetAsync(long? id, long? templateId, string? source, string? op)
    {
        await LoadListsAsync();

        if (id is > 0)
        {
            var stack = await _stackService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (stack is null)
            {
                return RedirectToPage("Index");
            }

            ManagedBy = stack.ManagedBy;
            Overrides = MatDock.Core.Stacks.ImageOverrides.Of(stack);
            Input = new InputModel
            {
                Id = stack.Id,
                Name = stack.Name,
                EnvironmentId = stack.EnvironmentId,
                Source = stack.IsGitBacked ? "git" : "inline",
                ComposeYaml = stack.ComposeYaml,
                EnvContent = stack.EnvContent,
                GitRepoUrl = stack.GitRepoUrl,
                GitReference = stack.GitReference,
                GitComposePath = stack.GitComposePath,
                GitCredentialId = stack.GitCredentialId,
                GitEnvFile = stack.GitEnvFile,
                Secrets = StackSecrets.Names(stack.SecretsJson).Select(n => new SecretRow { Key = n, Existing = true }).ToList(),
                Tags = stack.Tags,
                Icon = stack.Icon,
            };
            ComposeIcon = MatDock.Core.Apps.AppMetadataParser.FromJson(stack.AppMetaJson)?.Icon;
            Input.SetOptions(DeployOptions.FromJson(stack.DeployOptionsJson));
            Input.SetSync(stack.GitSyncCron);
            LastCommit = stack.GitLastCommit;

            if (!string.IsNullOrEmpty(op) && _operations.Get(op) is { } running)
            {
                var verb = running.Kind == "sync" ? ("Sync", "Syncing") : ("Deploy", "Deploying");
                Console = new OperationConsoleModel(running, running.IsFinished ? $"{verb.Item1} of {stack.Name}" : $"{verb.Item2} {stack.Name}…", $"{verb.Item1.ToLowerInvariant()}-{stack.Name}");
            }
        }
        else if (templateId is > 0)
        {
            var template = await _templateService.GetAsync(templateId.Value, HttpContext.RequestAborted);
            if (template is null)
            {
                return RedirectToPage("/Apps/Index");
            }

            FromTemplateName = template.Name;
            Input.Name = StackCommands.Slugify(template.Name);
            Input.ComposeYaml = template.ComposeYaml;
        }
        else if (string.Equals(source, "git", StringComparison.OrdinalIgnoreCase))
        {
            // Deep link from the Stacks page: start a new stack on the Git source tab.
            Input.Source = "git";
        }
        else if (string.IsNullOrEmpty(Input.ComposeYaml))
        {
            Input.ComposeYaml = "services:\n  app:\n    image: nginx:alpine\n    restart: unless-stopped\n    ports:\n      - \"8080:80\"\n";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadListsAsync();
        if (!ValidateSource() || !ModelState.IsValid)
        {
            return Page();
        }

        var result = await SaveAsync();
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        StatusMessage = result.Message;
        IsError = false;
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostSaveDeployAsync()
    {
        await LoadListsAsync();
        if (!ValidateSource() || !ModelState.IsValid)
        {
            return Page();
        }

        var result = await SaveAsync();
        if (!result.Ok)
        {
            ModelState.AddModelError(string.Empty, result.Message);
            return Page();
        }

        // The deploy runs in the background; the page follows it in its console.
        var operation = StackOperations.Deploy(_operations, Input.EnvironmentId, result.Id, Input.Name.Trim(),
            new DeployRequest(Input.Options, DeployTrigger.Manual, User.Identity?.Name));
        // What the save said about an image override that the edit made obsolete stays visible.
        const string saved = "Stack saved. ";
        if (result.Message.StartsWith(saved, StringComparison.Ordinal))
        {
            StatusMessage = result.Message;
            IsError = false;
        }

        return RedirectToPage(new { id = result.Id, op = operation.Id });
    }

    /// <summary>Lets compose on the chosen environment check the compose file and the .env, without saving or deploying.</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        await LoadListsAsync();
        ModelState.Clear();
        if (Input.EnvironmentId <= 0 || string.IsNullOrWhiteSpace(Input.ComposeYaml))
        {
            StatusMessage = "Choose an environment and enter a compose file to check it.";
            IsError = true;
            return Page();
        }

        // With the stored secrets, so compose resolves the variables as a deploy will.
        var secrets = IsEdit ? (await _stackService.GetAsync(Input.Id!.Value, HttpContext.RequestAborted))?.SecretsJson : null;
        var (ok, output) = await _stackService.CheckComposeAsync(Input.EnvironmentId, Input.ComposeYaml, Input.EnvContent, secrets, HttpContext.RequestAborted);
        DeployOutput = output.Trim();
        StatusMessage = ok ? "Compose accepts the file." : "Compose found a problem.";
        IsError = !ok;
        return Page();
    }

    /// <summary>Source-specific required fields (ComposeYaml for inline, GitRepoUrl for git).</summary>
    private bool ValidateSource()
    {
        if (Input.Source == "git")
        {
            if (string.IsNullOrWhiteSpace(Input.GitRepoUrl))
            {
                ModelState.AddModelError("Input.GitRepoUrl", "Git URL is required.");
            }
        }
        else if (string.IsNullOrWhiteSpace(Input.ComposeYaml))
        {
            ModelState.AddModelError("Input.ComposeYaml", "Compose YAML is required.");
        }
        else if (MatDock.Core.Stacks.ComposeCheck.Problem(Input.ComposeYaml) is { } problem)
        {
            ModelState.AddModelError("Input.ComposeYaml", problem);
        }

        return ModelState.IsValid;
    }

    private async Task<(bool Ok, string Message, long Id)> SaveAsync()
    {
        var isGit = Input.Source == "git";
        var input = new StackInput
        {
            Name = Input.Name,
            EnvironmentId = Input.EnvironmentId,
            ComposeYaml = isGit ? string.Empty : (Input.ComposeYaml ?? string.Empty),
            EnvContent = Input.EnvContent,
            GitRepoUrl = isGit ? Input.GitRepoUrl : null,
            GitReference = isGit ? Input.GitReference : null,
            GitComposePath = isGit ? Input.GitComposePath : null,
            GitCredentialId = isGit ? Input.GitCredentialId : null,
            GitEnvFile = isGit ? Input.GitEnvFile : null,
            GitSyncCron = isGit ? Input.SyncCron : null,
            Tags = Input.Tags ?? string.Empty,
            Icon = Input.Icon ?? string.Empty,
        };

        // The secrets are checked before anything is saved.
        var (set, remove, problem) = SecretChanges();
        if (problem is not null)
        {
            return (false, problem, 0);
        }

        (bool Ok, string Message, long Id) result;
        if (IsEdit)
        {
            var (ok, message) = await _stackService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            result = (ok, message, Input.Id!.Value);
        }
        else
        {
            result = await _stackService.CreateAsync(input, HttpContext.RequestAborted);
        }

        if (result.Ok)
        {
            await _stackService.SetDeployOptionsAsync(result.Id, Input.Options, HttpContext.RequestAborted);
            if (await _stackService.GetAsync(result.Id, HttpContext.RequestAborted) is { } saved)
            {
                await _gitSync.ApplyAsync(saved, HttpContext.RequestAborted);
                // A new tag name joins the catalog (without a colour; System > Tags gives it one).
                await _tags.EnsureAsync(TagService.Parse(saved.Tags), HttpContext.RequestAborted);
            }
            if (set.Count > 0 || remove.Count > 0)
            {
                var (ok, message) = await _stackService.SetSecretsAsync(result.Id, set, remove, HttpContext.RequestAborted);
                if (!ok)
                {
                    return (false, message, result.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// What the form changes of the secrets: a stored one with a value gets it, one marked to remove goes, a new row with a name is added (its value
    /// may be empty). The problem of the first invalid one, or null.
    /// </summary>
    private (Dictionary<string, string> Set, List<string> Remove, string? Problem) SecretChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        var remove = new List<string>();
        foreach (var row in Input.Secrets)
        {
            var key = row.Key?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (row.Remove)
            {
                remove.Add(key);
                continue;
            }

            if (row.Existing && string.IsNullOrEmpty(row.Value))
            {
                continue; // unchanged
            }

            var value = row.Value ?? string.Empty;
            if (StackSecrets.Problem(key, value) is { } problem)
            {
                return (set, remove, problem);
            }

            set[key] = value;
        }

        return (set, remove, null);
    }

    /// <summary>
    /// The live check of the editor (<c>stack-forms.js</c>): the rules of <see cref="ComposeLint"/> for the text as it is typed, with the variables of the
    /// .env and the secrets, and the host ports the rest of the environment publishes (read at most every 30 seconds).
    /// </summary>
    public async Task<IActionResult> OnPostLintAsync(string? yaml, string? env, long? environmentId, long? id, string? secretKeys, string? source)
    {
        var defined = new HashSet<string>(StackVariables.ParseEnv(env).Keys, StringComparer.Ordinal);
        string? stackName = null;
        if (id is > 0 && await _stackService.GetAsync(id.Value, HttpContext.RequestAborted) is { } stack)
        {
            stackName = stack.Name;
            defined.UnionWith(StackSecrets.Names(stack.SecretsJson));
        }

        foreach (var key in (secretKeys ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            defined.Add(key);
        }

        var ports = environmentId is > 0 ? await OtherPortsAsync(environmentId.Value, stackName) : [];
        var findings = ComposeLint.Check(yaml, source == "git" ? null : defined, ports);
        return new JsonResult(new
        {
            findings = findings.Select(f => new
            {
                code = f.Code,
                severity = f.Severity.ToString().ToLowerInvariant(),
                message = f.Message,
                line = f.Line,
                fix = f.Fix,
                fixLabel = f.FixLabel,
            }),
        });
    }

    /// <summary>The logo of an app from the selfh.st collection as a data URL (<c>stack-forms.js</c> puts it into the icon field), 404 when there is none.</summary>
    public async Task<IActionResult> OnPostLogoAsync(string? name)
    {
        var icon = await _logos.FetchAsync(name ?? string.Empty, HttpContext.RequestAborted);
        return icon is null
            ? NotFound(new { error = $"No logo for '{name}' in the collection (names as on selfh.st/icons, e.g. nextcloud, uptime-kuma)." })
            : new JsonResult(new { icon });
    }

    /// <summary>Applies a fix of the live check to the text and answers the new text (400 when it does not fit any more).</summary>
    public IActionResult OnPostFix(string? yaml, string? fix)
    {
        if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(fix) || ComposeLint.ApplyFix(yaml, fix) is not { } fixedYaml)
        {
            return BadRequest(new { error = "The fix does not fit the text any more." });
        }

        return new JsonResult(new { yaml = fixedYaml });
    }

    /// <summary>The host ports the containers of an environment publish, except those of the stack itself.</summary>
    private async Task<IReadOnlyCollection<HostPortUse>> OtherPortsAsync(long environmentId, string? stackName)
    {
        var all = await _cache.GetOrCreateAsync($"lint:ports:{environmentId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var env = await _environmentService.GetAsync(environmentId, HttpContext.RequestAborted);
            if (env is not { IsEnabled: true })
            {
                return new List<(HostPortUse Use, string? Project)>();
            }

            try
            {
                var containers = await _containers.ListAsync(env, HttpContext.RequestAborted, includeStats: false);
                return containers.SelectMany(c => PublishedPorts(c.Ports).Select(p =>
                    (Use: new HostPortUse(p.Port, p.Protocol, string.IsNullOrEmpty(c.Project) ? $"container '{c.Name}'" : $"stack '{c.Project}'"), Project: c.Project))).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new List<(HostPortUse Use, string? Project)>();
            }
        }) ?? [];

        return all.Where(p => p.Project is null || p.Project != stackName).Select(p => p.Use).DistinctBy(p => (p.Port, p.Protocol)).ToList();
    }

    /// <summary>The host ports of a container's port column ("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp").</summary>
    internal static IEnumerable<(int Port, string Protocol)> PublishedPorts(string? ports)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ports ?? string.Empty, @":(\d+)->\d+(?:-\d+)?/(tcp|udp|sctp)"))
        {
            yield return (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value);
        }
    }

    /// <summary>Removes the stack from MatDock (as on the list); its containers keep running.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _stackService.DeleteAsync(id, HttpContext.RequestAborted);
        if (deleted)
        {
            await _gitSync.RemoveAsync(id, HttpContext.RequestAborted);
        }

        StatusMessage = deleted ? "Stack removed from MatDock." : "Stack not found.";
        IsError = !deleted;
        return RedirectToPage("Index");
    }

    /// <summary>Syncs a Git stack now (in the background, followed in the console): deploys only when the compose folder changed.</summary>
    public async Task<IActionResult> OnPostSyncNowAsync(long id)
    {
        var stack = await _stackService.GetAsync(id, HttpContext.RequestAborted);
        if (stack is not { IsGitBacked: true })
        {
            StatusMessage = "Only a Git stack syncs.";
            IsError = true;
            return RedirectToPage(new { id });
        }

        var actor = User.Identity?.Name;
        var operation = _operations.Start("sync", stack.Name, StackOperations.LockKey(stack.EnvironmentId, stack.Name), async ctx =>
        {
            ctx.Log($"==> Syncing {stack.Name} from {stack.GitRepoUrl}");
            var result = await ctx.Services.GetRequiredService<StackService>()
                .SyncGitAsync(id, new DeployRequest(Trigger: DeployTrigger.Sync, Actor: actor), ct: ctx.CancellationToken);
            ctx.Log(GitSyncAction.Describe(result));
            return result.Outcome == GitSyncOutcome.Failed ? OperationOutcome.Failure("The sync failed.") : OperationOutcome.Success();
        });
        return RedirectToPage(new { id, op = operation.Id });
    }

    private async Task LoadListsAsync()
    {
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        GitCredentials = await _gitCredentialService.GetAllAsync(HttpContext.RequestAborted);
        SavedRepos = await _gitRepoService.GetAllAsync(HttpContext.RequestAborted);
        Apps = await _templateService.GetAllAsync(HttpContext.RequestAborted);
        TagCatalog = await _tags.GetAllAsync(HttpContext.RequestAborted);
        if (Input.Id is > 0)
        {
            ManagedBy = (await _stackService.GetAsync(Input.Id.Value, HttpContext.RequestAborted))?.ManagedBy;
        }
    }
}
