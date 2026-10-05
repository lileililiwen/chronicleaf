// Chronicleaf runtime shell (SHELL-001/SHELL-002/SHELL-003 in
// openspec/specs/runtime-shell/spec.md): a credential-free API liveness contract plus
// static hosting of the built React client from this repository's own build output.
// Durable Community state and honest dependency readiness come from the persistence package
// (COMMUNITY-PERSISTENCE-001/002); the single-user access boundary is
// COMMUNITY-ACCESS-001/002. This file stays the composition root and adds no product
// feature of its own.
using Chronicleaf.Api;
using Chronicleaf.Api.Access;
using Chronicleaf.Api.AI;
using Chronicleaf.Api.Capture;
using Chronicleaf.Api.Common;
using Chronicleaf.Api.Content;
using Chronicleaf.Api.Curation;
using Chronicleaf.Api.Evidence;
using Chronicleaf.Api.Git;
using Chronicleaf.Api.GitHub;
using Chronicleaf.Api.GitHubEnrichment;
using Chronicleaf.Api.Candidates;
using Chronicleaf.Api.Media;
using Chronicleaf.Api.Operations;
using Chronicleaf.Api.Projects;
using Chronicleaf.Api.Secrets;
using Chronicleaf.Api.Timeline;
using Chronicleaf.Api.Voice;
using Chronicleaf.AI.Anthropic;
using Chronicleaf.AI.Gemini;
using Chronicleaf.AI.Ollama;
using Chronicleaf.AI.OpenAI;
using Chronicleaf.AI.OpenRouter;
using Chronicleaf.Infrastructure.Access;
using Chronicleaf.Infrastructure.AI;
using Chronicleaf.Infrastructure.Capture;
using Chronicleaf.Infrastructure.Content;
using Chronicleaf.Infrastructure.Curation;
using Chronicleaf.Infrastructure.Evidence;
using Chronicleaf.Infrastructure.Git;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.GitHubEnrichment;
using Chronicleaf.Infrastructure.GitHubCandidates;
using Chronicleaf.Infrastructure.Media;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Chronicleaf.Infrastructure.Secrets;
using Chronicleaf.Infrastructure.Voice;
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

// MILESTONE-TIMELINE-001/002. The private chronological read over the milestone records above.
// It keeps no table of its own: the projects-and-milestones migration is its schema, and a
// database without it reports schema_mismatch at /api/ready.
builder.Services.AddTimeline();

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

// CREDENTIAL-VAULT-001/002. Sealed provider credentials over the same PostgreSQL, unit of work,
// audit trail and operation queue the packages above delivered. The key is read from the operator's
// Chronicleaf__EncryptionKeyFile, which is deliberately not a start-up gate: until it is configured,
// every credential route refuses with credential_vault_unavailable and nothing is ever stored in the
// clear. A database without the credential-vault migration reports schema_mismatch at /api/ready.
builder.Services.AddCredentialVault(builder.Configuration);

// VOICE-PROFILES-001/002. The owner's writing voice, its presets and the deterministic checker
// that judges text against it, over the same PostgreSQL, unit of work and audit trail the packages
// above delivered. There is no model in this registration and nothing to call: the checker is rule
// evaluation, so there is no provider to configure and no key to go missing. A database without the
// voice-profiles migration reports schema_mismatch at /api/ready.
builder.Services.AddVoiceProfiles();

// LLM-PROVIDER-CONTRACT-001/002. The provider-neutral bounded generation contract, the store for
// owner-authorized redacted snapshots, and the routes that run one bounded call. This registration
// adds NO provider adapter and no key: the five adapters are separate roadmap packages, each of
// which will register its own ILLMProvider here. Until one does, the resolver is built over an empty
// set and every provider answers provider_not_installed before a credential, a socket or a token is
// involved - which is the honest state of a contract with no transports, and what a test asserts
// rather than assumes. A database without the generation-previews migration reports schema_mismatch
// at /api/ready.
builder.Services.AddAIGeneration();

// LLM-OPENAI-ADAPTER-001/002. The first real transport behind the generation contract: one
// ILLMProvider for the `openai` slot, a fixed HTTPS origin with redirects refused, and a leased
// key that is used for one header and returned. This is the only registration in the repository that
// makes a provider reachable, and it is also the only lever that un-installs one - there is no flag
// that removes it at runtime, so "which providers can this deployment call" is answered by what was
// built and deployed rather than by a value. The accepted-model allowlist is configuration and is
// empty unless an operator filled it in, so an untouched deployment refuses every model before a
// credential is leased; that is the honest state of a transport nobody has pointed at anything. The
// four remaining slots are still separate roadmap packages and stay unclaimed.
builder.Services.AddSingleton<IOpenAIApiKeySource, VaultOpenAIApiKeySource>();
builder.Services.AddOpenAIProvider(builder.Configuration);

// LLM-GEMINI-ADAPTER-001/002. The second real transport behind the generation contract: one
// ILLMProvider for the `gemini` slot, a fixed HTTPS origin with redirects refused, and a leased
// key that is used for one header and returned. The same shape as the OpenAI registration above,
// and the same rollback rule: removing it un-installs the transport and leaves stored previews,
// stored credentials and stored audit rows where they are. The accepted-model allowlist is
// configuration and is empty unless an operator filled it in. The three remaining slots are still
// separate roadmap packages and stay unclaimed.
builder.Services.AddSingleton<IGeminiApiKeySource, VaultGeminiApiKeySource>();
builder.Services.AddGeminiProvider(builder.Configuration);

// LLM-ANTHROPIC-ADAPTER-001/002. The third real transport behind the generation contract: one
// ILLMProvider for the `anthropic` slot, a fixed HTTPS origin with redirects refused, and a leased
// key that is used for one header and returned. The same shape as the two registrations above,
// and the same rollback rule: removing it un-installs the transport and leaves stored previews,
// stored credentials and stored audit rows where they are. The accepted-model allowlist is
// configuration and is empty unless an operator filled it in. The one remaining slot is still
// a separate roadmap package and stays unclaimed.
builder.Services.AddSingleton<IAnthropicApiKeySource, VaultAnthropicApiKeySource>();
builder.Services.AddAnthropicProvider(builder.Configuration);

// LLM-OPENROUTER-ADAPTER-001/002. The fourth real transport behind the generation contract: one
// ILLMProvider for the `openrouter` slot, a fixed HTTPS origin with redirects refused, and a leased
// key that is used for one bearer header and returned. The same shape as the three registrations
// above, and the same rollback rule: removing it un-installs the transport and leaves stored
// previews, stored credentials and stored audit rows where they are. The accepted-model allowlist is
// configuration and is empty unless an operator filled it in. The one remaining slot is still a
// separate roadmap package and stays unclaimed.
builder.Services.AddSingleton<IOpenRouterApiKeySource, VaultOpenRouterApiKeySource>();
builder.Services.AddOpenRouterProvider(builder.Configuration);

// LLM-OLLAMA-ADAPTER-001/002. The fifth real transport behind the generation contract: one
// ILLMProvider for the `ollama` slot, addressing only an operator-allowlisted origin's chat
// resource with redirects refused, and an optional leased key that is used for one bearer header
// and returned. A local Ollama needs no key, so a consent that names no credential is a keyless
// call rather than a refusal. The same shape as the four registrations above, and the same
// rollback rule: removing it un-installs the transport and leaves stored previews, stored
// credentials and stored audit rows where they are. The accepted-model and accepted-endpoint
// allowlists are configuration and are empty unless an operator filled them in, so an untouched
// deployment refuses every model and every endpoint before a socket is opened. All five slots are
// now claimed.
builder.Services.AddSingleton<IOllamaApiKeySource, VaultOllamaApiKeySource>();
builder.Services.AddOllamaProvider(builder.Configuration);

// GITHUB-EVIDENCE-001/002. The read-only GitHub transport, pinned to the API host with redirects
// refused, plus the two stores, the service and the one job handler that appends collected evidence
// to a milestone. There is no key to configure here: a token is optional, is stored by the delivered
// vault under the `github` provider and is only ever a reference in this package. Nothing is ever
// written to GitHub - the transport has no verb that could. A database without the github-evidence
// migration reports schema_mismatch at /api/ready.
builder.Services.AddGitHubEvidence();

// GITHUB-RELEASE-CANDIDATES-001/002. The read-only candidate transport - a third registration name,
// so it shadows neither the hosted evidence transport nor the local one - plus four stores, the service,
// the one job handler that performs a poll and the one outbox consumer that turns an accepted delivery
// into candidates. There is no key to configure here: a webhook secret is a delivered vault credential
// under the `github-webhook` provider, stored by the owner, and this package only ever holds a
// reference to it and a lease for the length of one comparison. Nothing is ever written to GitHub - the
// transport exposes no verb that could - and nothing here confirms a candidate, appends evidence or
// publishes. A database without the github-release-candidates migration reports schema_mismatch at
// /api/ready.
builder.Services.AddGitHubCandidates();

// GITHUB-GRAPHQL-ENRICHMENT-001/002. The optional relationship reader over the delivered GitHub
// transport - no second HttpClient, so no second host, redirect policy or timeout to disagree
// about - plus the run store, the service and the one job handler that appends attributed
// issue/PR relationships to already-collected evidence. There is no key to configure here: a
// token is the delivered vault's under the `github` provider and is only ever a lease for the
// length of one read. Nothing is ever written to GitHub - the reader posts one fixed query and
// nothing else - and nothing here confirms a candidate or publishes. A database without the
// github-graphql-enrichment migration reports schema_mismatch at /api/ready.
builder.Services.AddGitHubGraphqlEnrichment();

// LOCAL-GIT-EVIDENCE-001/002. The read-only Git transport, which runs four Git verbs and refuses every
// other, plus the two stores, the service and the one job handler that appends collected evidence to a
// milestone. The registered roots are the operator's own configuration (`Chronicleaf:LocalRepositoryRoots`)
// and there is no route that adds one and no request field that could widen the set: a caller names an
// alias and a path relative to it, and the server resolves that inside the registered directory or
// refuses it. There is no credential here at all - a local repository is read with the operator's own Git
// and has no remote to authenticate to - and nothing is ever written to a repository, because the runner
// will not start a Git subcommand that changes state. A database without the local-git-evidence migration
// reports schema_mismatch at /api/ready.
builder.Services.AddLocalGitEvidence(builder.Configuration);

// SCREENSHOT-CAPTURE-001/002. The two stores, the service, the one job handler, and the production
// browser - the sandbox-backed Playwright provider, which is registered here and nowhere else. The
// provider refuses on a host that cannot confine a browser rather than falling back to unrestricted
// networking, and there is no setting that changes that: the only configuration this reads is where the
// packaged Chromium is and which deployment mode this is. A database without the screenshot-capture
// migration reports schema_mismatch at /api/ready.
builder.Services.AddScreenshotCapture(builder.Configuration);

// SCREENSHOT-CURATION-001/002. The store and service that move the candidate table's curation
// columns and publish a selection into the milestone's evidence. The table stays capture's and
// the bundle stays evidence's: this registration owns the choice between them. A database without
// the screenshot-curation migration reports schema_mismatch at /api/ready.
builder.Services.AddScreenshotCuration();

// GROUNDED-MARKDOWN-DRAFTS-001/002. The two stores, the service and the one job handler that turns a
// consented, evidence-grounded call into a Markdown draft for a human to review. It registers no
// provider, no key and no setting: the adapter packages own reaching a provider, through the same
// ILLMProvider seam the delivered resolver already reads, so a deployment with no adapter installed
// lists the slots honestly and cannot reach any of them. Nothing here can approve a draft - there is no
// approval registration to remove and no configuration that would install one, because approval is the
// review package's table bound to a revision and a body hash. A database without the
// grounded-markdown-drafts migration reports schema_mismatch at /api/ready.
builder.Services.AddGroundedMarkdownDrafts();

// MARKDOWN-REVIEW-EXPORT-001/002. The approvals store and the review service: human edits that
// append revisions, approvals bound to a revision and a body hash, and exports that serve the
// approved bytes only while every binding still holds. It registers no provider, no publisher
// and no setting: an export serves bytes to the owner that asked for them, and publication is a
// later package's reviewed adapter. A database without the markdown-review-export migration
// reports schema_mismatch at /api/ready.
builder.Services.AddMarkdownReviewExport();

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

// MILESTONE-TIMELINE-001/002: the private chronological timeline of one project. It is not marked
// public, so the access package's default-deny boundary already covers it and the public allow-list
// stays exactly as the access package fixed it - a timeline is readable only by the owner it
// belongs to, and an archived project's history stays readable.
app.MapTimelineEndpoints();

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

// CREDENTIAL-VAULT-001/002: the private settings routes for provider credentials. None of them is
// marked public, so the access package's default-deny boundary already covers every one of them and
// the public allow-list stays exactly as the access package fixed it. No route here returns a secret,
// on success or on failure.
app.MapCredentialEndpoints();

// VOICE-PROFILES-001/002: the private voice routes. None of them is marked public, so the access
// package's default-deny boundary already covers every one of them and the public allow-list stays
// exactly as the access package fixed it - a project's voice is readable only by the owner it
// belongs to.
app.MapVoiceEndpoints();

// LLM-PROVIDER-CONTRACT-001/002: the private generation routes. None of them is marked public, so the
// access package's default-deny boundary already covers every one of them. No route here returns a
// secret, and no route returns provider prose: a provider failure becomes one of this package's own
// authored codes and messages.
app.MapAIGenerationEndpoints();

// GITHUB-EVIDENCE-001/002: the private connection and collection routes. None of them is marked
// public, so the access package's default-deny boundary already covers every one of them. No route
// here accepts or returns a URL and none returns a credential: a repository is an owner and a name,
// and the host is a constant of the transport.
app.MapGitHubEvidenceEndpoints();

// GITHUB-RELEASE-CANDIDATES-001/002: the private candidate, poll and subscription routes, plus the one
// public delivery route. The five private ones are not marked public, so the access package's
// default-deny boundary already covers every one of them. The delivery route is marked public
// deliberately and is the single place in this repository that an owner session does not protect: its
// caller is GitHub, it has no session, and the HMAC signature over its own raw bytes is what
// authenticates it. Nothing in this file confirms a candidate, and no route here returns a secret.
app.MapGitHubCandidateEndpoints();

// GITHUB-GRAPHQL-ENRICHMENT-001/002: the private enrichment request route. It is not marked
// public, so the access package's default-deny boundary already covers it. No route here accepts
// or returns a URL, a ref or a SHA: the SHAs an enrichment queries are the ones the milestone's
// own bundle already carries, and the route carries only the evidence revision and the caller's
// idempotency key.
app.MapGitHubEnrichmentEndpoints();

// LOCAL-GIT-EVIDENCE-001/002: the private root catalogue, registration and collection routes. None of
// them is marked public, so the access package's default-deny boundary already covers every one of them.
// No route here accepts an absolute path and none returns one: a registration is an operator-registered
// alias plus a relative path, and the directory behind the alias stays in the operator's configuration.
app.MapLocalGitEvidenceEndpoints();

// SCREENSHOT-CAPTURE-001/002: the private capture and candidate routes. Neither is marked public, so
// the access package's default-deny boundary already covers both. This is the one surface in the
// repository whose body names a host the server will then connect to, and it is the URL policy rather
// than this boundary that makes it safe: a forbidden destination is refused with 403 before a browser
// is started, and the read route publishes candidate provenance without any storage key.
app.MapCaptureEndpoints();

// SCREENSHOT-CURATION-001/002: the private curation routes. Neither is marked public, so the access
// package's default-deny boundary already covers both. No route here captures anything and none
// approves anything: a selection is evidence a human may act on, and approval is the review
// package's.
app.MapCurationEndpoints();

// GROUNDED-MARKDOWN-DRAFTS-001/002: the private draft request and read routes. None of them is marked
// public, so the access package's default-deny boundary already covers every one of them. There is no
// approve route here and no field that could report one: a draft is text a human has not reviewed yet,
// and the review package owns the table that will record that decision.
app.MapContentDraftEndpoints();

// MARKDOWN-REVIEW-EXPORT-001/002: the private edit, approve, unapprove, review and export
// routes. None of them is marked public, so the access package's default-deny boundary already
// covers every one of them. There is no generation here and no publication: an edit appends a
// human revision, an approval binds a revision and a body hash, and an export serves approved
// bytes as an attachment the owner chose to download.
app.MapContentReviewEndpoints();

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
