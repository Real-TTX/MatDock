using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Stacks;
using MatDock.Core.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks.SyncJobs;

public class EditModel : PageModel
{
    private readonly SyncJobService _syncJobs;
    private readonly EnvironmentService _environments;
    private readonly GitCredentialService _gitCredentials;
    private readonly GitRepoService _gitRepos;

    public EditModel(SyncJobService syncJobs, EnvironmentService environments, GitCredentialService gitCredentials, GitRepoService gitRepos)
    {
        _syncJobs = syncJobs;
        _environments = environments;
        _gitCredentials = gitCredentials;
        _gitRepos = gitRepos;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<GitCredential> Credentials { get; private set; } = new();
    public List<GitRepo> SavedRepos { get; private set; } = new();

    public bool IsEdit => Input.Id > 0;
    [TempData] public string? StatusMessage { get; set; }
    public string? ScanError { get; private set; }
    public int ScannedCount { get; private set; } = -1;
    public string? WebhookUrl { get; private set; }

    public class InputModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string GitRepoUrl { get; set; } = string.Empty;
        public string? GitReference { get; set; }
        public string? Subdirectory { get; set; }
        public long? GitCredentialId { get; set; }
        public string? Cron { get; set; }
        /// <summary>Trigger toggle: run on the cron schedule.</summary>
        public bool ScheduleEnabled { get; set; }
        /// <summary>Trigger toggle: allow the webhook URL to start a sync.</summary>
        public bool WebhookEnabled { get; set; } = true;
        /// <summary>Toggle: on = only deploy when the repo changed; off = always deploy on every trigger.</summary>
        public bool OnlyOnChange { get; set; } = true;
        public bool PullImages { get; set; }
        public bool PruneRemoved { get; set; }
        public List<RowInput> Rows { get; set; } = new();
    }

    public class RowInput
    {
        public string ComposePath { get; set; } = string.Empty;
        public string StackName { get; set; } = string.Empty;
        public List<long> EnvironmentIds { get; set; } = new();

        /// <summary>Display only: whether this compose is still present in the repo after the last scan.</summary>
        public bool InRepo { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        await LoadListsAsync();

        if (id > 0)
        {
            var job = await _syncJobs.GetAsync(id, HttpContext.RequestAborted);
            if (job is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = job.Id,
                Name = job.Name,
                GitRepoUrl = job.GitRepoUrl,
                GitReference = job.GitReference,
                Subdirectory = job.Subdirectory,
                GitCredentialId = job.GitCredentialId,
                Cron = job.Cron,
                ScheduleEnabled = job.ScheduleEnabled,
                WebhookEnabled = job.WebhookEnabled,
                OnlyOnChange = job.UpdateMode == SyncUpdateMode.OnGitChange,
                PullImages = job.PullImages,
                PruneRemoved = job.PruneRemoved,
                Rows = await BuildRowsFromItemsAsync(job.Id),
            };
            WebhookUrl = BuildWebhookUrl(job.WebhookToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostScanAsync()
    {
        await LoadListsAsync();
        await RefreshWebhookAsync();

        var scan = await _syncJobs.ScanAsync(Input.GitRepoUrl, Input.GitReference, Input.GitCredentialId, Input.Subdirectory, HttpContext.RequestAborted);
        if (!scan.Ok)
        {
            ScanError = scan.Error;
            return Page();
        }

        ScannedCount = scan.ComposeFiles.Count;

        // Merge discovered compose files with the current (posted) selections.
        var byPath = Input.Rows
            .GroupBy(r => r.ComposePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var merged = new List<RowInput>();
        var discovered = new HashSet<string>(scan.ComposeFiles, StringComparer.Ordinal);
        foreach (var path in scan.ComposeFiles)
        {
            if (byPath.TryGetValue(path, out var existing))
            {
                existing.InRepo = true;
                merged.Add(existing);
            }
            else
            {
                merged.Add(new RowInput { ComposePath = path, StackName = DeriveName(path), InRepo = true });
            }
        }

        // Keep already-configured composes that are no longer in the repo (flagged).
        foreach (var row in Input.Rows.Where(r => !discovered.Contains(r.ComposePath)))
        {
            row.InRepo = false;
            merged.Add(row);
        }

        Input.Rows = merged.OrderBy(r => r.ComposePath, StringComparer.OrdinalIgnoreCase).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadListsAsync();
        await RefreshWebhookAsync();

        var input = new SyncJobInput
        {
            Id = Input.Id,
            Name = Input.Name,
            GitRepoUrl = Input.GitRepoUrl,
            GitReference = Input.GitReference,
            Subdirectory = Input.Subdirectory,
            GitCredentialId = Input.GitCredentialId,
            Cron = Input.Cron,
            ScheduleEnabled = Input.ScheduleEnabled,
            WebhookEnabled = Input.WebhookEnabled,
            UpdateMode = Input.OnlyOnChange ? SyncUpdateMode.OnGitChange : SyncUpdateMode.Always,
            PullImages = Input.PullImages,
            PruneRemoved = Input.PruneRemoved,
            Items = Flatten(Input.Rows),
        };

        if (Input.Id > 0)
        {
            var (ok, message) = await _syncJobs.UpdateAsync(Input.Id, input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
        }
        else
        {
            var (ok, message, _) = await _syncJobs.CreateAsync(input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
        }

        TempData["StatusMessage"] = Input.Id > 0 ? "Sync job saved." : "Sync job created.";
        return RedirectToPage("/Stacks/SyncJobs/Index");
    }

    public async Task<IActionResult> OnPostRegenerateAsync(long id)
    {
        await _syncJobs.RegenerateTokenAsync(id, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRunNowAsync(long id)
    {
        var summary = await _syncJobs.RunNowAsync(id, force: true, HttpContext.RequestAborted);
        TempData["StatusMessage"] = $"Sync: {summary}";
        return RedirectToPage(new { id });
    }

    private static List<SyncItemInput> Flatten(List<RowInput> rows)
    {
        var items = new List<SyncItemInput>();
        foreach (var row in rows)
        {
            foreach (var envId in row.EnvironmentIds.Where(e => e > 0).Distinct())
            {
                items.Add(new SyncItemInput
                {
                    ComposePath = row.ComposePath,
                    EnvironmentId = envId,
                    StackName = row.StackName,
                    Enabled = true,
                });
            }
        }
        return items;
    }

    private async Task<List<RowInput>> BuildRowsFromItemsAsync(long jobId)
    {
        var items = await _syncJobs.GetItemsAsync(jobId, HttpContext.RequestAborted);
        return items
            .GroupBy(i => i.ComposePath, StringComparer.Ordinal)
            .Select(g => new RowInput
            {
                ComposePath = g.Key,
                StackName = g.First().StackName,
                EnvironmentIds = g.Where(i => i.Enabled).Select(i => i.EnvironmentId).Distinct().ToList(),
            })
            .OrderBy(r => r.ComposePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Generic compose file stems that carry no meaning as a stack name — fall back to the folder instead.
    private static readonly HashSet<string> GenericComposeNames =
        new(StringComparer.OrdinalIgnoreCase) { "docker", "docker-compose", "compose" };

    private string DeriveName(string composePath)
    {
        var segments = composePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileStem = segments.Length > 0 ? Path.GetFileNameWithoutExtension(segments[^1]) : null;
        var parentDir = segments.Length > 1 ? segments[^2] : null;

        // Normally use the file name; if it's a generic compose name, take the parent directory instead;
        // at the repo root (no parent) fall back to the repository name.
        string? basis = fileStem;
        if (string.IsNullOrEmpty(basis) || GenericComposeNames.Contains(basis))
        {
            basis = !string.IsNullOrEmpty(parentDir) ? parentDir : RepoName(Input.GitRepoUrl);
        }

        return StackCommands.Slugify(basis);
    }

    /// <summary>The repository name from a Git URL (e.g. .../org/<c>repo</c>.git → "repo").</summary>
    private static string? RepoName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            u = u[..^4];
        }

        var slash = u.LastIndexOf('/');
        return slash >= 0 && slash < u.Length - 1 ? u[(slash + 1)..] : u;
    }

    private string BuildWebhookUrl(string token)
        => $"{Request.Scheme}://{Request.Host}/webhooks/sync/{token}";

    private async Task RefreshWebhookAsync()
    {
        if (Input.Id > 0)
        {
            var job = await _syncJobs.GetAsync(Input.Id, HttpContext.RequestAborted);
            if (job is not null)
            {
                WebhookUrl = BuildWebhookUrl(job.WebhookToken);
            }
        }
    }

    private async Task LoadListsAsync()
    {
        Environments = await _environments.GetEnabledAsync(HttpContext.RequestAborted);
        Credentials = await _gitCredentials.GetAllAsync(HttpContext.RequestAborted);
        SavedRepos = await _gitRepos.GetAllAsync(HttpContext.RequestAborted);
    }
}
