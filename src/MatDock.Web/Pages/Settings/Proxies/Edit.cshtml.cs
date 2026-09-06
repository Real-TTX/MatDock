using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Proxies;

public class EditModel : PageModel
{
    private readonly ProxyConnectionService _service;

    public EditModel(ProxyConnectionService service) => _service = service;

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public string? TestMessage { get; private set; }
    public bool TestOk { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public ProxyProviderType Provider { get; set; } = ProxyProviderType.Caddy;

        [Required(ErrorMessage = "URL is required.")]
        [StringLength(1000)]
        public string Url { get; set; } = string.Empty;

        [StringLength(200)]
        public string? ServerName { get; set; }

        public string? Token { get; set; }

        public bool Enabled { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var c = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (c is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = c.Id,
                Name = c.Name,
                Provider = c.Provider,
                Url = c.Url,
                ServerName = c.ServerName,
                Enabled = c.Enabled,
                // Token intentionally not surfaced.
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = ToInput();
        if (IsEdit)
        {
            var (ok, message) = await _service.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
        }
        else
        {
            var (ok, message, _) = await _service.CreateAsync(input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
        }

        StatusMessage = "Proxy connection saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var result = await _service.TestInputAsync(ToInput(), Input.Id, HttpContext.RequestAborted);
        TestOk = result.Ok;
        TestMessage = result.Message;
        return Page();
    }

    private ProxyInput ToInput() => new()
    {
        Name = Input.Name,
        Provider = Input.Provider,
        Url = Input.Url,
        ServerName = Input.ServerName,
        Token = Input.Token,
        Enabled = Input.Enabled,
    };
}
