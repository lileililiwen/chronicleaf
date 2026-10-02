using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// COMMUNITY-ACCESS-001 and COMMUNITY-ACCESS-002 against a real disposable PostgreSQL started
/// from a pinned Docker image, through the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// Nothing here is simulated. Session rows, idle and absolute deadlines, the durable login
/// throttle, the owner identity and the access-token rotation are all produced by a real
/// PostgreSQL server, and every status code, header, cookie attribute and row count is read back
/// over the wire or over a raw connection. There is no EF in-memory provider and no stubbed store:
/// an unavailable container is a failure, never a passing result.
/// <para>
/// The clock is injected and deterministic, so expiry is proven by moving time rather than by
/// waiting for it. Every id the assertions depend on is either a deterministic constant or read
/// back from the database.
/// </para>
/// </remarks>
public sealed class CommunityAccessTests(ChronicleafPostgresFixture postgres) : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The operator's setup token and the hash this deployment is configured with. The token is a
    /// fixture value; a deployment configures only its SHA-256 hex, which is what the host below
    /// is given.
    /// </summary>
    private const string AccessToken = "chronicleaf-operator-setup-token-0123456789abcdef";

    private static readonly string AccessTokenHash = Sha256.Sha256Hex(AccessToken);

    /// <summary>A different setup token, for the rotation scenario.</summary>
    private const string RotatedAccessToken = "chronicleaf-operator-setup-token-rotated-9876543210";

    private static readonly string RotatedAccessTokenHash = Sha256.Sha256Hex(RotatedAccessToken);

    /// <summary>The single origin this installation accepts for mutations.</summary>
    private const string PublicOrigin = "https://chronicleaf.example";

    private static readonly Guid InstallationId = new("4c7a1b2d-3e4f-4a5b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid LegacyInstallationId = new("5d8b2c3e-4f5a-4b6c-9d0e-1f2a3b4c5d6e");

    private const string SessionCookieName = "chronicleaf_session";
    private const string AntiforgeryHeaderName = "X-Chronicleaf-Antiforgery";

    // ===========================================================================================
    // COMMUNITY-ACCESS-001 Success: a valid setup token creates a bounded owner session.
    // ===========================================================================================

    [Fact]
    public async Task Valid_setup_token_creates_a_session_cookie_and_private_routes_work()
    {
        Assert.True(postgres.IsRealPostgres, "the access scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("signin");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        using var signIn = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });

        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        Assert.Equal(0, signIn.Content.Headers.ContentLength ?? 0);

        // The cookie is unreadable from script, is never sent by a cross-site request, and is
        // marked Secure because this deployment is not a loopback Development run.
        var cookie = ParseSetCookie(Assert.Single(signIn.Headers.GetValues("Set-Cookie")));
        Assert.Equal(SessionCookieName, cookie.Name);
        Assert.Equal("httponly", cookie.HttpOnly);
        Assert.Equal("strict", cookie.SameSite);
        Assert.Equal("secure", cookie.Secure);
        Assert.Equal("/", cookie.Path);

        // The cookie expires with the session's absolute lifetime, not with the browser session.
        Assert.Equal(Clock.AddDays(7), cookie.Expires);

        // The cookie's own value is a random opaque token, and only its hash reached a row.
        var cookieToken = CookieValue(cookie);
        Assert.Equal(64, cookieToken.Length);
        Assert.Equal(
            Sha256.Sha256Hex(cookieToken),
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT id_hash FROM session"));
        Assert.DoesNotContain(cookieToken, await ReadAllSessionRowsAsync(database), StringComparison.Ordinal);

        // The session is one row, owned by the installation's own actor id, and not revoked.
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
        Assert.Equal(
            "true",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT (revoked_at IS NULL)::text FROM session"));
        var actorId = await ReadOwnerActorIdAsync(database);
        Assert.NotEqual(Guid.Empty, actorId);
        Assert.Equal(
            actorId.ToString(),
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT actor_id::text FROM session"));

        // The same cookie now opens a private route, and it reports that owner.
        using var session = await client.SendAsync(RequestWithCookie(cookieToken));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(actorId.ToString(), (await ReadActorIdAsync(session)));
    }

    [Fact]
    public async Task Owner_identity_is_generated_once_and_survives_session_rotation()
    {
        var database = await ProvisionedDatabaseAsync("owner_identity");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var firstCookie = await SignInAsync(client);
        var actorId = await ReadOwnerActorIdAsync(database);
        Assert.NotEqual(Guid.Empty, actorId);

        // A second login replaces the first: the cookie changes, the new session works and the
        // old one stops working, while the owner identity is untouched.
        using var second = await SignInAsync(client, firstCookie);
        var secondCookie = CookieValue(ParseSetCookie(Assert.Single(second.Headers.GetValues("Set-Cookie"))));
        Assert.NotEqual(firstCookie, secondCookie);

        Assert.Equal(actorId, await ReadOwnerActorIdAsync(database));
        Assert.Equal(2, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));

        // The old cookie is refused; the new one still works.
        using var stale = await client.SendAsync(RequestWithCookie(firstCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        using var current = await client.SendAsync(RequestWithCookie(secondCookie));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-001 Failure: an anonymous caller reaches nothing, and a wrong token is
    // refused generically.
    // ===========================================================================================

    [Fact]
    public async Task Anonymous_caller_gets_401_without_any_private_data()
    {
        var database = await ProvisionedDatabaseAsync("anonymous");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        foreach (var path in new[] { "/api/session", "/api/session/antiforgery" })
        {
            using var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            // The envelope names the refusal and carries no actor, no session, no token and no
            // antiforgery value.
            Assert.Equal(AccessError.Unauthenticated, document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("actorId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AccessToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessTokenHash, body, StringComparison.OrdinalIgnoreCase);
        }

        // An anonymous mutation is refused before it can write anything.
        using var mutation = await client.DeleteAsync("/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
        Assert.Equal(0, await CountAccessAuditRowsAsync(database));
    }

    [Fact]
    public async Task Invalid_setup_token_is_a_generic_401_and_never_persists_anything()
    {
        var database = await ProvisionedDatabaseAsync("invalid_token");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        // A wrong token, a near-miss, an empty one and a malformed one are all the same answer:
        // nothing may distinguish them.
        foreach (var presented in new[] { AccessToken + "x", AccessToken[..^1], string.Empty, "not-a-token" })
        {
            using var response = await client.PostAsJsonAsync("/api/session", new { accessToken = presented });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(AccessError.InvalidCredentials, document.RootElement.GetProperty("code").GetString());

            // The message is the generic one, and the body never quotes what was presented.
            Assert.DoesNotContain("unknown", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("malformed", body, StringComparison.OrdinalIgnoreCase);
            if (presented.Length > 0)
            {
                Assert.DoesNotContain(presented, body, StringComparison.Ordinal);
            }
        }

        // Nothing was created, and the owner identity was never generated by a failed login.
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
        Assert.Equal(0, await CountAccessAuditRowsAsync(database));
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT owner_actor_id::text FROM installation"));
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-001 Boundary: expiry and revocation, proven by moving the injected clock.
    // ===========================================================================================

    [Fact]
    public async Task Idle_expired_session_is_refused_and_the_absolute_deadline_is_never_extended()
    {
        var database = await ProvisionedDatabaseAsync("idle_expiry");
        var clock = new AdjustableTimeProvider(Clock);

        await using var host = ChronicleafApiHost.StartWithAccess(database, clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);

        // The stored deadlines are the design's: 12h idle, 7d absolute.
        Assert.Equal(
            "43200|604800",
            await ReadDeadlinesAsync(database));

        // Still inside the idle window, with the absolute deadline untouched by the activity.
        clock.Advance(TimeSpan.FromHours(11));
        using var duringIdle = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.OK, duringIdle.StatusCode);
        Assert.Equal("82800|604800", await ReadDeadlinesAsync(database));

        // One hour past the refreshed idle deadline: the session is refused.
        clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        using var afterIdle = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, afterIdle.StatusCode);

        // The refusal is a decision, not a deletion: the row is still there and still unrevoked.
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT revoked_at::text FROM session"));
    }

    [Fact]
    public async Task Session_past_its_absolute_lifetime_is_refused_even_while_active()
    {
        var database = await ProvisionedDatabaseAsync("absolute_expiry");
        var clock = new AdjustableTimeProvider(Clock);

        await using var host = ChronicleafApiHost.StartWithAccess(database, clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);

        // Activity every 11 hours keeps refreshing the idle deadline, and the absolute deadline
        // still ends the session on the seventh day: fifteen 11 hour steps is six days and
        // twenty-one hours, and the next one crosses the seven day deadline.
        for (var step = 0; step < 15; step++)
        {
            clock.Advance(TimeSpan.FromHours(11));
            using var alive = await client.SendAsync(RequestWithCookie(cookie));
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }

        Assert.Equal("604800", (await ReadDeadlinesAsync(database)).Split('|')[1]);

        clock.Advance(TimeSpan.FromHours(11));
        using var expired = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Revoked_session_is_refused_immediately()
    {
        var database = await ProvisionedDatabaseAsync("revoked");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);

        using var beforeRevoke = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        // Revoke the stored session directly, the way a rotation or an operator decision would.
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "UPDATE session SET revoked_at = '2026-10-02T12:00:00+00:00' WHERE revoked_at IS NULL");

        using var afterRevoke = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        using var antiforgery = await client.SendAsync(RequestWithCookie(cookie, "/api/session/antiforgery"));
        Assert.Equal(HttpStatusCode.Unauthorized, antiforgery.StatusCode);

        // A revocation also removes the session's ability to mutate.
        using var mutation = await client.SendAsync(Mutation(cookie, "irrelevant", PublicOrigin));
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_deletes_the_cookie()
    {
        var database = await ProvisionedDatabaseAsync("logout");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);
        using var antiforgeryResponse = await client.SendAsync(RequestWithCookie(cookie, "/api/session/antiforgery"));
        var antiforgery = await ReadAntiforgeryAsync(antiforgeryResponse);

        using var signOut = await client.SendAsync(Mutation(cookie, antiforgery, PublicOrigin));
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        // The deletion repeats the cookie's own flags, so the browser drops it even though it no
        // longer matches a stored session.
        var deletion = ParseSetCookie(Assert.Single(signOut.Headers.GetValues("Set-Cookie")));
        Assert.Equal(SessionCookieName, deletion.Name);
        Assert.Equal("httponly", deletion.HttpOnly);
        Assert.Equal("strict", deletion.SameSite);
        Assert.Equal("secure", deletion.Secure);
        Assert.Equal("/", deletion.Path);
        Assert.Equal(DateTimeOffset.UnixEpoch, deletion.Expires);

        // The revocation is a real row change, and the cookie no longer opens anything.
        Assert.Equal(
            "true",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT (revoked_at IS NOT NULL)::text FROM session"));
        using var afterwards = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-002 Success: a same-origin mutation with a valid antiforgery token runs.
    // ===========================================================================================

    [Fact]
    public async Task Same_origin_mutation_with_a_valid_antiforgery_token_executes()
    {
        var database = await ProvisionedDatabaseAsync("mutation_ok");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);
        var antiforgery = await IssueAntiforgeryAsync(client, cookie);

        using var response = await client.SendAsync(Mutation(cookie, antiforgery, PublicOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The write really happened: the row is revoked and the audit log names the action.
        Assert.Equal(
            "true",
            await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT (revoked_at IS NOT NULL)::text FROM session"));
        Assert.Equal("1", await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT count(*)::text FROM audit_events WHERE action = 'session.revoked'"));
    }

    [Fact]
    public async Task Antiforgery_metadata_is_owner_only_and_rotates_on_every_request()
    {
        var database = await ProvisionedDatabaseAsync("antiforgery_meta");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);

        using var first = await client.SendAsync(RequestWithCookie(cookie, "/api/session/antiforgery"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(AntiforgeryHeaderName, firstDocument.RootElement.GetProperty("headerName").GetString());
        var firstToken = firstDocument.RootElement.GetProperty("token").GetString()!;

        // The value is separate from the login token and is not the session cookie.
        Assert.NotEqual(AccessToken, firstToken);
        Assert.NotEqual(cookie, firstToken);
        Assert.Equal(64, firstToken.Length);

        // A new value replaces the stored one, so the previous value stops working.
        using var second = await client.SendAsync(RequestWithCookie(cookie, "/api/session/antiforgery"));
        var secondToken = await ReadAntiforgeryAsync(second);
        Assert.NotEqual(firstToken, secondToken);

        using var withOld = await client.SendAsync(Mutation(cookie, firstToken, PublicOrigin));
        Assert.Equal(HttpStatusCode.Forbidden, withOld.StatusCode);

        using var withNew = await client.SendAsync(Mutation(cookie, secondToken, PublicOrigin));
        Assert.Equal(HttpStatusCode.NoContent, withNew.StatusCode);

        // The metadata response is the only place the value is issued, and it is owner-only.
        using var anonymous = await client.GetAsync("/api/session/antiforgery");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-002 Failure: a cross-origin mutation writes nothing.
    // ===========================================================================================

    [Fact]
    public async Task Cross_origin_mutation_is_403_and_writes_nothing()
    {
        var database = await ProvisionedDatabaseAsync("cross_origin");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);
        var antiforgery = await IssueAntiforgeryAsync(client, cookie);
        var auditRowsBefore = await CountAccessAuditRowsAsync(database);

        // A wrong scheme, a wrong port, a look-alike host, a look-alike suffix and a request with
        // no Origin at all are all refused.
        foreach (var origin in new[]
        {
            "https://evil.example",
            "http://chronicleaf.example",
            "https://chronicleaf.example:8443",
            "https://not-chronicleaf.example",
            "https://chronicleaf.example.evil.test",
            string.Empty,
        })
        {
            using var response = await client.SendAsync(
                MutationWithOrigin(cookie, antiforgery, origin));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(AccessError.OriginMismatch, await ReadCodeAsync(response));
        }

        // Not one of them wrote anything: the session is still usable, still unrevoked, and not
        // one of them produced an audit row.
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT revoked_at::text FROM session"));
        Assert.Equal(auditRowsBefore, await CountAccessAuditRowsAsync(database));
        using var stillValid = await client.SendAsync(RequestWithCookie(cookie));
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-002 Failure: a missing or wrong antiforgery token writes nothing either.
    // ===========================================================================================

    [Fact]
    public async Task Missing_or_wrong_antiforgery_token_is_403_and_writes_nothing()
    {
        var database = await ProvisionedDatabaseAsync("antiforgery_missing");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var cookie = await SignInAsync(client);
        var antiforgery = await IssueAntiforgeryAsync(client, cookie);

        // Another session's antiforgery value is not this session's value, so it must not work.
        using var otherClient = host.CreateClient();
        var otherCookie = await SignInAsync(otherClient);
        var otherAntiforgery = await IssueAntiforgeryAsync(otherClient, otherCookie);
        var auditRowsBefore = await CountAccessAuditRowsAsync(database);

        using (var absent = await client.SendAsync(Mutation(cookie, null, PublicOrigin)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, absent.StatusCode);
            Assert.Equal(AccessError.AntiforgeryInvalid, await ReadCodeAsync(absent));
        }

        foreach (var presented in new[]
        {
            antiforgery + "x",
            antiforgery[..^1],
            AccessToken,
            otherAntiforgery,
            "not-a-token",
        })
        {
            using var response = await client.SendAsync(Mutation(cookie, presented, PublicOrigin));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(AccessError.AntiforgeryInvalid, await ReadCodeAsync(response));
        }

        // Nothing was written, and the rejected value never came back in an error body.
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT revoked_at::text FROM session"));
        Assert.Equal(auditRowsBefore, await CountAccessAuditRowsAsync(database));
        Assert.Equal(2, await CountUnrevokedSessionsAsync(database));
    }

    // ===========================================================================================
    // COMMUNITY-ACCESS-002 Boundary: the durable login throttle.
    // ===========================================================================================

    [Fact]
    public async Task Failed_logins_inside_the_window_are_counted_and_the_next_attempt_is_throttled()
    {
        var database = await ProvisionedDatabaseAsync("throttle");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        // Five failed logins inside the 15 minute window are each refused as invalid credentials,
        // and each one is durably counted.
        for (var attempt = 1; attempt <= AccessSettings.LoginThrottleAllowance; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/session", new { accessToken = $"wrong-{attempt}" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(AccessError.InvalidCredentials, await ReadCodeAsync(response));
            Assert.Equal(attempt, await ChronicleafPostgresFixture.CountRowsAsync(database, "login_attempt"));
        }

        // The next attempt is throttled rather than merely counted, and so is the one after it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var throttled = await client.PostAsJsonAsync("/api/session", new { accessToken = "wrong-again" });

            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
            Assert.Equal(AccessError.TooManyRequests, await ReadCodeAsync(throttled));

            // A real cooldown, derived from the oldest counted failure, and no new count.
            Assert.Equal("900", throttled.Headers.RetryAfter?.ToString());
            Assert.Equal(
                AccessSettings.LoginThrottleAllowance,
                await ChronicleafPostgresFixture.CountRowsAsync(database, "login_attempt"));
        }

        // A throttled client is refused even when it presents the correct token, and nothing was
        // created for it.
        using var validButThrottled = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        Assert.Equal(HttpStatusCode.TooManyRequests, validButThrottled.StatusCode);
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
    }

    [Fact]
    public async Task Throttle_window_closes_and_a_successful_login_clears_the_count()
    {
        var database = await ProvisionedDatabaseAsync("throttle_window");
        var clock = new AdjustableTimeProvider(Clock);

        await using var host = ChronicleafApiHost.StartWithAccess(database, clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        for (var attempt = 0; attempt < AccessSettings.LoginThrottleAllowance; attempt++)
        {
            using var failed = await client.PostAsJsonAsync("/api/session", new { accessToken = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using (var throttled = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        }

        // Past the window the counted failures no longer apply, and a verified login clears them
        // rather than leaving a client one mistake away from a lockout.
        clock.Advance(AccessSettings.LoginThrottleWindow + TimeSpan.FromMinutes(1));

        using var signIn = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "login_attempt"));
    }

    [Fact]
    public async Task Throttle_counters_are_stored_hashed_and_are_durable_across_a_restart()
    {
        var database = await ProvisionedDatabaseAsync("throttle_durable");

        await using (var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin))
        {
            using var client = host.CreateClient();

            for (var attempt = 0; attempt < AccessSettings.LoginThrottleAllowance; attempt++)
            {
                using var failed = await client.PostAsJsonAsync("/api/session", new { accessToken = "wrong" });
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
        }

        // The counter is a hash, not a client identity in the clear.
        var stored = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT client_key_hash FROM login_attempt LIMIT 1");
        Assert.NotNull(stored);
        Assert.Equal(64, stored!.Length);
        Assert.Matches("^[0-9a-f]{64}$", stored);
        Assert.DoesNotContain("::", stored, StringComparison.Ordinal);

        // A new process against the same database is still throttled: the counters are rows, not
        // process memory.
        await using var restarted = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var restartedClient = restarted.CreateClient();

        using var throttled = await restartedClient.PostAsJsonAsync("/api/session", new { accessToken = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(
            AccessSettings.LoginThrottleAllowance,
            await ChronicleafPostgresFixture.CountRowsAsync(database, "login_attempt"));
    }

    // ===========================================================================================
    // A rotated access token revokes every existing session, durably, at start-up.
    // ===========================================================================================

    [Fact]
    public async Task Changing_the_configured_access_token_hash_revokes_every_session_at_startup()
    {
        var database = await ProvisionedDatabaseAsync("rotation");

        string firstCookie;
        await using (var original = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin))
        {
            using var client = original.CreateClient();
            firstCookie = await SignInAsync(client);

            using var before = await client.SendAsync(RequestWithCookie(firstCookie));
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // The next start is configured with a different access token hash.
        await using var rotated = ChronicleafApiHost.StartWithAccess(database, Clock, RotatedAccessTokenHash, PublicOrigin);
        using var rotatedClient = rotated.CreateClient();

        // The session the previous token created was already revoked, before any request arrived.
        Assert.Equal(0, await CountUnrevokedSessionsAsync(database));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
        Assert.Equal("session.revoked_token_rotation", await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT action FROM audit_events WHERE action LIKE 'session.revoked%' LIMIT 1"));

        using var afterRotation = await rotatedClient.SendAsync(RequestWithCookie(firstCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRotation.StatusCode);

        // The old token no longer logs in, and the new one does.
        using var oldToken = await rotatedClient.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.Equal(AccessError.InvalidCredentials, await ReadCodeAsync(oldToken));

        string rotatedCookie;
        using (var newToken = await rotatedClient.PostAsJsonAsync("/api/session", new { accessToken = RotatedAccessToken }))
        {
            Assert.Equal(HttpStatusCode.NoContent, newToken.StatusCode);
            rotatedCookie = CookieValue(ParseSetCookie(Assert.Single(newToken.Headers.GetValues("Set-Cookie"))));
        }

        // The fingerprint is durable: a third start with the same hash revokes nothing new, and the
        // session the rotated token created still works.
        await using var same = ChronicleafApiHost.StartWithAccess(database, Clock, RotatedAccessTokenHash, PublicOrigin);
        using var sameClient = same.CreateClient();
        Assert.Equal(1, await CountUnrevokedSessionsAsync(database));
        using var survived = await sameClient.SendAsync(RequestWithCookie(rotatedCookie));
        Assert.Equal(HttpStatusCode.OK, survived.StatusCode);
        Assert.Equal("1", await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT count(*)::text FROM audit_events WHERE action = 'session.revoked_token_rotation'"));
    }

    // ===========================================================================================
    // The public exceptions, and the liveness contract they must not disturb.
    // ===========================================================================================

    [Fact]
    public async Task Health_and_ready_stay_public_and_health_keeps_its_exact_payload()
    {
        var database = await ProvisionedDatabaseAsync("public_routes");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"service\":\"chronicleaf\"}", await health.Content.ReadAsStringAsync());

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("{\"status\":\"ready\"}", await ready.Content.ReadAsStringAsync());

        // The liveness contract is answered with no cookie and from a foreign origin too.
        using var anonymousHealth = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/health")
            {
                Headers = { { "Origin", "https://elsewhere.example" } },
            });
        Assert.Equal(HttpStatusCode.OK, anonymousHealth.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"service\":\"chronicleaf\"}", await anonymousHealth.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_three_named_api_routes_are_reachable_without_a_session()
    {
        var database = await ProvisionedDatabaseAsync("boundary_table");
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        var publicRoutes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/health"),
            (HttpMethod.Get, "/api/ready"),
            (HttpMethod.Post, "/api/session"),
        };

        foreach (var (method, path) in publicRoutes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new { accessToken = AccessToken });
            }

            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode != HttpStatusCode.Unauthorized,
                $"{method} {path} must not require a session, but answered {response.StatusCode}.");
        }

        var privateRoutes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/session"),
            (HttpMethod.Get, "/api/session/antiforgery"),
            (HttpMethod.Delete, "/api/session"),
        };

        foreach (var (method, path) in privateRoutes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get)
            {
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // An unknown /api path is still the shell's 404, which discloses nothing either.
        using var unknown = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unknownDocument = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal("not_found", unknownDocument.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Missing_or_unusable_access_token_hash_stops_a_non_development_start()
    {
        var failure = Assert.ThrowsAny<Exception>(() =>
        {
            using var host = ChronicleafApiHost.StartInEnvironment(
                ChronicleafConnectionForStartUp(),
                Clock,
                accessTokenHash: null,
                "Production");
            _ = host.Services;
        });

        Assert.Contains(
            Flatten(failure),
            message => message.Contains(AccessSettings.AccessTokenHashSettingName, StringComparison.Ordinal));

        // An unusable value is treated exactly like an absent one: never a half-open boundary.
        var unusable = Assert.ThrowsAny<Exception>(() =>
        {
            using var host = ChronicleafApiHost.StartInEnvironment(
                ChronicleafConnectionForStartUp(),
                Clock,
                "not-a-hash",
                "Production");
            _ = host.Services;
        });

        Assert.Contains(
            Flatten(unusable),
            message => message.Contains(AccessSettings.AccessTokenHashSettingName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Development_without_an_access_token_hash_starts_but_accepts_no_login()
    {
        // The documented Development behaviour: the process starts, and because no token is
        // configured no presented token can match. The boundary is closed, not open.
        var database = await ProvisionedDatabaseAsync("development_no_token");

        await using var host = ChronicleafApiHost.StartInEnvironment(
            database,
            Clock,
            accessTokenHash: null,
            "Development");
        using var client = host.CreateClient();

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var signIn = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(AccessError.InvalidCredentials, await ReadCodeAsync(signIn));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
    }

    [Fact]
    public async Task Unprovisioned_installation_is_reported_as_such_and_not_as_bad_credentials()
    {
        // A migrated database with no installation row: this deployment was never provisioned.
        var database = await postgres.CreateDatabaseAsync("unprovisioned");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AccessError.InstallationMissing, await ReadCodeAsync(response));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "session"));
    }

    // ===========================================================================================
    // The migration this package owns, applied to a database the previous build created.
    // ===========================================================================================

    [Fact]
    public async Task Access_migration_backfills_the_owner_actor_id_of_an_existing_installation()
    {
        Assert.True(postgres.IsRealPostgres);

        // A database migrated by the previous build only, with an installation that predates the
        // access package. This is the real upgrade path, not a fresh database.
        var database = await postgres.CreateDatabaseAsync("upgrade_path");
        await MigrateToAsync(database, ChronicleafSchema.InitialMigrationId);
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO installation (id, schema_version, created_at) VALUES "
            + $"('{LegacyInstallationId}', {ChronicleafDbContext.RequiredSchemaVersion}, '2026-10-01T09:00:00+00:00')");

        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT column_name FROM information_schema.columns WHERE column_name = 'owner_actor_id'"));

        // The forward migration adds the column and backfills the installation that already existed.
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var backfilled = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT owner_actor_id::text FROM installation WHERE id = '{LegacyInstallationId}'");
        Assert.NotNull(backfilled);
        Assert.True(Guid.TryParse(backfilled, out _), $"the backfill must be a uuid, but was '{backfilled}'.");

        // The pre-existing data-foundation rows are untouched by the additive migration.
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));
        Assert.Equal(2, await ChronicleafPostgresFixture.CountRowsAsync(database, "__EFMigrationsHistory"));

        // The backfilled identity is the one the access layer uses, and it does not change again.
        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();
        var cookie = await SignInAsync(client);
        Assert.Equal(backfilled, await ReadActorIdAsync(await client.SendAsync(RequestWithCookie(cookie))));

        // Applying the migrations again is a no-op, the identity is stable, and readiness is current.
        await ChronicleafPostgresFixture.MigrateAsync(database);
        Assert.Equal(2, await ChronicleafPostgresFixture.CountRowsAsync(database, "__EFMigrationsHistory"));
        Assert.Equal(
            backfilled,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT owner_actor_id::text FROM installation WHERE id = '{LegacyInstallationId}'"));

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Readiness_requires_every_migration_not_only_the_newest()
    {
        // A database carrying the first migration but not the access migration: reachable, and
        // genuinely behind. It must not be reported ready.
        var database = await postgres.CreateDatabaseAsync("partial_schema");
        await MigrateToAsync(database, ChronicleafSchema.InitialMigrationId);

        await using var host = ChronicleafApiHost.StartWithAccess(database, Clock, AccessTokenHash, PublicOrigin);
        using var client = host.CreateClient();

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var readyDocument = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("schema_mismatch", readyDocument.RootElement.GetProperty("code").GetString());

        // And the boundary cannot be exercised against a schema that has no session table.
        using var signIn = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, signIn.StatusCode);

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    /// <summary>
    /// A migrated database with the singleton installation provisioned through the data
    /// foundation's own contract, which is the order a real deployment follows.
    /// </summary>
    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using (var host = ChronicleafApiHost.Start(database, Clock))
        {
            using var scope = host.Services.CreateScope();
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return database;
    }

    private static async Task MigrateToAsync(string connectionString, string targetMigrationId)
    {
        await using var context = ChronicleafPostgresFixture.CreateContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(targetMigrationId);
    }

    /// <summary>
    /// A connection string the start-up guard rejects before the access boundary matters, so the
    /// configuration failure under test is the access one and not the database one.
    /// </summary>
    private static string ChronicleafConnectionForStartUp() =>
        "Host=chronicleaf-startup-guard.invalid;Database=chronicleaf;Username=chronicleaf";

    private static async Task<string> SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/session", new { accessToken = AccessToken });
        response.EnsureSuccessStatusCode();

        return CookieValue(ParseSetCookie(Assert.Single(response.Headers.GetValues("Set-Cookie"))));
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string existingCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = JsonContent.Create(new { accessToken = AccessToken }),
        };
        request.Headers.Add("Cookie", $"{SessionCookieName}={existingCookie}");

        return await client.SendAsync(request);
    }

    private static async Task<string> IssueAntiforgeryAsync(HttpClient client, string cookie)
    {
        using var response = await client.SendAsync(RequestWithCookie(cookie, "/api/session/antiforgery"));
        response.EnsureSuccessStatusCode();

        return (await ReadAntiforgeryAsync(response))!;
    }

    private static async Task<Guid> ReadOwnerActorIdAsync(string database) =>
        Guid.Parse((await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT owner_actor_id::text FROM installation"))!);

    /// <summary>
    /// The stored idle and absolute deadlines in whole seconds from the session's creation. Seconds
    /// are used because an interval's own text form is a PostgreSQL rendering detail, not the
    /// property under test.
    /// </summary>
    private static async Task<string> ReadDeadlinesAsync(string database) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT EXTRACT(EPOCH FROM (idle_expires_at - created_at))::int::text || '|' || "
            + "EXTRACT(EPOCH FROM (expires_at - created_at))::int::text FROM session"))!;

    private static async Task<long> CountUnrevokedSessionsAsync(string database) =>
        long.Parse((await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT count(*)::text FROM session WHERE revoked_at IS NULL"))!);

    /// <summary>How many audit rows the access package itself wrote, ignoring provisioning's.</summary>
    private static async Task<long> CountAccessAuditRowsAsync(string database) =>
        long.Parse((await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT count(*)::text FROM audit_events WHERE action LIKE 'session.%'"))!);

    /// <summary>Every stored session value, so a test can prove a plaintext token is not among them.</summary>
    private static async Task<string> ReadAllSessionRowsAsync(string database) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(database, "SELECT id_hash FROM session")) ?? string.Empty;

    private static async Task<string?> ReadAntiforgeryAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString();
    }

    private static async Task<string?> ReadActorIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("actorId").GetString();
    }

    /// <summary>Reads a failure body and pins the envelope every failure in this repository uses.</summary>
    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["code", "message", "traceId"], fields);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static HttpRequestMessage RequestWithCookie(string cookie, string path = "/api/session")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookieName}={cookie}");

        return request;
    }

    private static HttpRequestMessage Mutation(string cookie, string? antiforgery, string origin) =>
        MutationWithOrigin(cookie, antiforgery, origin);

    private static HttpRequestMessage MutationWithOrigin(string cookie, string? antiforgery, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/session")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("Cookie", $"{SessionCookieName}={cookie}");
        if (origin.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (antiforgery is not null)
        {
            request.Headers.Add(AntiforgeryHeaderName, antiforgery);
        }

        return request;
    }

    private static string CookieValue(SetCookieAttributes cookie) =>
        cookie.Raw.Split(';')[0].Split('=', 2)[1];

    /// <summary>
    /// Parses a real <c>Set-Cookie</c> header into its observable attributes. Nothing here reads a
    /// production type: the assertions are about the bytes on the wire.
    /// </summary>
    private static SetCookieAttributes ParseSetCookie(string setCookie)
    {
        var attributes = setCookie.Split(';').Select(part => part.Trim()).ToArray();
        var nameValue = attributes[0].Split('=', 2);

        return new SetCookieAttributes(
            nameValue[0],
            setCookie,
            Flag(attributes, "httponly"),
            Value(attributes, "samesite="),
            Flag(attributes, "secure"),
            Value(attributes, "path="),
            ExpiryOf(Value(attributes, "expires=")));
    }

    private static string? Flag(IReadOnlyList<string> attributes, string name) =>
        attributes.Any(attribute => attribute.Equals(name, StringComparison.OrdinalIgnoreCase)) ? name : null;

    private static string? Value(IReadOnlyList<string> attributes, string prefix) =>
        attributes
            .FirstOrDefault(attribute => attribute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(prefix.Length);

    private static DateTimeOffset? ExpiryOf(string? value) =>
        value is not null && DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : null;

    private static string[] Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return [.. messages];
    }

    private sealed record SetCookieAttributes(
        string Name,
        string Raw,
        string? HttpOnly,
        string? SameSite,
        string? Secure,
        string? Path,
        DateTimeOffset? Expires);
}

/// <summary>A UTC clock the test moves forward, so expiry is proven without waiting for it.</summary>
internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
