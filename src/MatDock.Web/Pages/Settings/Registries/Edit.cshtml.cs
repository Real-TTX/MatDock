using System.ComponentModel.DataAnnotations;
using MatDock.Core.Registries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Registries;

public class EditModel : PageModel
{
    private readonly RegistryService _service;

    public EditModel(RegistryService service) => _service = service;

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

        [Required(ErrorMessage = "Host is required.")]
        [StringLength(255)]
        public string Host { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Username { get; set; }

        public string? Token { get; set; }

        public bool Insecure { get; set; }

        public bool Enabled { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var r = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (r is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = r.Id,
                Name = r.Name,
                Host = r.Host,
                Username = r.Username,
                Insecure = r.Insecure,
                Enabled = r.Enabled,
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

        StatusMessage = "Registry saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var result = await _service.TestInputAsync(ToInput(), Input.Id, HttpContext.RequestAborted);
        TestOk = result.Ok;
        TestMessage = result.Message;
        return Page();
    }

    private RegistryInput ToInput() => new()
    {
        Id = Input.Id ?? 0,
        Name = Input.Name,
        Host = Input.Host,
        Username = Input.Username ?? string.Empty,
        Token = Input.Token,
        Insecure = Input.Insecure,
        Enabled = Input.Enabled,
    };
}
