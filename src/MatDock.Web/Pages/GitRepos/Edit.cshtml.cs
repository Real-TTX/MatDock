using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.GitRepos;

public class EditModel : PageModel
{
    private readonly GitRepoService _service;
    private readonly GitCredentialService _credentials;

    public EditModel(GitRepoService service, GitCredentialService credentials)
    {
        _service = service;
        _credentials = credentials;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<GitCredential> Credentials { get; private set; } = new();
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

        [Required(ErrorMessage = "Repository URL is required.")]
        [StringLength(1000)]
        public string Url { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Reference { get; set; }

        public long? GitCredentialId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadListsAsync();

        if (id is > 0)
        {
            var repo = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (repo is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = repo.Id,
                Name = repo.Name,
                Url = repo.Url,
                Reference = repo.Reference,
                GitCredentialId = repo.GitCredentialId,
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadListsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new GitRepoInput
        {
            Name = Input.Name,
            Url = Input.Url,
            Reference = Input.Reference,
            GitCredentialId = Input.GitCredentialId,
        };

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

        StatusMessage = "Git repository saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        await LoadListsAsync();
        var (ok, message) = await _service.TestAsync(Input.Url, Input.Reference, Input.GitCredentialId, HttpContext.RequestAborted);
        TestOk = ok;
        TestMessage = message;
        return Page();
    }

    private async Task LoadListsAsync()
        => Credentials = await _credentials.GetAllAsync(HttpContext.RequestAborted);
}
