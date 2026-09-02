using MatDock.Core.Entities;
using MatDock.Core.Users;
using MatDock.Web.Controls;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Users;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly UserService _userService;

    public IndexModel(UserService userService)
    {
        _userService = userService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "username";

    [BindProperty(SupportsGet = true)]
    public string Dir { get; set; } = "asc";

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public List<User> Items { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var all = await _userService.GetAllAsync(HttpContext.RequestAborted);
        IEnumerable<User> query = all;

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(u => u.Username.Contains(s, Ic) || u.DisplayName.Contains(s, Ic));
        }

        var descending = string.Equals(Dir, "desc", Ic);
        query = Sort.ToLowerInvariant() switch
        {
            "displayname" => Order(query, u => u.DisplayName, descending),
            "role" => Order(query, u => (int)u.Role, descending),
            "lastlogin" => Order(query, u => u.LastLoginAt ?? DateTime.MinValue, descending),
            _ => Order(query, u => u.Username, descending)
        };

        var list = query.ToList();
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = list.Count };
        Pagination.Normalize();
        Items = list.Skip((Pagination.Page - 1) * PageSize).Take(PageSize).ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (User.GetUserId() == id)
        {
            StatusMessage = "You cannot delete your own account.";
            IsError = true;
            return RedirectToPage();
        }

        var deleted = await _userService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "User deleted." : "User not found.";
        IsError = !deleted;
        return RedirectToPage();
    }

    private static IEnumerable<User> Order<TKey>(IEnumerable<User> source, Func<User, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
