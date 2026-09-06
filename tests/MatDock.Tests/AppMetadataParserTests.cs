using MatDock.Core.Apps;
using Xunit;

namespace MatDock.Tests;

public class AppMetadataParserTests
{
    private const string Compose = """
        x-matdock:
          name: Jellyfin
          icon: icon.svg
          description: Media server
          category: Media
          actions:
            - name: Admin
              url: /web/index.html
              icon: settings
            - name: Docs
              url: https://jellyfin.org
        services:
          app:
            image: jellyfin/jellyfin
            ports:
              - "8096:8096"
        """;

    [Fact]
    public void FromCompose_reads_x_matdock_block()
    {
        var m = AppMetadataParser.FromCompose(Compose);

        Assert.NotNull(m);
        Assert.Equal("Jellyfin", m!.Name);
        Assert.Equal("icon.svg", m.Icon);
        Assert.Equal("Media", m.Category);
        Assert.Equal(2, m.Actions.Count);
        Assert.Equal("Admin", m.Actions[0].Name);
        Assert.Equal("/web/index.html", m.Actions[0].Url);
        Assert.Equal("https://jellyfin.org", m.Actions[1].Url);
    }

    [Fact]
    public void FromCompose_reads_primary_url()
    {
        var m = AppMetadataParser.FromCompose("x-matdock:\n  name: A\n  url: /admin\nservices:\n  a:\n    image: nginx\n");
        Assert.NotNull(m);
        Assert.Equal("/admin", m!.Url);
    }

    [Fact]
    public void FromCompose_returns_null_without_x_matdock()
        => Assert.Null(AppMetadataParser.FromCompose("services:\n  a:\n    image: nginx\n"));

    [Fact]
    public void FromCompose_never_throws_on_garbage()
        => Assert.Null(AppMetadataParser.FromCompose("\t not : valid : yaml : ["));

    [Fact]
    public void Sidecar_overrides_compose_on_merge()
    {
        var baseMeta = AppMetadataParser.FromCompose(Compose);
        var sidecar = AppMetadataParser.FromSidecar("name: Renamed\nicon: rocket\n", "matdock.yml");

        var merged = AppMetadataParser.Merge(baseMeta, sidecar);

        Assert.NotNull(merged);
        Assert.Equal("Renamed", merged!.Name);   // sidecar wins
        Assert.Equal("rocket", merged.Icon);
        Assert.Equal("Media", merged.Category);   // falls back to compose
        Assert.Equal(2, merged.Actions.Count);    // compose actions kept (sidecar had none)
    }

    [Fact]
    public void FromSidecar_parses_json()
    {
        var m = AppMetadataParser.FromSidecar(
            "{\"name\":\"J\",\"actions\":[{\"name\":\"Docs\",\"url\":\"https://x\"}]}", "matdock.json");

        Assert.NotNull(m);
        Assert.Equal("J", m!.Name);
        Assert.Single(m.Actions);
        Assert.Equal("https://x", m.Actions[0].Url);
    }

    [Fact]
    public void Clean_drops_actions_missing_name_or_url()
    {
        var m = AppMetadataParser.FromSidecar(
            "name: X\nactions:\n  - { name: OK, url: /ok }\n  - { name: NoUrl }\n  - { url: /noname }\n", "matdock.yml");

        Assert.NotNull(m);
        Assert.Single(m!.Actions);
        Assert.Equal("OK", m.Actions[0].Name);
    }

    [Fact]
    public void Json_roundtrips()
    {
        var m = AppMetadataParser.FromCompose(Compose)!;
        var back = AppMetadataParser.FromJson(AppMetadataParser.ToJson(m));

        Assert.NotNull(back);
        Assert.Equal(m.Name, back!.Name);
        Assert.Equal(m.Actions.Count, back.Actions.Count);
    }
}
