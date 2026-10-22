using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Updates;

/// <summary>The update rule of one scope: the default for all stacks, the stacks of an environment, or one stack. A value left on "as the wider rule says" is not set here.</summary>
public class RulesModel : PageModel
{
    private readonly UpdatePolicyService _policies;
    private readonly EnvironmentService _environments;
    private readonly StackService _stacks;

    public RulesModel(UpdatePolicyService policies, EnvironmentService environments, StackService stacks)
    {
        _policies = policies;
        _environments = environments;
        _stacks = stacks;
    }

    [BindProperty(SupportsGet = true)] public UpdatePolicyScope Scope { get; set; }
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public string Title { get; private set; } = "Default rule";

    /// <summary>What the rule falls back to where it sets nothing.</summary>
    public EffectiveUpdatePolicy Inherited { get; private set; } = EffectiveUpdatePolicy.Default;

    /// <summary>For a stack: its services with the image the compose file names (a strategy can be chosen for each).</summary>
    public IReadOnlyDictionary<string, string> Services { get; private set; } = new Dictionary<string, string>();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        // "" = as the wider rule says
        public string? Mode { get; set; }
        public string? Strategy { get; set; }
        public string? WindowStartHour { get; set; }
        public string? WindowEndHour { get; set; }
        public int? CheckEveryHours { get; set; }
        public string? PreBackup { get; set; }
        public string? Rollback { get; set; }
        public string? NotifyAvailable { get; set; }
        public string? NotifyResult { get; set; }
        public Dictionary<string, string?> ServiceStrategies { get; set; } = new();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var row = await _policies.GetAsync(Scope, ScopeId);
        if (row is not null)
        {
            Input = new InputModel
            {
                Mode = row.Mode?.ToString(),
                Strategy = row.Strategy?.ToString(),
                WindowStartHour = row.WindowStartHour?.ToString(),
                WindowEndHour = row.WindowEndHour?.ToString(),
                CheckEveryHours = row.CheckEveryHours,
                PreBackup = Flag(row.PreBackup),
                Rollback = Flag(row.Rollback),
                NotifyAvailable = Flag(row.NotifyAvailable),
                NotifyResult = Flag(row.NotifyResult),
                ServiceStrategies = UpdatePolicies.ServiceStrategies(row.ServiceStrategiesJson).ToDictionary(s => s.Key, s => (string?)s.Value.ToString()),
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var rule = new UpdatePolicy
        {
            Scope = Scope,
            ScopeId = ScopeId,
            Mode = Enum<UpdateMode>(Input.Mode),
            Strategy = Enum<UpdateStrategy>(Input.Strategy),
            WindowStartHour = Hour(Input.WindowStartHour),
            WindowEndHour = Hour(Input.WindowEndHour),
            CheckEveryHours = Input.CheckEveryHours,
            PreBackup = Bool(Input.PreBackup),
            Rollback = Bool(Input.Rollback),
            NotifyAvailable = Bool(Input.NotifyAvailable),
            NotifyResult = Bool(Input.NotifyResult),
        };
        if (Scope == UpdatePolicyScope.Stack)
        {
            var chosen = Input.ServiceStrategies
                .Where(s => Services.ContainsKey(s.Key) && Enum<UpdateStrategy>(s.Value) is not null)
                .ToDictionary(s => s.Key, s => Enum<UpdateStrategy>(s.Value)!.Value);
            rule.ServiceStrategiesJson = UpdatePolicies.SerializeServiceStrategies(chosen);
        }

        var (ok, message) = await _policies.SaveAsync(rule, HttpContext.RequestAborted);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, message);
            return Page();
        }

        StatusMessage = message;
        IsError = false;
        return RedirectToPage("Index");
    }

    private long ScopeId => Scope == UpdatePolicyScope.Global ? 0 : Id;

    /// <summary>Finds what the scope is (its name, what it inherits, the services of a stack); false when the environment or stack does not exist.</summary>
    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var set = await _policies.LoadAsync(ct);
        switch (Scope)
        {
            case UpdatePolicyScope.Global:
                Title = "Default rule";
                Inherited = set.Inherited(UpdatePolicyScope.Global, 0);
                return true;

            case UpdatePolicyScope.Environment:
                var env = await _environments.GetAsync(Id, ct);
                if (env is null)
                {
                    return false;
                }

                Title = $"Rule of {env.Name}";
                Inherited = set.Inherited(UpdatePolicyScope.Environment, env.Id);
                return true;

            default:
                var stack = await _stacks.GetAsync(Id, ct);
                if (stack is null || await _environments.GetAsync(stack.EnvironmentId, ct) is null)
                {
                    return false;
                }

                Title = $"Rule of {stack.Name}";
                Inherited = set.Inherited(UpdatePolicyScope.Stack, stack.EnvironmentId);
                Services = ImageOverrides.ComposeImages(stack.ComposeYaml);
                return true;
        }
    }

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "true" : "false";

    private static bool? Bool(string? text) => text switch { "true" => true, "false" => false, _ => null };

    private static int? Hour(string? text) => int.TryParse(text, out var h) ? h : null;

    private static T? Enum<T>(string? text) where T : struct, Enum
        => System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : null;
}
