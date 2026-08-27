using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitCredentials;

public class EditModel : PageModel
{
    private readonly GitCredentialService _service;

    public EditModel(GitCredentialService service)
    {
        _service = service;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    [TempData] public string? StatusMessage { get; set; }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name ist erforderlich.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Username { get; set; }

        public string? Token { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var cred = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (cred is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = cred.Id,
                Name = cred.Name,
                Username = cred.Username,
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

        var input = new GitCredentialInput
        {
            Name = Input.Name,
            AuthType = GitAuthType.HttpsToken,
            Username = Input.Username,
            Token = Input.Token,
        };

        if (IsEdit)
        {
            var (ok, message) = await _service.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
            StatusMessage = message;
        }
        else
        {
            var (ok, message, _) = await _service.CreateAsync(input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
            StatusMessage = message;
        }

        return RedirectToPage("Index");
    }
}
