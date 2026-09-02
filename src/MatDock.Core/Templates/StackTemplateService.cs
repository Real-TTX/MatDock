using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Templates;

/// <summary>CRUD for app templates (reusable compose blueprints). No remote access — installing a
/// template is done by pre-filling the Stack editor, which owns all deploy logic.</summary>
public sealed class StackTemplateService
{
    private readonly MatDockDbContext _db;

    public StackTemplateService(MatDockDbContext db)
    {
        _db = db;
    }

    public Task<List<StackTemplate>> GetAllAsync(CancellationToken ct = default)
        => _db.StackTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public Task<StackTemplate?> GetAsync(long id, CancellationToken ct = default)
        => _db.StackTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(StackTemplateInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return (false, "Name is required.", 0);
        }

        if (string.IsNullOrWhiteSpace(input.ComposeYaml))
        {
            return (false, "Compose YAML is required.", 0);
        }

        var template = new StackTemplate();
        Apply(template, input);
        _db.StackTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return (true, "App created.", template.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, StackTemplateInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return (false, "Name is required.");
        }

        if (string.IsNullOrWhiteSpace(input.ComposeYaml))
        {
            return (false, "Compose YAML is required.");
        }

        var template = await _db.StackTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null)
        {
            return (false, "App not found.");
        }

        Apply(template, input);
        await _db.SaveChangesAsync(ct);
        return (true, "App saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var template = await _db.StackTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null)
        {
            return false;
        }

        _db.StackTemplates.Remove(template); // soft delete via SaveChanges override
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void Apply(StackTemplate template, StackTemplateInput input)
    {
        template.Name = input.Name.Trim();
        template.Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim();
        template.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        template.IconUrl = string.IsNullOrWhiteSpace(input.IconUrl) ? null : input.IconUrl.Trim();
        template.ComposeYaml = input.ComposeYaml ?? string.Empty;
    }
}
