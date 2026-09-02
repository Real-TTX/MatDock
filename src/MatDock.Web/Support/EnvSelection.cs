namespace MatDock.Web.Support;

/// <summary>
/// The globally-selected environment, chosen from the sidebar dropdown and persisted in a cookie so it
/// applies across every page. <c>null</c> means "all environments". A <c>?EnvId=</c> query parameter
/// (e.g. from a deep link) overrides and persists the selection.
/// </summary>
public static class EnvSelection
{
    public const string Cookie = "matdock.env";

    /// <summary>
    /// Resolves the selected environment id (null = all) and, when a <c>?EnvId=</c> query override is
    /// present, persists it to the cookie. Call this from page handlers (before the response is written).
    /// </summary>
    public static long? Resolve(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue("EnvId", out var q) && long.TryParse(q, out var qid))
        {
            Write(ctx, qid);
            return qid > 0 ? qid : null;
        }

        return FromCookie(ctx);
    }

    /// <summary>Read-only current selection (null = all) — does NOT write the cookie. For the layout dropdown.</summary>
    public static long? Current(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue("EnvId", out var q) && long.TryParse(q, out var qid))
        {
            return qid > 0 ? qid : null;
        }

        return FromCookie(ctx);
    }

    private static long? FromCookie(HttpContext ctx)
        => ctx.Request.Cookies.TryGetValue(Cookie, out var c) && long.TryParse(c, out var cid) && cid > 0 ? cid : null;

    private static void Write(HttpContext ctx, long id)
        => ctx.Response.Cookies.Append(Cookie, id > 0 ? id.ToString() : "all", new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        });
}
