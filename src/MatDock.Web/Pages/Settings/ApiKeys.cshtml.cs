using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Pages.Settings;

/// <summary>The named keys of the API: made (the key is shown once), listed with their last use, revoked.</summary>
public class ApiKeysModel : PageModel
{
    private readonly ApiKeyService _keys;
    private readonly IOptions<MatDockOptions> _options;

    public ApiKeysModel(ApiKeyService keys, IOptions<MatDockOptions> options)
    {
        _keys = keys;
        _options = options;
    }

    public IReadOnlyList<ApiKey> Keys { get; private set; } = Array.Empty<ApiKey>();

    /// <summary>The engine key of the configuration is set (it may do everything and is not listed).</summary>
    public bool HasEngineKey => !string.IsNullOrWhiteSpace(_options.Value.Api.Key);

    /// <summary>A key that was just made: shown once, on the page that answers the POST (never stored, never in TempData).</summary>
    public string? NewKey { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync() => Keys = await _keys.GetAllAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostCreateAsync(string? name, ApiKeyScope scope, int? days)
    {
        var expires = days is > 0 and <= 3650 ? DateTime.UtcNow.AddDays(days.Value) : (DateTime?)null;
        var (ok, message, key) = await _keys.CreateAsync(name, scope, expires, HttpContext.RequestAborted);
        if (!ok)
        {
            StatusMessage = message;
            IsError = true;
            return RedirectToPage();
        }

        // Answered here directly: a redirect would have to carry the key through a cookie.
        NewKey = key;
        Keys = await _keys.GetAllAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        var ok = await _keys.RevokeAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Key revoked: it no longer works." : "Key not found.";
        IsError = !ok;
        return RedirectToPage();
    }
}
