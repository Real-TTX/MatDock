// MatPlatform 1.5.0 – a copy from MatDesign, do not edit here
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MatPlatform;

/// <summary>
/// Who may show the app in a frame: the origins in <c>MAT_FRAME_ANCESTORS</c> (docs/PLATFORM.md, "Shown inside another app"). Mattiverse sets it to its own
/// addresses, so it can open the app in the window of its home screen. Unset or empty, nothing changes. Each entry must be an origin
/// (<c>https://mattiverse.example.com</c>, <c>http://192.168.1.5:8030</c>), separated by spaces or commas; anything else (a path, a wildcard, <c>*</c>, a keyword)
/// is left out, so a mistake never opens the app to every site. With origins, every answer carries <c>Content-Security-Policy: frame-ancestors 'self' &lt;origins&gt;</c>
/// and no <c>X-Frame-Options</c> (it knows no list of origins; also not the <c>SAMEORIGIN</c> that ASP.NET Core's anti-forgery tokens add).
/// <c>AddMatPlatform</c> registers it; an app without it calls <see cref="AddMatFrameAncestors"/>.
/// </summary>
public static class MatFrameAncestors
{
    public const string Variable = "MAT_FRAME_ANCESTORS";

    /// <summary>
    /// Registers the headers of <c>MAT_FRAME_ANCESTORS</c> for every answer of the app: an <see cref="IStartupFilter"/> at the very start of the pipeline, so its
    /// <c>Response.OnStarting</c> runs after the ones of the app's own middleware (they run last registered first) and has the last word. Without origins it adds nothing.
    /// Idempotent.
    /// </summary>
    public static IServiceCollection AddMatFrameAncestors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, MatFrameAncestorsStartupFilter>());
        return services;
    }

    /// <summary>The origins of <c>MAT_FRAME_ANCESTORS</c> in the configuration (the environment).</summary>
    public static IReadOnlyList<string> From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Variable]);
    }

    /// <summary>The origins in a value of <c>MAT_FRAME_ANCESTORS</c>, each as <c>scheme://host[:port]</c> (lower case, no default port), without repeats.</summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        var origins = new List<string>();
        foreach (var token in (value ?? string.Empty).Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains('*', StringComparison.Ordinal) || token.Contains('\'', StringComparison.Ordinal)
                || !Uri.TryCreate(token, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                continue;
            }

            var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost; // an IPv6 address keeps its brackets
            var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                origins.Add(origin);
            }
        }

        return origins;
    }

    /// <summary>A content security policy whose <c>frame-ancestors</c> is the app itself and <paramref name="origins"/> (an existing one is replaced, the rest stays).</summary>
    public static string Apply(string? policy, IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var directive = "frame-ancestors 'self'" + string.Concat(origins.Select(o => " " + o));
        var parts = (policy ?? string.Empty).Split(';')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add(directive);
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The headers of an answer that <paramref name="origins"/> may frame: no <c>X-Frame-Options</c>, <c>frame-ancestors</c> in the content security policy.
    /// Without origins the headers stay as they are.
    /// </summary>
    public static void ApplyTo(IHeaderDictionary headers, IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(origins);

        if (origins.Count == 0)
        {
            return;
        }

        headers.Remove("X-Frame-Options");
        headers.ContentSecurityPolicy = Apply(headers.ContentSecurityPolicy.ToString(), origins);
    }
}

/// <summary>Puts <see cref="MatFrameAncestors.ApplyTo"/> into <c>Response.OnStarting</c> of every request, at the very start of the pipeline; nothing without origins.</summary>
internal sealed class MatFrameAncestorsStartupFilter(IConfiguration configuration, ILoggerFactory loggers) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            var origins = MatFrameAncestors.From(configuration);
            var logger = loggers.CreateLogger("MatPlatform.MatFrameAncestors");
            if (origins.Count > 0)
            {
                logger.LogInformation("{Variable}: {Origins} may show the app in a frame", MatFrameAncestors.Variable, string.Join(' ', origins));
                app.Use((context, nextMiddleware) =>
                {
                    var response = context.Response;
                    response.OnStarting(() =>
                    {
                        MatFrameAncestors.ApplyTo(response.Headers, origins);
                        return Task.CompletedTask;
                    });
                    return nextMiddleware(context);
                });
            }
            else if (!string.IsNullOrWhiteSpace(configuration[MatFrameAncestors.Variable]))
            {
                logger.LogWarning("{Variable} names no origin (scheme://host[:port]); no other site may show the app in a frame", MatFrameAncestors.Variable);
            }

            next(app);
        };
}
