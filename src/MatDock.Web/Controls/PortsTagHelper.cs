using System.Net;
using System.Text;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatDock.Web.Controls;

/// <summary>
/// Renders a container's Docker "Ports" string as compact compose-style tags. Published ports
/// (<c>0.0.0.0:8064-&gt;8080/tcp</c>) become <c>8064:8080</c> link-tags that open
/// <c>http://&lt;host&gt;:8064</c> in a new tab; exposed-only ports render as plain tags.
/// <code>&lt;container-ports ports="@c.Ports" host="@host" /&gt;</code>
/// </summary>
[HtmlTargetElement("container-ports")]
public sealed class PortsTagHelper : TagHelper
{
    public string? Ports { get; set; }

    /// <summary>Host/IP for the link (from <c>OpenUrls.PortLinkHost</c>); empty = render tags without links.</summary>
    public string? Host { get; set; }

    public string Scheme { get; set; } = "http";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        // The source element is written self-closing (<container-ports />); force a start+end tag so our
        // generated content is actually rendered instead of dropped.
        output.TagMode = TagMode.StartTagAndEndTag;
        var existing = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existing) ? "port-tags" : $"port-tags {existing}");

        var (published, exposed) = Parse(Ports);
        if (published.Count == 0 && exposed.Count == 0)
        {
            output.Content.SetHtmlContent("<span class=\"text-muted\">–</span>");
            return;
        }

        var sb = new StringBuilder();
        foreach (var (hostPort, containerPort) in published)
        {
            var label = WebUtility.HtmlEncode($"{hostPort}:{containerPort}");
            if (!string.IsNullOrEmpty(Host))
            {
                var url = WebUtility.HtmlEncode($"{Scheme}://{Host}:{hostPort}");
                sb.Append($"<a class=\"port-tag port-tag--link\" href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"Open {url}\">{label}</a>");
            }
            else
            {
                sb.Append($"<span class=\"port-tag\">{label}</span>");
            }
        }

        foreach (var cp in exposed)
        {
            sb.Append($"<span class=\"port-tag port-tag--muted\" title=\"exposed, not published\">{WebUtility.HtmlEncode(cp)}</span>");
        }

        output.Content.SetHtmlContent(sb.ToString());
    }

    private static (List<(string Host, string Container)> Published, List<string> Exposed) Parse(string? ports)
    {
        var published = new List<(string, string)>();
        var exposed = new List<string>();
        var seenPub = new HashSet<string>(StringComparer.Ordinal);
        var seenExp = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(ports))
        {
            return (published, exposed);
        }

        foreach (var raw in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = raw.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var left = raw[..arrow];                        // 0.0.0.0:8064  or  :::8064
                var right = raw[(arrow + 2)..];                 // 8080/tcp
                var hostPort = left.Contains(':') ? left[(left.LastIndexOf(':') + 1)..] : left;
                var containerPort = right.Split('/')[0].Trim();
                if (hostPort.Length > 0 && containerPort.Length > 0 && seenPub.Add($"{hostPort}:{containerPort}"))
                {
                    published.Add((hostPort, containerPort));
                }
            }
            else
            {
                var cp = raw.Split('/')[0].Trim();              // 8080  (exposed, not published)
                if (cp.Length > 0 && cp.All(char.IsDigit) && seenExp.Add(cp))
                {
                    exposed.Add(cp);
                }
            }
        }

        return (published, exposed);
    }
}
