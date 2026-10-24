using MatDock.Core.Entities;
using MatDock.Core.Updates;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Web.Api;

/// <summary>Body of <c>PUT /api/v1/update-rules/{scope}/{id}</c>: every value null = "as the wider rule says".</summary>
public sealed class UpdateRuleBody
{
    public UpdateMode? Mode { get; set; }
    public UpdateStrategy? Strategy { get; set; }
    public int? WindowStartHour { get; set; }
    public int? WindowEndHour { get; set; }
    public int? CheckEveryHours { get; set; }
    public bool? PreBackup { get; set; }
    public bool? Rollback { get; set; }
    public bool? NotifyAvailable { get; set; }
    public bool? NotifyResult { get; set; }

    /// <summary>For a stack rule: the strategy per service.</summary>
    public Dictionary<string, UpdateStrategy>? ServiceStrategies { get; set; }
}

/// <summary><c>/api/v1/update-rules</c>: the rules of the update management (global, per environment, per stack), read and set.</summary>
public static class UpdateRulesApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/update-rules", ListAsync).WithSummary("Every rule of the update management.");
        api.MapGet("/update-rules/{scope}/{id:long}", GetAsync).WithSummary("The rule of a scope (global/0, environment/{id}, stack/{id}); null values = as the wider rule says.");
        api.MapPut("/update-rules/{scope}/{id:long}", PutAsync).WithSummary("Sets the rule of a scope; a rule without values falls back to the wider one.");
    }

    private static object View(UpdatePolicy p) => new
    {
        scope = p.Scope,
        id = p.ScopeId,
        mode = p.Mode,
        strategy = p.Strategy,
        p.WindowStartHour,
        p.WindowEndHour,
        p.CheckEveryHours,
        p.PreBackup,
        p.Rollback,
        p.NotifyAvailable,
        p.NotifyResult,
        serviceStrategies = UpdatePolicies.ServiceStrategies(p.ServiceStrategiesJson),
    };

    private static async Task<IResult> ListAsync(HttpContext http, Core.Data.MatDockDbContext db)
        => ApiResults.Ok((await db.UpdatePolicies.AsNoTracking().ToListAsync(http.RequestAborted)).OrderBy(p => p.Scope).ThenBy(p => p.ScopeId).Select(View));

    private static async Task<IResult> GetAsync(HttpContext http, string scope, long id, UpdatePolicyService rules)
    {
        if (ScopeOf(scope, id) is not { } s)
        {
            return ApiResults.BadRequest("The scope is global (id 0), environment or stack.");
        }

        var rule = await rules.GetAsync(s, id, http.RequestAborted) ?? new UpdatePolicy { Scope = s, ScopeId = id };
        return ApiResults.Ok(View(rule));
    }

    private static async Task<IResult> PutAsync(HttpContext http, string scope, long id, UpdatePolicyService rules)
    {
        if (ScopeOf(scope, id) is not { } s)
        {
            return ApiResults.BadRequest("The scope is global (id 0), environment or stack.");
        }

        var (body, error) = await ApiResults.ReadBodyAsync<UpdateRuleBody>(http.Request);
        if (error is not null)
        {
            return error;
        }

        body ??= new UpdateRuleBody(); // no body: the rule falls back to the wider one

        var (ok, message) = await rules.SaveAsync(new UpdatePolicy
        {
            Scope = s, ScopeId = id, Mode = body.Mode, Strategy = body.Strategy, WindowStartHour = body.WindowStartHour, WindowEndHour = body.WindowEndHour,
            CheckEveryHours = body.CheckEveryHours, PreBackup = body.PreBackup, Rollback = body.Rollback, NotifyAvailable = body.NotifyAvailable, NotifyResult = body.NotifyResult,
            ServiceStrategiesJson = body.ServiceStrategies is { Count: > 0 } map && s == UpdatePolicyScope.Stack ? UpdatePolicies.SerializeServiceStrategies(map) : null,
        }, http.RequestAborted);
        return ok ? ApiResults.Ok(new { ok, message }) : ApiResults.BadRequest(message);
    }

    private static UpdatePolicyScope? ScopeOf(string scope, long id) => scope.ToLowerInvariant() switch
    {
        "global" when id == 0 => UpdatePolicyScope.Global,
        "environment" when id > 0 => UpdatePolicyScope.Environment,
        "stack" when id > 0 => UpdatePolicyScope.Stack,
        _ => null,
    };
}
