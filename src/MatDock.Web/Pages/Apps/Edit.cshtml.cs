using System.ComponentModel.DataAnnotations;
using MatDock.Core.Templates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Apps;

public class EditModel : PageModel
{
    private readonly StackTemplateService _templateService;

    public EditModel(StackTemplateService templateService)
    {
        _templateService = templateService;
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

        [StringLength(100)]
        public string? Category { get; set; }

        [StringLength(500)]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Compose-YAML ist erforderlich.")]
        public string ComposeYaml { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var template = await _templateService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (template is null)
            {
                return RedirectToPage("Index");
            }

            Input = new InputModel
            {
                Id = template.Id,
                Name = template.Name,
                Category = template.Category,
                Description = template.Description,
                ComposeYaml = template.ComposeYaml,
            };
        }
        else if (Input.ComposeYaml.Length == 0)
        {
            Input.ComposeYaml = "services:\n  app:\n    image: nginx:alpine\n    restart: unless-stopped\n    ports:\n      - \"8080:80\"\n";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new StackTemplateInput
        {
            Name = Input.Name,
            Category = Input.Category,
            Description = Input.Description,
            ComposeYaml = Input.ComposeYaml,
        };

        if (IsEdit)
        {
            var (ok, message) = await _templateService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message);
                return Page();
            }
            StatusMessage = message;
        }
        else
        {
            var (ok, message, _) = await _templateService.CreateAsync(input, HttpContext.RequestAborted);
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
