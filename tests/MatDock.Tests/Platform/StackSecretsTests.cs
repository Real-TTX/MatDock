using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatDock.Core.Stacks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// Secret variables of a stack (encrypted, never written to the host, handed to compose as environment variables), the variables a compose file
/// uses without a value, the env file of a Git stack, and the rules of the editor's live check with their fixes.
/// </summary>
public sealed class StackSecretsTests : IClassFixture<StackSecretsTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("secrets-host");
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public StackSecretsTests(Site site) => _site = site;

    private async Task<(HttpClient Browser, string Html)> AdminAsync(string page)
    {
        var browser = _site.App.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        return (browser, await (await browser.GetAsync(page)).Content.ReadAsStringAsync());
    }

    private long CreateStack(string name, string compose = "services:\n  web:\n    image: nginx:1.27\n    restart: unless-stopped\n")
    {
        using var scope = _site.App.Services.CreateScope();
        var (ok, message, id) = scope.ServiceProvider.GetRequiredService<StackService>()
            .CreateAsync(new StackInput { Name = name, EnvironmentId = _site.EnvId, ComposeYaml = compose }).GetAwaiter().GetResult();
        Assert.True(ok, message);
        return id;
    }

    private async Task<(bool Ok, string Message)> SetSecretsAsync(long id, Dictionary<string, string> set, params string[] remove)
    {
        using var scope = _site.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StackService>().SetSecretsAsync(id, set, remove);
    }

    // ---- secrets and variables ----

    [Fact]
    public void A_secret_is_one_line_with_a_valid_name()
    {
        Assert.Null(StackSecrets.Problem("DB_PASSWORD", "p@ss w0rd$*'\""));
        Assert.NotNull(StackSecrets.Problem("1ABC", "x"));
        Assert.NotNull(StackSecrets.Problem("A-B", "x"));
        Assert.NotNull(StackSecrets.Problem("KEY", "two\nlines"));

        var exports = StackSecrets.Exports("{\"B\":\"2\",\"A\":\"secret\"}", "A=from-env\nC=3");
        Assert.Equal("A=secret\nB=2\nC=3\n", exports); // the secret wins over the variable of the same name
        Assert.Null(StackSecrets.Exports(null));
        Assert.Equal(new[] { "A", "B" }, StackSecrets.Names("{\"B\":\"2\",\"A\":\"1\"}"));
    }

    [Fact]
    public void Variables_without_a_value_are_found_and_defaults_count_as_values()
    {
        const string compose = "# ${COMMENTED}\nservices:\n  db:\n    image: postgres:${PG_VERSION:-17}\n    environment:\n"
                               + "      POSTGRES_PASSWORD: ${DB_PASSWORD:?set it}\n      USER: $DB_USER\n      ESCAPED: $$NOT_A_VARIABLE\n"
                               + "      PROJECT: ${COMPOSE_PROJECT_NAME}\n      TZ: ${TZ}\n";
        var missing = StackVariables.Missing(compose, ["TZ"]);
        Assert.Equal(new[] { "DB_PASSWORD", "DB_USER" }, missing.Select(m => m.Name));
        Assert.True(missing[0].Required);
        Assert.Equal(6, missing[0].Line);
        Assert.False(missing[1].Required);

        var env = StackVariables.ParseEnv("# c\nexport A=1\nB=\"two words\"\nC='x'\nbad line\n");
        Assert.Equal("1", env["A"]);
        Assert.Equal("two words", env["B"]);
        Assert.Equal("x", env["C"]);
        Assert.Equal(3, env.Count);
    }

    [Fact]
    public async Task Secrets_are_encrypted_in_the_database_and_changed_by_name()
    {
        var id = CreateStack("enc-secrets");
        var (ok, _) = await SetSecretsAsync(id, new() { ["DB_PASSWORD"] = "S3cret-Value-4711", ["API_KEY"] = "abc" });
        Assert.True(ok);

        // The column holds only the encrypted form.
        using (var connection = new SqliteConnection(_site.App.Db(db => db.Database.GetDbConnection().ConnectionString)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SecretsJson FROM Stack WHERE Id = " + id;
            var raw = (string)command.ExecuteScalar()!;
            Assert.StartsWith("enc1:", raw);
            Assert.DoesNotContain("S3cret", raw);
        }

        Assert.Equal("S3cret-Value-4711", StackSecrets.Parse(_site.App.Db(db => db.Stacks.Single(s => s.Id == id).SecretsJson))["DB_PASSWORD"]);

        Assert.True((await SetSecretsAsync(id, new() { ["NEW"] = "1" }, "API_KEY")).Ok);
        Assert.Equal(new[] { "DB_PASSWORD", "NEW" }, StackSecrets.Names(_site.App.Db(db => db.Stacks.Single(s => s.Id == id).SecretsJson)));

        var (refused, message) = await SetSecretsAsync(id, new() { ["BAD NAME"] = "x" });
        Assert.False(refused);
        Assert.Contains("not a variable name", message);
    }

    [Fact]
    public async Task The_secrets_travel_on_stdin_and_never_on_the_command_line()
    {
        var id = CreateStack("stdin-secrets");
        await SetSecretsAsync(id, new() { ["DB_PASSWORD"] = "Never-On-The-Command-Line" });
        _site.App.Hosts.Commands.Clear();
        using (var scope = _site.App.Services.CreateScope())
        {
            var stacks = scope.ServiceProvider.GetRequiredService<StackService>();
            await stacks.DeployAsync(id);
            await stacks.RestartAsync(id);
            await stacks.ServiceActionAsync(id, "web", StackCommands.ServiceAction.Recreate);
        }

        var commands = _site.App.Hosts.Commands.Where(c => c.Contains("stdin-secrets", StringComparison.Ordinal)).ToList();
        Assert.True(commands.Count >= 3);
        Assert.DoesNotContain(commands, c => c.Contains("Never-On-The-Command-Line", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("DB_PASSWORD=Never-On-The-Command-Line\n")), StringComparison.Ordinal));
        // restart and the service action are told the exports follow on stdin
        Assert.Contains(commands, c => c.Contains(" restart", StringComparison.Ordinal) && c.EndsWith(" x 2>&1", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("--force-recreate --no-deps", StringComparison.Ordinal) && c.EndsWith(" x 2>&1", StringComparison.Ordinal));

        var lines = Encoding.UTF8.GetString(StackCommands.FilesOnStdin("services: {}\n", "A=1", null, "DB_PASSWORD=x\n")).Split('\n');
        Assert.Equal("DB_PASSWORD=x\n", Encoding.UTF8.GetString(Convert.FromBase64String(lines[2])));
    }

    [Fact]
    public void A_deploy_exports_the_secrets_to_compose_without_writing_them()
    {
        if (!File.Exists("/bin/sh"))
        {
            return; // the shell tests run on Linux (CI, the SDK container)
        }

        var dir = Directory.CreateTempSubdirectory("matdock-secrets").FullName;
        try
        {
            // The "docker" of this test prints the variable compose would see and comments out the rest of the command.
            var command = StackCommands.Deploy("printenv DB_PASSWORD; printenv WITH_STAR; #", "app");
            var stdin = StackCommands.FilesOnStdin("services: {}\n", null, null, "DB_PASSWORD=p@ss w0rd'$x\nWITH_STAR=*\n");
            var (exit, output) = Run(command, dir, stdin);
            Assert.Equal(0, exit);
            Assert.Contains("p@ss w0rd'$x", output);
            Assert.Contains("*\n", output);
            Assert.False(File.ReadAllText(Path.Combine(dir, "app", "docker-compose.yml")).Contains("p@ss", StringComparison.Ordinal));
            Assert.False(Directory.EnumerateFiles(Path.Combine(dir, "app")).Any(f => File.ReadAllText(f).Contains("p@ss", StringComparison.Ordinal)));

            // A Git deploy reads the exports line before the tar and names the env files.
            var repo = Directory.CreateTempSubdirectory("matdock-secrets-repo").FullName;
            Directory.CreateDirectory(Path.Combine(repo, "deploy"));
            File.WriteAllText(Path.Combine(repo, "deploy", "compose.yml"), "services: {}\n");
            File.WriteAllText(Path.Combine(repo, "deploy", ".env"), "A=1\n");
            File.WriteAllText(Path.Combine(repo, "deploy", ".env.prod"), "A=2\n");
            var tar = new MemoryStream();
            tar.Write(StackCommands.ExportsLine("DB_PASSWORD=from-git\n"));
            MatDock.Core.Git.RepoTar.Write(repo, tar);
            var git = StackCommands.GitSync("printenv DB_PASSWORD; echo", "gitapp", "deploy/compose.yml", DeployOptions.None, null, null,
                "deploy/.env.prod", "deploy/.env", exportsOnStdin: true);
            var (gitExit, gitOutput) = Run(git, dir, tar.ToArray());
            Assert.Equal(0, gitExit);
            Assert.Contains("from-git", gitOutput);
            Assert.True(gitOutput.Contains("--env-file deploy/.env --env-file deploy/.env.prod up -d", StringComparison.Ordinal),
                gitOutput + string.Join(",", Directory.EnumerateFileSystemEntries(Path.Combine(dir, "gitapp"), "*", SearchOption.AllDirectories)));
            Assert.Equal(" --env-file deploy/.env --env-file deploy/.env.prod".Trim(), File.ReadAllText(Path.Combine(dir, "gitapp", StackCommands.EnvFilesName)).Trim());
            Directory.Delete(repo, recursive: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_repository_goes_to_the_host_with_its_dot_files()
    {
        // TarFile.CreateFromDirectory leaves out what Linux counts as hidden: a repository's .env never reached the host.
        var repo = Directory.CreateTempSubdirectory("matdock-repotar").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "deploy", ".config"));
            File.WriteAllText(Path.Combine(repo, ".env"), "A=1\n");
            File.WriteAllText(Path.Combine(repo, "deploy", "compose.yml"), "services: {}\n");
            File.WriteAllText(Path.Combine(repo, "deploy", ".env.prod"), "A=2\n");
            File.WriteAllText(Path.Combine(repo, "deploy", ".config", "app.ini"), "x\n");

            var tar = new MemoryStream();
            MatDock.Core.Git.RepoTar.Write(repo, tar);
            tar.Position = 0;
            var names = new List<string>();
            using (var reader = new TarReader(tar))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    names.Add(entry.Name);
                }
            }

            Assert.Contains(".env", names);
            Assert.Contains("deploy/.env.prod", names);
            Assert.Contains("deploy/.config/", names);
            Assert.Contains("deploy/.config/app.ini", names);
            Assert.Contains("deploy/compose.yml", names);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static (int Exit, string Output) Run(string command, string stacksDir, byte[] stdin)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["MATDOCK_STACKS_DIR"] = stacksDir;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.BaseStream.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }

    [Theory]
    [InlineData(".env.production", "docker-compose.yml", ".env.production", ".env")]
    [InlineData("prod.env", "deploy/compose.yml", "deploy/prod.env", "deploy/.env")]
    public void The_env_file_sits_beside_the_compose_file(string file, string composePath, string envFile, string dotEnv)
    {
        var (resolved, dot) = StackService.GitEnvFiles(new MatDock.Core.Entities.Stack { GitEnvFile = file }, composePath);
        Assert.Equal(envFile, resolved);
        Assert.Equal(dotEnv, dot);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/etc/passwd")]
    [InlineData("my env")]
    [InlineData("a;rm -rf")]
    public void An_unsafe_env_file_is_refused(string file)
        => Assert.Null(StackService.GitEnvFiles(new MatDock.Core.Entities.Stack { GitEnvFile = file }, "docker-compose.yml").EnvFile);

    // ---- the rules ----

    private const string Messy = "version: \"3.8\"\nservices:\n  web:\n    image: nginx\n    ports:\n      - \"8080:80\"\n    enviroment:\n      - A=1\n"
                                 + "    depends_on:\n      - cache\n    volumes:\n      - data:/data\n      - /var/run/docker.sock:/var/run/docker.sock\n"
                                 + "  db:\n    image: postgres:17\n    restart: always\n    privileged: true\n    ports:\n      - \"5432:5432\"\n      - \"8080:81\"\n"
                                 + "    environment:\n      POSTGRES_PASSWORD: hunter2\n    networks:\n      - backend\n"
                                 + "volumes:\n  unused:\n";

    [Fact]
    public void The_rules_find_what_compose_refuses_and_what_is_risky()
    {
        var findings = ComposeLint.Check(Messy, defined: [], otherPorts: [new HostPortUse(5432, "tcp", "stack 'other'")]);
        string[] Codes(LintSeverity s) => findings.Where(f => f.Severity == s).Select(f => f.Code).Distinct().OrderBy(c => c).ToArray();

        Assert.Equal(new[] { "DEPENDS_ON_UNDEFINED", "DUPLICATE_HOST_PORT", "UNDEFINED_NETWORK", "UNDEFINED_VOLUME" }, Codes(LintSeverity.Error));
        Assert.Equal(new[] { "CROSS_STACK_PORT_COLLISION", "DB_PORT_ON_ALL_INTERFACES", "DOCKER_SOCKET_MOUNT", "PRIVILEGED_CONTAINER", "SECRET_IN_ENVIRONMENT", "UNKNOWN_KEY" },
            Codes(LintSeverity.Warning));
        Assert.Contains("LATEST_TAG", Codes(LintSeverity.Suggestion));
        Assert.Contains("MISSING_RESTART_POLICY", Codes(LintSeverity.Suggestion));
        Assert.Contains("OBSOLETE_VERSION_KEY", Codes(LintSeverity.Suggestion));
        Assert.Contains("UNUSED_VOLUME", Codes(LintSeverity.Suggestion));

        var typo = findings.Single(f => f.Code == "UNKNOWN_KEY");
        Assert.Contains("Did you mean 'environment'", typo.Message);
        Assert.Equal(7, typo.Line);
        Assert.Equal(19, findings.Single(f => f.Code == "DB_PORT_ON_ALL_INTERFACES").Line);
        Assert.True(findings.Select(f => f.Severity).SequenceEqual(findings.Select(f => f.Severity).OrderBy(s => s)));
    }

    [Fact]
    public void A_yaml_error_names_its_line()
    {
        var finding = Assert.Single(ComposeLint.Check("services:\n  web:\n    image: [nginx\n"));
        Assert.Equal("YAML_SYNTAX", finding.Code);
        Assert.Equal(LintSeverity.Error, finding.Severity);
    }

    [Fact]
    public void The_fixes_edit_the_text_in_place()
    {
        var findings = ComposeLint.Check(Messy, defined: []);
        var text = Messy;
        foreach (var code in new[] { "OBSOLETE_VERSION_KEY", "UNKNOWN_KEY", "DB_PORT_ON_ALL_INTERFACES", "MISSING_RESTART_POLICY" })
        {
            var finding = ComposeLint.Check(text, defined: []).First(f => f.Code == code && f.Fix is not null);
            text = ComposeLint.ApplyFix(text, finding.Fix!) ?? throw new Xunit.Sdk.XunitException($"{code} did not fix.");
        }

        Assert.DoesNotContain("version:", text);
        Assert.Contains("    environment:\n      - A=1", text);
        Assert.Contains("\"127.0.0.1:5432:5432\"", text);
        Assert.Contains("  web:\n    restart: unless-stopped\n    image: nginx", text);
        var after = ComposeLint.Check(text, defined: []).Select(f => f.Code).ToList();
        Assert.DoesNotContain("OBSOLETE_VERSION_KEY", after);
        Assert.DoesNotContain("UNKNOWN_KEY", after);
        Assert.DoesNotContain("DB_PORT_ON_ALL_INTERFACES", after);
        Assert.DoesNotContain("MISSING_RESTART_POLICY", after);
        Assert.NotEmpty(findings);
        Assert.Null(ComposeLint.ApplyFix(text, "rename:1:nope:rm -rf"));
    }

    [Fact]
    public void A_clean_file_has_only_suggestions_at_most()
    {
        const string clean = "services:\n  web:\n    image: nginx:1.27\n    restart: unless-stopped\n    healthcheck:\n      test: [\"CMD\", \"true\"]\n"
                             + "    ports:\n      - \"127.0.0.1:8080:80\"\n    environment:\n      DB_PASSWORD: ${DB_PASSWORD}\n";
        Assert.Empty(ComposeLint.Check(clean, defined: ["DB_PASSWORD"]));
        Assert.Contains(ComposeLint.Check(clean, defined: []), f => f.Code == "MISSING_VARIABLE" && f.Severity == LintSeverity.Warning);
    }

    // ---- the editor and the API ----

    [Fact]
    public async Task The_editor_saves_secrets_and_never_shows_their_values()
    {
        var (browser, html) = await AdminAsync("/Stacks/Edit");
        var response = await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = "page-secrets",
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Source"] = "inline",
            ["Input.ComposeYaml"] = "services:\n  web:\n    image: nginx:1.27\n",
            ["Input.Secrets[0].Key"] = "DB_PASSWORD",
            ["Input.Secrets[0].Value"] = "Page-Secret-0815",
            ["Input.Secrets[1].Key"] = "TOKEN",
            ["Input.Secrets[1].Value"] = "t",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var stack = _site.App.Db(db => db.Stacks.Single(s => s.Name == "page-secrets"));
        Assert.Equal("Page-Secret-0815", StackSecrets.Parse(stack.SecretsJson)["DB_PASSWORD"]);

        var edit = await (await browser.GetAsync($"/Stacks/Edit?id={stack.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("DB_PASSWORD", edit);
        Assert.DoesNotContain("Page-Secret-0815", edit);

        // An empty value keeps a stored secret, "Remove" drops one.
        response = await browser.PostAsync("/Stacks/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(edit),
            ["Input.Id"] = stack.Id.ToString(),
            ["Input.Name"] = "page-secrets",
            ["Input.EnvironmentId"] = _site.EnvId.ToString(),
            ["Input.Source"] = "inline",
            ["Input.ComposeYaml"] = "services:\n  web:\n    image: nginx:1.27\n",
            ["Input.Secrets[0].Key"] = "DB_PASSWORD",
            ["Input.Secrets[0].Existing"] = "true",
            ["Input.Secrets[0].Value"] = string.Empty,
            ["Input.Secrets[1].Key"] = "TOKEN",
            ["Input.Secrets[1].Existing"] = "true",
            ["Input.Secrets[1].Remove"] = "true",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var secrets = StackSecrets.Parse(_site.App.Db(db => db.Stacks.Single(s => s.Id == stack.Id).SecretsJson));
        Assert.Equal("Page-Secret-0815", secrets["DB_PASSWORD"]);
        Assert.False(secrets.ContainsKey("TOKEN"));
    }

    [Fact]
    public async Task The_live_check_and_its_fix_answer_json()
    {
        var (browser, html) = await AdminAsync("/Stacks/Edit");
        var token = SignIn.AntiforgeryToken(html);
        var lint = await browser.PostAsync("/Stacks/Edit?handler=Lint", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["yaml"] = "version: '3'\nservices:\n  web:\n    image: nginx:1.27\n    environment:\n      A: ${MISSING}\n      B: ${SECRET_ONE}\n",
            ["env"] = string.Empty,
            ["environmentId"] = _site.EnvId.ToString(),
            ["secretKeys"] = "SECRET_ONE",
        }));
        Assert.Equal(HttpStatusCode.OK, lint.StatusCode);
        var findings = JsonDocument.Parse(await lint.Content.ReadAsStringAsync()).RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Contains(findings, f => f.GetProperty("code").GetString() == "MISSING_VARIABLE" && f.GetProperty("message").GetString()!.Contains("MISSING"));
        Assert.DoesNotContain(findings, f => f.GetProperty("message").GetString()!.Contains("SECRET_ONE"));
        var version = findings.Single(f => f.GetProperty("code").GetString() == "OBSOLETE_VERSION_KEY");

        var fix = await browser.PostAsync("/Stacks/Edit?handler=Fix", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["yaml"] = "version: '3'\nservices:\n  web:\n    image: nginx:1.27\n",
            ["fix"] = version.GetProperty("fix").GetString()!,
        }));
        Assert.Equal("services:\n  web:\n    image: nginx:1.27\n", JsonDocument.Parse(await fix.Content.ReadAsStringAsync()).RootElement.GetProperty("yaml").GetString());
    }

    [Fact]
    public async Task The_api_sets_secrets_and_answers_only_their_names()
    {
        var api = _site.App.Api(MatDockFactory.EngineKey);
        var put = await api.PutAsJsonAsync("/api/v1/stacks/api-secrets", new
        {
            composeYaml = "services:\n  web:\n    image: nginx:1.27\n",
            secrets = new Dictionary<string, string?> { ["DB_PASSWORD"] = "Api-Secret-1", ["OTHER"] = "2" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Api-Secret-1", body);
        Assert.Equal(new[] { "DB_PASSWORD", "OTHER" },
            JsonDocument.Parse(body).RootElement.GetProperty("stack").GetProperty("secrets").EnumerateArray().Select(e => e.GetString()));

        var remove = await api.PutAsJsonAsync("/api/v1/stacks/api-secrets", new
        {
            composeYaml = "services:\n  web:\n    image: nginx:1.27\n",
            secrets = new Dictionary<string, string?> { ["OTHER"] = null },
        });
        var names = JsonDocument.Parse(await remove.Content.ReadAsStringAsync()).RootElement.GetProperty("stack").GetProperty("secrets").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(new[] { "DB_PASSWORD" }, names);
    }
}
