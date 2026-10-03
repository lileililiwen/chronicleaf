// Chronicleaf runtime shell (SHELL-001/SHELL-002/SHELL-003 in
// openspec/specs/runtime-shell/spec.md): a credential-free API liveness contract plus
// static hosting of the built React client from this repository's own build output.
// Durable Community state and honest dependency readiness come from the persistence package
// (COMMUNITY-PERSISTENCE-001/002); the single-user access boundary is
// COMMUNITY-ACCESS-001/002. This file stays the composition root and adds no product
// feature of its own.
using Chronicleaf.Api;
using Chronicleaf.Api.Access;
using Chronicleaf.Api.Common;
using Chronicleaf.Api.Evidence;
using Chronicleaf.Api.Media;
using Chronicleaf.Api.Operations;
using Chronicleaf.Api.Projects;
using Chronicleaf.Infrastructure.Access;
using Chronicleaf.Infrastructure.Evidence;
using Chronicleaf.Infrastructure.Media;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// The built React client lives in the API's wwwroot after the explicit publish step
// (scripts/publish.sh, also used by the Dockerfile). The root is resolved once at start-up
// and every routing decision below is a real filesystem lookup against it.
var webAssets = WebAssetCatalog.Resolve(builder.Configuration, builder.Environment);

// COMMUNITY-PERSISTENCE-001/002. Registration validates a configured connection string and
// fails start-up with a redacted message when it is unusable. It never migrates the database
// and never installs a substitute store: the operator runs the explicit `dotnet ef database
// update` command, and an unreachable or unconfigured database is reported at /api/ready.
builder.Services.AddChronicleafPersistence(builder.Configuration);

// COMMUNITY-ACCESS-001/002. Registration requires the operator's access token hash outside
// Development and fails start-up with a redacted message when it is absent, because a
// boundary that cannot verify a token must not pretend to exist. It registers the session
// store, the durable login throttle and the start-up token-rotation guard.
builder.Services.AddChronicleafAccess(builder.Configuration, builder.Environment.EnvironmentName);

// PROJECTS-AND-MILESTONES-001/002. The owner's own Project and Milestone records, stored by the
// same context, unit of work and audit trail the packages above delivered, with no store of its
// own to fall back on: a database without the projects-and-milestones migration reports
// schema_mismatch at /api/ready rather than serving an empty installation.
builder.Services.AddProjectMilestones();

// BACKGROUND-JOBS-001/002. Durable operation execution: the same PostgreSQL database as everything
// else, in its own schema, with no in-memory fallback. Registration refuses to start when there is no
// connection string or when the dashboard is asked for, because a queue that only lives inside one
// process cannot honour what this package promises. A database without the background-operations
// migration reports schema_mismatch at /api/ready.
builder.Services.AddChronicleafOperationQueue(builder.Configuration);

// LOCAL-MEDIA-STORAGE-001/002. Private, scoped filesystem media over the adopted shared storage
// package. The MediaRoot is resolved once at start-up from Chronicleaf__MediaRoot, defaults to
// runtime/media and is refused outright if it is the published wwwroot or inside it - stored images
// must never become static assets. Keys are generated here, so no request can choose a path. A
// database without the local-media-storage migration reports schema_mismatch at /api/ready.
builder.Services.AddChronicleafMedia(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    webAssets.WebRootPath);

// MILESTONE-EVIDENCE-001/002. Private, append-only provenance snapshots over real PostgreSQL. It
// also replaces the media package's default media-reference checker with the real one, because the
// evidence tables are what references now exist in - so the 409 media_in_use the media package
// already answered becomes a statement about stored evidence rather than about a stub. Registration
// order does not matter: it removes and replaces rather than trying to add first.
builder.Services.AddMilestoneEvidence();

// The rotation guard is applied once at start-up, so an access token that was changed since the
// last run revokes every existing session before this process serves anything.
builder.Services.AddHostedService<AccessTokenRotationStartup>();

var app = builder.Build();

// The static file middleware runs before endpoint routing on purpose: in .NET 10 it declines
// any request that routing has already matched, and the catch-all client route below matches
// every path that is not an existing file. Existing files are therefore served here, and
// everything else falls through to the endpoints.
if (Directory.Exists(webAssets.WebRootPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webAssets.WebRootPath),
    });
}

app.UseRouting();

// COMMUNITY-PERSISTENCE: one envelope for the failures this repository raises. It wraps the
// endpoints below and leaves the shell's own 404/503 bodies untouched.
app.UseMiddleware<ApiErrorMiddleware>();

// COMMUNITY-ACCESS-001/002: the default-deny boundary. It runs after routing, so it can see
// which endpoint matched, and it refuses a private route or a mutation before the endpoint
// body runs - which is what makes a refused request a request that wrote nothing. Its own
// failures are raised as access exceptions and translated by the middleware above.
app.UseMiddleware<OwnerSessionBoundaryMiddleware>();

// SHELL-001: liveness only. No database, provider, version or environment detail is exposed,
// and it keeps answering 200 when PostgreSQL is unreachable or unconfigured.
app.MapGet("/api/health", () => Results.Json(HealthResponse.Instance)).AllowPublicApi();

// COMMUNITY-PERSISTENCE-002: readiness. 200 {"status":"ready"} only for a reachable, current
// schema; otherwise 503 database_unavailable or schema_mismatch.
app.MapGet("/api/ready", ReadinessEndpoint.CheckAsync).AllowPublicApi();

// COMMUNITY-ACCESS-001: the owner session routes. POST /api/session is the one of them that
// is public - it is the exchange itself - and the other two are private by default.
app.MapAccessEndpoints();

// PROJECTS-AND-MILESTONES-001/002: the private project and milestone routes. None of them is
// marked public, so the access package's default-deny boundary already covers every one of them
// and the public allow-list stays exactly as the design fixed it.
app.MapProjectMilestoneEndpoints();

// BACKGROUND-JOBS-001/002: the private operation routes. None is marked public, so the access
// package's default-deny boundary already covers every one of them and the public allow-list stays
// exactly as the design fixed it.
app.MapOperationEndpoints();

// LOCAL-MEDIA-STORAGE-001/002: the private media routes. None is marked public, so the access
// package's default-deny boundary already covers every one of them and the public allow-list stays
// exactly as the design fixed it - there is no public media route and no directory listing.
app.MapMediaEndpoints();

// MILESTONE-EVIDENCE-001/002: the private evidence routes. Neither is marked public, so the access
// package's default-deny boundary already covers both and the public allow-list stays exactly as the
// access package fixed it - evidence is readable only by the owner it belongs to.
app.MapMilestoneEvidenceEndpoints();

// SHELL-001/SHELL-002: every unmatched path is either an unknown API path, an unknown
// static asset, or a client route. Only the last one falls back to the SPA entry point. This
// endpoint serves no stored data and is the shell's shipped 404, so it is public.
app.MapGet("/", (HttpContext context) => WebAssetRouting.ServeClientRoute(context, webAssets)).AllowPublicApi();
app.MapFallback("{**clientRoute}", (HttpContext context) => WebAssetRouting.ServeClientRoute(context, webAssets)).AllowPublicApi();

app.Run();

// Exposed so tests/Chronicleaf.Api.Tests can host this application with
// WebApplicationFactory<Program>.
public partial class Program
{
}
