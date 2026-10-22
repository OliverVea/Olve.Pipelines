using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Profiles;

/// <summary>A profile of <c>~/.pl</c> as <c>pl profile list</c> reports it (never the tokens).</summary>
public sealed class ProfileInfo
{
    public required string Name { get; init; }
    public string? ApiUrl { get; init; }
    public bool LoggedIn { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool Selected { get; init; }
}

/// <summary><c>pl profile list</c> — the profiles in <c>~/.pl</c>, their instance and login state.</summary>
public sealed class ProfileListCommand : ICliCommand
{
    public string Noun => "profile";
    public string Verb => "list";
    public bool UsesApi => false;
    public string HelpLine => "List the profiles in ~/.pl: instance, login state, which one is selected";
    public string? HelpDetail =>
        """
        pl profile list

          A profile is one instance and its login. Pick one with --profile <name> or
          PIPELINES_PROFILE; without either, commands use 'default'. Create or refresh one with
            pl login --profile beta --api-url https://pipelines-v2-beta.ovea.pro
        """;

    public Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (CliConfigStore.LoadProfiles().TryPickProblems(out var problems, out var profiles))
            return Task.FromResult(Result.Failure(problems));

        var selected = CliConfigStore.ResolveProfile(cli);
        var infos = profiles.Values
            .OrderBy(p => p.Profile != CliConfigStore.DefaultProfile)
            .ThenBy(p => p.Profile, StringComparer.Ordinal)
            .Select(p => new ProfileInfo
            {
                Name = p.Profile,
                ApiUrl = p.ApiUrl,
                LoggedIn = p.Auth?.AccessToken is { Length: > 0 },
                ExpiresAt = p.Auth?.ExpiresAt,
                Selected = p.Profile == selected,
            })
            .ToArray();

        ctx.Output.Emit(infos, CliJsonContext.Default.ProfileInfoArray, () =>
        {
            if (infos.Length == 0)
            {
                ctx.Output.Line("No profiles yet. Create one with `pl login --api-url <instance>` (profile 'default').");
                return;
            }
            var rows = infos.Select(i => (IReadOnlyList<string>)
            [
                (i.Selected ? "* " : "  ") + i.Name,
                i.ApiUrl ?? "(default instance)",
                i.LoggedIn ? (i.ExpiresAt is { } e ? $"yes, token until {e:u}" : "yes") : "no",
            ]).ToList();
            foreach (var line in Table.Render(["PROFILE", "INSTANCE", "LOGGED IN"], rows))
                ctx.Output.Line(line);
        });
        return Task.FromResult(Result.Success());
    }
}
