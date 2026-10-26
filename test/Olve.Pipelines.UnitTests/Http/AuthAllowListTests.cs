using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Olve.Pipelines.Configuration;

namespace Olve.Pipelines.UnitTests.Http;

public class AuthAllowListTests
{
    private const string Issuer = "https://issuer.test";
    private static readonly string SigningKey = new('k', 64);

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static AuthAllowList Allow(string[]? users = null, string[]? orgs = null, string[]? emails = null, string[]? subjects = null) =>
        new(subjects ?? [], users ?? [], orgs ?? [], emails ?? []);

    [Test]
    public async Task Empty_AllowsAnyone() =>
        await Assert.That(Allow().Allows(User(("preferred_username", "someone")))).IsTrue();

    [Test]
    public async Task Users_MatchPreferredUsername_IgnoringCase()
    {
        var allow = Allow(users: ["OliverVea"]);
        await Assert.That(allow.Allows(User(("preferred_username", "olivervea")))).IsTrue();
        await Assert.That(allow.Allows(User(("preferred_username", "someone")))).IsFalse();
        await Assert.That(allow.Allows(User(("sub", "OliverVea")))).IsFalse();
    }

    // Catches: an allow-list entry that a renamed-and-re-registered login would still satisfy.
    [Test]
    public async Task Subjects_MatchSubExactly_NotTheLogin()
    {
        var allow = Allow(subjects: ["CgcxMjM0NTY3EgZnaXRodWI"]);
        await Assert.That(allow.Allows(User(("sub", "CgcxMjM0NTY3EgZnaXRodWI"), ("preferred_username", "anyone")))).IsTrue();
        await Assert.That(allow.Allows(User((ClaimTypes.NameIdentifier, "CgcxMjM0NTY3EgZnaXRodWI")))).IsTrue();
        await Assert.That(allow.Allows(User(("sub", "cgcxmjm0nty3egznaxrodwi")))).IsFalse();
        await Assert.That(allow.Allows(User(("preferred_username", "CgcxMjM0NTY3EgZnaXRodWI")))).IsFalse();
    }

    [Test]
    public async Task Orgs_MatchTheOrgOrAnyOfItsTeams_AndOrgTeamOnlyThatTeam()
    {
        await Assert.That(Allow(orgs: ["ovea"]).Allows(User(("groups", "ovea:admins")))).IsTrue();
        await Assert.That(Allow(orgs: ["ovea"]).Allows(User(("groups", "ovea")))).IsTrue();
        await Assert.That(Allow(orgs: ["ovea"]).Allows(User(("groups", "oveax")))).IsFalse();
        await Assert.That(Allow(orgs: ["ovea:admins"]).Allows(User(("groups", "ovea:admins")))).IsTrue();
        await Assert.That(Allow(orgs: ["ovea:admins"]).Allows(User(("groups", "ovea:guests")))).IsFalse();
    }

    [Test]
    public async Task Emails_NeedAVerifiedEmail()
    {
        var allow = Allow(emails: ["me@example.com"]);
        await Assert.That(allow.Allows(User(("email", "me@example.com"), ("email_verified", "true")))).IsTrue();
        await Assert.That(allow.Allows(User(("email", "me@example.com"), ("email_verified", "false")))).IsFalse();
        await Assert.That(allow.Allows(User(("email", "me@example.com")))).IsFalse();
    }

    private static async Task<HttpStatusCode> Get(string path, string? user, params string[] allowedUsers)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Auth:Audience"] = "olve-pipelines";
        builder.Configuration["Auth:SigningKey"] = SigningKey;
        builder.Configuration["Auth:Authority"] = Issuer;
        builder.Configuration["Auth:FrontendAuthority"] = Issuer;
        for (var i = 0; i < allowedUsers.Length; i++)
            builder.Configuration[$"Auth:Allow:Users:{i}"] = allowedUsers[i];
        builder.ConfigureAuthentication();

        await using var app = builder.Build();
        app.MapAuthentication();
        app.MapGet("/api/read", () => "ok").RequireRead();
        app.MapPost("/api/write", () => "ok");
        await app.StartAsync();

        var client = app.GetTestClient();
        if (user is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        var response = path == "/api/write" ? await client.PostAsync(path, null) : await client.GetAsync(path);
        return response.StatusCode;
    }

    private static string Token(string user) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = "olve-pipelines",
        Claims = new Dictionary<string, object> { ["preferred_username"] = user },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
    });

    [Test]
    [Arguments("/api/read")]
    [Arguments("/api/write")]
    public async Task ValidTokenOfAnotherUser_IsForbidden(string path) =>
        await Assert.That(await Get(path, "someone", "OliverVea")).IsEqualTo(HttpStatusCode.Forbidden);

    [Test]
    [Arguments("/api/read")]
    [Arguments("/api/write")]
    public async Task AllowedUser_GetsIn(string path) =>
        await Assert.That(await Get(path, "OliverVea", "OliverVea")).IsEqualTo(HttpStatusCode.OK);

    [Test]
    public async Task WithoutAnAllowList_AnyValidTokenGetsIn() =>
        await Assert.That(await Get("/api/write", "someone")).IsEqualTo(HttpStatusCode.OK);
}
