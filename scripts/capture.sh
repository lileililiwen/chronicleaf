#!/usr/bin/env bash
# Capture real product screenshots for docs/assets/capture-plan.md.
#
# This is evidence, not a demo. Every PNG it produces is a screenshot of the real published
# application, served by the real API, backed by a real disposable PostgreSQL, holding records the
# product itself created during this run:
#
#   * scripts/publish.sh builds the client and publishes the API, and the published process is the
#     one the browser is pointed at - the same relationship scripts/e2e.sh uses.
#   * A disposable PostgreSQL container is started for this run, the committed migrations are
#     applied with the operator command, and the single installation is provisioned.
#   * A fresh random access token is generated for this run. Its hash is the server's
#     Chronicleaf__AccessTokenHash; the plaintext is only ever typed into the local sign-in form by
#     the browser. It is never printed, never logged and never written to a tracked file.
#   * The browser drives the product the way a person would: it signs in on the local session form,
#     creates a project, and records a milestone. One candidate milestone is then handed to the
#     real intake route the product exposes for a source's records, from inside the page, with this
#     browser's own session cookie - the same call the repository's own browser fixture makes,
#     because no product surface collects records from a source yet.
#   * Nothing is inserted into the database to make a screenshot look better. The installation row is
#     the one operator-provisioned singleton every installation has; the project, the confirmed
#     milestone and the candidate are all created by the product's own flows during this run.
#
# Viewports follow docs/assets/capture-plan.md: desktop 1440x900 and mobile 390x844.
#
# Everything temporary lives in a mktemp directory under /tmp and is removed on exit, whether the
# run succeeded or failed. PNGs are staged there first and copied into docs/assets/ only after every
# capture and digest has succeeded, so a failed run can never leave an artifact that looks like a
# capture that happened.
#
# The script prints a machine-readable evidence block (git revision, the commands run, and the
# sha256 of each PNG and of the built client artifacts that were served) to stdout. That block is
# evidence for the documentation record; this script never writes it into the documentation.
#
# Prerequisites, all required - the script fails loudly rather than degrading:
#   docker + a reachable daemon, dotnet 10 with the EF tool, node, npm, openssl, curl, git,
#   sha256sum, and the Playwright chromium build the repository's own @playwright/test expects.
#   Nothing is installed here. A missing browser is reported, never fetched.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="${CHRONICLEAF_CAPTURE_PUBLISH_DIR:-dist/capture}"
base_url="${CHRONICLEAF_CAPTURE_BASE_URL:-http://127.0.0.1:5081}"
configuration="Release"
web_rel="src/Chronicleaf.Web"
assets_rel="docs/assets"
database_name="chronicleaf_capture"
captures=(
  chronicleaf-dashboard-empty-desktop.png
  chronicleaf-project-milestones-desktop.png
  chronicleaf-project-milestones-mobile.png
)

# The image is pinned, exactly as scripts/e2e.sh pins it: a floating tag would make this run
# unreproducible.
postgres_image="postgres:17.6-alpine"
postgres_container="chronicleaf-capture-postgres-$$"
postgres_port=""

log_file="$repo_root/$publish_dir/server.log"
token_file="$repo_root/$publish_dir/access-token"
run_dir=""
server_pid=""
commands=()

record() {
  commands+=("$1")
  echo "==> $1"
}

cleanup() {
  local exit_status=$?
  if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  if [ -n "$postgres_container" ]; then
    docker rm --force --volumes "$postgres_container" >/dev/null 2>&1 || true
  fi
  # The token is a secret for the lifetime of this run and must not survive it.
  rm -f "$token_file"
  if [ -n "$run_dir" ]; then
    rm -rf "$run_dir"
  fi
  exit "$exit_status"
}
trap cleanup EXIT

fail() {
  echo "FAILED: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "(prerequisite unavailable): '$1' is not on PATH."
}

require_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    fail "(prerequisite unavailable): docker is not on PATH; a real PostgreSQL container is the only acceptable database for this capture."
  fi
  if ! docker info >/dev/null 2>&1; then
    fail "(prerequisite unavailable): the docker daemon is not reachable; a real PostgreSQL container is the only acceptable database for this capture."
  fi
}

cd "$repo_root"

for tool in docker dotnet node npm openssl curl git sha256sum; do
  require_command "$tool"
done
require_docker

[ -d "$web_rel/node_modules/@playwright/test" ] ||
  fail "(prerequisite unavailable): $web_rel/node_modules/@playwright/test is missing. Run 'npm ci' in $web_rel first; this script installs nothing."
[ -f "$web_rel/playwright.config.ts" ] ||
  fail "(prerequisite unavailable): $web_rel/playwright.config.ts is missing."

# The repository's own Playwright must be able to launch the browser it is pinned to. Reported, not
# installed: a missing build is a fact about this machine, not something to paper over.
CAPTURE_WEB_DIR="$repo_root/$web_rel" node --input-type=module -e '
  import { createRequire } from "node:module";
  import { existsSync } from "node:fs";
  import { join } from "node:path";
  const require = createRequire(join(process.env.CAPTURE_WEB_DIR, "package.json"));
  const { chromium } = require("@playwright/test");
  const executable = chromium.executablePath();
  if (!existsSync(executable)) {
    console.error(
      "FAILED (prerequisite unavailable): the Playwright chromium build is not installed at " +
        executable +
        ". This script installs no browser; install it deliberately, then run again.",
    );
    process.exit(1);
  }
' || fail "(prerequisite unavailable): the repository's Playwright cannot launch chromium."

run_dir="$(mktemp -d /tmp/chronicleaf-capture.XXXXXX)"
png_facts="$run_dir/pngs.txt"
: >"$png_facts"

# The origin must be free. A server this script did not start would answer the readiness probe, and
# the captures would then be of something other than what this run published.
base_host_port="${base_url#*://}"
if (exec 3<>"/dev/tcp/${base_host_port%%/*}/${base_host_port##*/}") 2>/dev/null; then
  fail "something is already listening on $base_host_port; set CHRONICLEAF_CAPTURE_BASE_URL to a free loopback port so the captures are of this run's application and nothing else."
fi

# The content the capture records is drawn from this repository's own history, so the screenshots
# show facts a reader can check rather than invented history: the two most recent feat() commits.
# If the history changes shape the run stops, rather than inventing substitutes.
mapfile -t feature_commits < <(TZ=UTC git log --grep='^feat(' --format='%H%x09%h%x09%cd%x09%s' --date=format-local:'%Y-%m-%dT%H:%M' -n 2)
[ "${#feature_commits[@]}" -eq 2 ] ||
  fail "expected exactly two recent feat() commits to describe the captured content, found ${#feature_commits[@]}."

IFS=$'\t' read -r newest_full newest_short newest_at newest_subject <<<"${feature_commits[0]}"
IFS=$'\t' read -r previous_full previous_short previous_at previous_subject <<<"${feature_commits[1]}"

# The milestone title is the commit's own subject with the conventional-commit prefix dropped. The
# occurred-at value is that commit's own date in UTC, because the product stores what the form says
# and renders it in the reader's zone.
newest_title="${newest_subject#*: }"
previous_title="${previous_subject#*: }"
web_version="$(node -p "require('$repo_root/$web_rel/package.json').version")"

mkdir -p "$repo_root/$assets_rel"

record "git rev-parse HEAD"
git rev-parse HEAD
record "bash scripts/publish.sh $publish_dir $configuration"
bash "$repo_root/scripts/publish.sh" "$publish_dir" "$configuration"

record "docker run --detach --rm --name $postgres_container --env POSTGRES_DB=$database_name --env POSTGRES_USER=chronicleaf --env POSTGRES_PASSWORD=chronicleaf-capture --publish 127.0.0.1::5432 $postgres_image"
docker run --detach --rm \
  --name "$postgres_container" \
  --env POSTGRES_DB="$database_name" \
  --env POSTGRES_USER=chronicleaf \
  --env POSTGRES_PASSWORD=chronicleaf-capture \
  --publish 127.0.0.1::5432 \
  "$postgres_image" >/dev/null

postgres_port="$(docker port "$postgres_container" 5432/tcp | head -n 1 | sed 's/.*://')"
if [ -z "$postgres_port" ]; then
  echo "the disposable PostgreSQL did not publish a port:" >&2
  docker logs "$postgres_container" >&2 || true
  exit 1
fi

record "docker exec $postgres_container pg_isready --username=chronicleaf --dbname=$database_name  (waited for, up to 60s)"
ready=0
for _ in $(seq 1 60); do
  if docker exec "$postgres_container" pg_isready --username=chronicleaf --dbname="$database_name" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[ "$ready" -eq 1 ] || fail "the disposable PostgreSQL did not become ready within 60s."

# pg_isready answers as soon as the postmaster accepts connections, which is a moment before the
# first real query can succeed. A query is asked for as well, so the run does not discover the
# difference in the middle of a migration.
record "docker exec $postgres_container psql --username=chronicleaf --dbname=$database_name --command 'SELECT 1'  (waited for, up to 60s)"
queryable=0
for _ in $(seq 1 60); do
  if docker exec "$postgres_container" psql --username=chronicleaf --dbname="$database_name" --quiet \
    --command 'SELECT 1' >/dev/null 2>&1; then
    queryable=1
    break
  fi
  sleep 1
done
[ "$queryable" -eq 1 ] || fail "the disposable PostgreSQL accepted no query within 60s of becoming ready."

connection_string="Host=127.0.0.1;Port=${postgres_port};Database=${database_name};Username=chronicleaf;Password=chronicleaf-capture;Timeout=5;Command Timeout=10"

# Migrations are applied by the operator command, never at start-up. The token hash used here is a
# fixed placeholder: it is only needed so the design-time host can construct its services.
#
# A brand new container can refuse the very first connection from the host a moment after it answers
# a local one, so the operator command is retried a bounded number of times. Three attempts is not
# tolerance of a failure: if none of them completes, the run stops and prints what the command said.
record "ConnectionStrings__Chronicleaf=<loopback container> dotnet ef database update --project src/Chronicleaf.Infrastructure --startup-project src/Chronicleaf.Api --no-build  (up to 3 attempts)"
migration_log="$run_dir/migration.log"
migrated=0
for attempt in 1 2 3; do
  if (
    cd "$repo_root"
    ConnectionStrings__Chronicleaf="$connection_string" \
      Chronicleaf__AccessTokenHash="$(head -c 64 /dev/zero | tr '\0' '0' | sha256sum | cut -d' ' -f1)" \
      dotnet ef database update \
        --project src/Chronicleaf.Infrastructure \
        --startup-project src/Chronicleaf.Api \
        --no-build
  ) >"$migration_log" 2>&1; then
    migrated=1
    break
  fi
  echo "    migration attempt $attempt did not complete; retrying" >&2
  sleep 5
done
if [ "$migrated" -ne 1 ]; then
  cat "$migration_log" >&2
  fail "the committed migrations could not be applied to the disposable PostgreSQL."
fi

# The singleton installation is operator-provisioned state, not something a visitor may claim.
installation_id="$(cat /proc/sys/kernel/random/uuid)"
record "docker exec $postgres_container psql --username=chronicleaf --dbname=$database_name --command \"INSERT INTO installation (id, schema_version, created_at) VALUES (<one random uuid>, 1, now())\""
docker exec "$postgres_container" psql --username=chronicleaf --dbname="$database_name" --quiet --set=ON_ERROR_STOP=1 \
  --command "INSERT INTO installation (id, schema_version, created_at)
             VALUES ('${installation_id}', 1, now())
             ON CONFLICT (id) DO NOTHING;" >/dev/null

# A token for this run only: 32 random bytes. The plaintext goes to a file inside the gitignored
# publish directory for the length of the run, readable by this user only, and deleted when the run
# ends. It is never echoed, never placed on a command line and never written to a tracked file.
access_token="$(openssl rand -hex 32)"
access_token_hash="$(printf '%s' "$access_token" | sha256sum | cut -d' ' -f1)"
(
  umask 077
  printf '%s' "$access_token" >"$token_file"
)
unset access_token

record "(cd $publish_dir && ASPNETCORE_URLS=$base_url ASPNETCORE_ENVIRONMENT=Production ConnectionStrings__Chronicleaf=<loopback container> Chronicleaf__AccessTokenHash=<redacted> Chronicleaf__PublicOrigin=$base_url dotnet Chronicleaf.Api.dll)"
# exec replaces the subshell, so $! is the server process itself and the trap below actually stops it
# instead of orphaning it on the port.
(
  cd "$repo_root/$publish_dir" && exec env \
    ASPNETCORE_URLS="$base_url" \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Chronicleaf="$connection_string" \
    Chronicleaf__AccessTokenHash="$access_token_hash" \
    Chronicleaf__PublicOrigin="$base_url" \
    dotnet Chronicleaf.Api.dll >"$log_file" 2>&1
) &
server_pid=$!

ready=0
record "curl --fail --silent --show-error --max-time 2 $base_url/api/health  (waited for, up to 60s)"
for _ in $(seq 1 60); do
  if curl --fail --silent --show-error --max-time 2 "$base_url/api/health" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    echo "the published application exited before becoming ready:" >&2
    cat "$log_file" >&2
    exit 1
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "the published application did not answer /api/health within 60s:" >&2
  cat "$log_file" >&2
  exit 1
fi

# The content of the records, assembled once, from the shell's own facts. The browser reads this
# file; it contains no secret.
record "node -e <build the captured content from this repository's own history> $run_dir/seed.json"
SEED_PATH="$run_dir/seed.json" \
  node --input-type=module -e '
  import { writeFileSync } from "node:fs";
  const [newestCommit, newestShort, newestAt, newestTitle, previousCommit, previousShort, previousAt, previousTitle, version, path] =
    process.argv.slice(1);
  const seed = {
    project: {
      name: "Chronicleaf",
      description: "The product in this repository, recorded as its own milestones.",
    },
    confirmedMilestone: {
      title: previousTitle,
      type: "feature",
      version: `v${version}`,
      occurredAt: previousAt,
      notes: `Recorded from commit ${previousShort}.`,
      knownLimitations: ["No evidence, capture or media: this page records milestones only."],
      nextSteps: [
        "Evidence bundles, capture and media.",
        "AI drafting, export and publishing to external services.",
      ],
    },
    candidateMilestone: {
      source: "capture-run",
      sourceKey: `capture-run/${newestShort}`,
      title: newestTitle,
      type: "release",
      occurredAt: newestAt,
      notes: "Left as a candidate for the owner to confirm, as every imported record is.",
    },
    provenance: {
      note: "Every value below comes from this repository, not from an invented history.",
      confirmed: {
        commit: previousCommit,
        commitDateUtc: previousAt,
        subject: `feat(milestones): ${previousTitle}`,
        version: { value: `v${version}`, source: "src/Chronicleaf.Web/package.json" },
      },
      candidate: { commit: newestCommit, commitDateUtc: newestAt, subject: `feat(jobs): ${newestTitle}` },
      nextStepsText: "The two entries under Not in this change, as the product itself states them.",
    },
  };
  writeFileSync(path, `${JSON.stringify(seed, null, 2)}\n`);
' "$newest_full" "$newest_short" "$newest_at" "$newest_title" \
  "$previous_full" "$previous_short" "$previous_at" "$previous_title" \
  "$web_version" "$run_dir/seed.json"
[ -s "$run_dir/seed.json" ] || fail "the captured content could not be assembled."

# The browser driver. It is written into the temporary run directory and executed once. It is a
# plain script rather than a Playwright test on purpose: playwright.config.ts matches the fixture
# files under the e2e/ and tests/ directories, and a capture is not a fixture - nothing here adds to
# or changes the suite scripts/verify.sh runs. The suite's own configuration is still the source of
# the origin and of the desktop viewport, and the browser is the repository's own pinned build.
cat >"$run_dir/capture-driver.mjs" <<'CAPTURE_DRIVER_EOF'
/**
 * The browser half of scripts/capture.sh, which writes this file into a temporary directory and runs
 * it once. It drives the real product in a real browser and reports what the browser did.
 *
 * It is a plain script rather than a Playwright test on purpose: playwright.config.ts matches the
 * fixture files under the e2e/ and tests/ directories, and a capture is not a fixture. Nothing here
 * adds to or changes the suite that scripts/verify.sh runs. The suite's own configuration is still
 * the source of the origin and of the desktop viewport, and the browser is the repository's own
 * pinned build.
 */
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const webDir = process.env.CAPTURE_WEB_DIR;
const stageDir = process.env.CAPTURE_STAGE_DIR;
const seedPath = process.env.CAPTURE_SEED_PATH;
const evidencePath = process.env.CAPTURE_EVIDENCE_PATH;
const tokenFile = process.env.CAPTURE_TOKEN_FILE;

for (const [name, value] of Object.entries({ webDir, stageDir, seedPath, evidencePath, tokenFile })) {
  if (!value) {
    throw new Error(`capture driver: the required capture environment ${name} is missing.`);
  }
}

const require = createRequire(join(webDir, 'package.json'));
const { chromium, devices } = require('@playwright/test');
const playwrightVersion = require('@playwright/test/package.json').version;

/** The repository's own Playwright configuration, not a copy of it. */
const config = (await import(join(webDir, 'playwright.config.ts'))).default;
const baseURL = process.env.CAPTURE_BASE_URL ?? config.use.baseURL;
const projectUse = config.projects?.[0]?.use ?? {};
const desktopViewport = projectUse.viewport ?? { width: 1440, height: 900 };
const mobileViewport = { width: 390, height: 844 };

/**
 * The plaintext token is read here, typed into the local sign-in form, and then never written
 * anywhere: not echoed, not logged, not put on a command line, not included in the evidence.
 */
const accessToken = readFileSync(tokenFile, 'utf8');
const seed = JSON.parse(readFileSync(seedPath, 'utf8'));

const DESKTOP = { ...devices['Desktop Chrome'], viewport: desktopViewport };
const MOBILE = { ...devices['Desktop Chrome'], viewport: mobileViewport, isMobile: true, hasTouch: true };

/**
 * Rendering has to be the same every run for the digests to mean anything: one locale, one zone, no
 * animation, no dark scheme. These are properties of the capture, not of the product.
 */
const DETERMINISTIC = {
  locale: 'en-US',
  timezoneId: 'UTC',
  colorScheme: 'light',
  reducedMotion: 'reduce',
  deviceScaleFactor: 1,
};

const facts = {
  schema: 'chronicleaf-capture-browser-evidence/1',
  baseURL,
  playwrightVersion,
  browser: { name: 'chromium' },
  configSource: 'src/Chronicleaf.Web/playwright.config.ts',
  desktopViewport,
  mobileViewport,
  deterministicContextOptions: DETERMINISTIC,
  deviceEmulation: {
    desktop: { name: 'Desktop Chrome', isMobile: false, hasTouch: false },
    mobile: { name: 'Desktop Chrome', isMobile: true, hasTouch: true, deviceScaleFactor: 1 },
  },
  steps: [],
  captures: [],
  checks: {},
  requests: null,
};

const requestLog = [];
const blockedNonLoopback = [];

function note(name, waitFor) {
  facts.steps.push({ name, waitFor });
  console.log(`    - ${name} (wait for ${waitFor})`);
}

/**
 * Every request the browser made, and a hard refusal of anything that is not this run's own
 * loopback origin. A remote font, script or image cannot be loaded silently here: it is recorded,
 * aborted, and reported.
 */
function watchRequests(context) {
  context.on('request', (request) => {
    requestLog.push(`${request.method()} ${request.url()}`);
  });
  context.route('**/*', async (route) => {
    const host = new URL(route.request().url()).hostname;
    if (host === '127.0.0.1' || host === 'localhost' || host === '::1') {
      return route.continue();
    }
    blockedNonLoopback.push(route.request().url());
    console.warn(`    ! blocked a non-loopback request the page attempted: ${route.request().url()}`);
    return route.abort();
  });
}

/** Signs in through the local session form with the keyboard only, exactly as an owner would. */
async function signIn(page) {
  await page.goto(`${baseURL}/`, { waitUntil: 'load' });
  const input = page.getByTestId('session-token-input');
  await input.waitFor({ state: 'visible' });
  await input.focus();
  await page.keyboard.type(accessToken);
  await page.keyboard.press('Enter');
  await page.getByTestId('product-area').waitFor({ state: 'visible' });
}

/** Waits for the page to have finished rendering before anything is photographed. */
async function settle(page) {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(500);
}

/**
 * The pixel size of a written PNG, read out of its IHDR rather than assumed, so the evidence states
 * the real dimensions of the file that exists on disk.
 */
function pngPixels(path) {
  const header = readFileSync(path).subarray(0, 24);

  return { width: header.readUInt32BE(16), height: header.readUInt32BE(20) };
}

/**
 * Photographs the page and records what was on it: the dimensions of the file, and the text a
 * reader can actually see. The visible text is the evidence for the privacy review - it is what the
 * image contains, and nothing can be in the picture that is not in this string.
 */
async function capture(page, file, name, waitFor) {
  await page.getByTestId(waitFor).first().waitFor({ state: 'visible' });
  await settle(page);
  const path = join(stageDir, file);
  await page.screenshot({
    path,
    animations: 'disabled',
    caret: 'hide',
    scale: 'device',
    // The whole rendered page, not only the first screenful. The signed-in shell uses roughly half
    // of a 900px viewport for its own brand and session facts, so a viewport-only capture of a
    // project would show the first milestone and cut the second in half. The viewport is still the
    // one the convention names, and the evidence states both it and the file's real size.
    fullPage: true,
  });

  const milestones = page.locator('[data-testid="milestone"]');
  const count = await milestones.count();
  const visibleText = (await page.locator('body').innerText()).replace(/\r/g, '');
  // The per-installation actor id is the one thing in the image that is random per run. Redacting it
  // here lets two runs be compared for everything else, so a difference in the PNG digests can be
  // attributed rather than guessed at.
  const withoutActor = visibleText.replace(
    /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi,
    '<the owner actor uuid>',
  );
  const sha = (text) => createHash('sha256').update(text).digest('hex');
  const pixels = pngPixels(path);

  console.log(`    - captured ${file}: ${pixels.width}x${pixels.height} from a ${page.viewportSize().width}x${page.viewportSize().height} viewport`);
  facts.captures.push({
    file,
    name,
    waitFor,
    viewport: page.viewportSize(),
    fullPage: true,
    pngPixels: `${pixels.width}x${pixels.height}`,
    milestonesOnPage: count,
    visibleText,
    visibleTextSha256: sha(visibleText),
    visibleTextSha256WithoutActorUuid: sha(withoutActor),
  });
}

const browser = await chromium.launch();

// -------------------------------------------------------------------------------------------
// Desktop: the honest first-run state, then a project the owner created during this run.
// -------------------------------------------------------------------------------------------
const desktop = await browser.newContext({ ...DESKTOP, ...DETERMINISTIC });
watchRequests(desktop);
const page = await desktop.newPage();

note('sign in on the local session form', "[data-testid='product-area'] visible");
await signIn(page);

// A first-run installation with nothing in it. This is captured before anything is created, so the
// empty state is a real state and not a page that was emptied by hand.
note('the first-run dashboard is genuinely empty', "[data-testid='dashboard-empty'] visible");
await page.getByTestId('dashboard-empty').waitFor({ state: 'visible' });
const projectsBefore = await page.getByTestId('dashboard-projects').count();
const milestonesBefore = await page.getByTestId('milestone').count();
if (projectsBefore !== 0 || milestonesBefore !== 0) {
  throw new Error(
    `the installation is not empty: ${projectsBefore} project(s) and ${milestonesBefore} milestone(s) were already present.`,
  );
}
facts.checks.emptyInstallationBeforeCreate = { projects: projectsBefore, milestones: milestonesBefore };
await capture(page, 'chronicleaf-dashboard-empty-desktop.png', 'empty-state dashboard', 'dashboard-empty');

// The project: created by clicking, typing and saving - the owner's own flow.
note('create a project through the project form', "[data-testid='project-name'] visible");
await page.getByTestId('dashboard-create-project').click();
await page.waitForURL(/\/projects\/new$/);
await page.getByTestId('project-name-input').fill(seed.project.name);
await page.getByTestId('project-description-input').fill(seed.project.description);
await page.getByTestId('project-save').click();
await page.getByTestId('project-detail').waitFor({ state: 'visible' });
const projectPath = new URL(page.url()).pathname;
if (!/^\/projects\/[0-9a-f-]{36}$/.test(projectPath)) {
  throw new Error(`the project was not created: the page is at ${page.url()}`);
}

// The milestone, recorded by the owner. The form has no status field: the save is the decision, so
// what the product stores here is a confirmed record.
const confirmed = seed.confirmedMilestone;
note('record a milestone through the milestone form', "[data-testid='milestone-list'] visible");
await page.getByTestId('milestone-new').click();
await page.waitForURL(/\/projects\/[0-9a-f-]{36}\/milestones\/new$/);
await page.getByTestId('milestone-title-input').fill(confirmed.title);
await page.getByTestId('milestone-type-input').selectOption(confirmed.type);
await page.getByTestId('milestone-version-input').fill(confirmed.version);
await page.getByTestId('milestone-occurred-at-input').fill(confirmed.occurredAt);
await page.getByTestId('milestone-notes-input').fill(confirmed.notes);
await page.getByTestId('milestone-limitations-input').fill(confirmed.knownLimitations.join('\n'));
await page.getByTestId('milestone-next-steps-input').fill(confirmed.nextSteps.join('\n'));
await page.getByTestId('milestone-save').click();
await page.getByTestId('milestone-list').waitFor({ state: 'visible' });
const storedConfirmed = await page.locator('[data-testid="milestone"][data-status="confirmed"]').count();
if (storedConfirmed !== 1) {
  throw new Error(`expected one confirmed milestone after the save, found ${storedConfirmed}.`);
}
facts.checks.confirmedMilestoneAfterSave = storedConfirmed;

// The candidate. No product surface collects records from a source yet, so the record is handed to
// the intake route the product exposes for exactly this, from inside the page, with this browser's
// own session cookie and a fresh antiforgery value - the call a collector in a later package will
// make. It is a real request the product answered, not a row written behind its back, and the
// product can only ever store it as a candidate.
const candidate = seed.candidateMilestone;
note('hand a record to the product intake route', "the route answers 201, and the record reads back as a candidate");
const imported = await page.evaluate(
  async ([id, record]) => {
    const metadata = await (await fetch('/api/session/antiforgery', { credentials: 'same-origin' })).json();
    const response = await fetch(`/api/projects/${id}/milestones/import`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', [metadata.headerName]: metadata.token },
      body: JSON.stringify({
        source: record.source,
        candidates: [
          {
            sourceKey: record.sourceKey,
            title: record.title,
            occurredAt: record.occurredAt,
            type: record.type,
            notes: record.notes,
          },
        ],
      }),
    });

    return { status: response.status, body: await response.json() };
  },
  [projectPath.split('/').filter((part) => part.length > 0)[1], candidate],
);
if (imported.status !== 201) {
  throw new Error(
    `the intake route refused the record: HTTP ${imported.status} ${JSON.stringify(imported.body)}`,
  );
}
if (imported.body?.items?.[0]?.created !== true) {
  throw new Error(`the intake route did not create a record: ${JSON.stringify(imported.body)}`);
}

// The import answer names the record and says it was new; the status is read back from the product's
// own milestone route, so "it was stored as a candidate" is the product's answer and not the
// capture's assumption.
const importedId = imported.body.items[0].milestoneId;
const storedCandidate = await page.evaluate(
  async (id) => {
    const response = await fetch(`/api/milestones/${id}`, { credentials: 'same-origin' });

    return { status: response.status, body: await response.json() };
  },
  importedId,
);
if (storedCandidate.status !== 200 || storedCandidate.body?.status !== 'candidate') {
  throw new Error(
    `the intake route did not store a candidate: HTTP ${storedCandidate.status} ${JSON.stringify(storedCandidate.body)}`,
  );
}
facts.checks.importedCandidate = {
  importStatus: imported.status,
  created: true,
  storedStatus: storedCandidate.body.status,
  source: candidate.source,
  sourceKey: candidate.sourceKey,
  milestoneId: importedId,
};

// Both records, read back through the product's own list.
note('read both records back through the project view', "[data-testid='milestone'] count 2");
await page.goto(`${baseURL}${projectPath}`, { waitUntil: 'load' });
await page.locator('[data-testid="milestone"]').nth(1).waitFor({ state: 'visible' });
const counts = {
  confirmed: await page.locator('[data-testid="milestone"][data-status="confirmed"]').count(),
  candidate: await page.locator('[data-testid="milestone"][data-status="candidate"]').count(),
};
if (counts.confirmed !== 1 || counts.candidate !== 1) {
  throw new Error(
    `the project should hold one confirmed and one candidate milestone, found ${counts.confirmed} and ${counts.candidate}.`,
  );
}
facts.checks.projectMilestones = counts;
await capture(
  page,
  'chronicleaf-project-milestones-desktop.png',
  'a project with one confirmed and one candidate milestone',
  'project-detail',
);

// -------------------------------------------------------------------------------------------
// Mobile: the same project and the same records, in a mobile-emulated browser.
// -------------------------------------------------------------------------------------------
const mobile = await browser.newContext({ ...MOBILE, ...DETERMINISTIC });
watchRequests(mobile);
const mobilePage = await mobile.newPage();

note('sign in again in the mobile browser', "[data-testid='product-area'] visible");
await signIn(mobilePage);
note('open the same project in the mobile browser', "[data-testid='project-detail'] visible");
await mobilePage.goto(`${baseURL}${projectPath}`, { waitUntil: 'load' });
await mobilePage.locator('[data-testid="milestone"]').nth(1).waitFor({ state: 'visible' });
const mobileCandidate = await mobilePage
  .locator('[data-testid="milestone"][data-status="candidate"]')
  .count();
if (mobileCandidate !== 1) {
  throw new Error(`the mobile browser should show the candidate milestone, found ${mobileCandidate}.`);
}
facts.checks.mobileCandidateVisible = mobileCandidate;
await capture(
  mobilePage,
  'chronicleaf-project-milestones-mobile.png',
  'the same project on a 390x844 mobile viewport',
  'project-detail',
);

await mobile.close();
await desktop.close();
await browser.close();

// -------------------------------------------------------------------------------------------
// The request log, as the browser actually issued it.
// -------------------------------------------------------------------------------------------
const unique = [...new Set(requestLog)].sort();
const hostOf = (entry) => new URL(entry.slice(entry.indexOf(' ') + 1)).hostname;
const isLoopback = (host) => host === '127.0.0.1' || host === 'localhost' || host === '::1';

facts.requests = {
  total: requestLog.length,
  unique: unique.length,
  hosts: [...new Set(unique.map(hostOf))].sort(),
  nonLoopbackIssued: unique.filter((entry) => !isLoopback(hostOf(entry))),
  nonLoopbackBlocked: blockedNonLoopback,
  log: unique,
};

writeFileSync(evidencePath, `${JSON.stringify(facts, null, 2)}\n`);
console.log('    - the browser finished and wrote its evidence');
CAPTURE_DRIVER_EOF

record "node <the temporary capture driver> (repository Playwright, origin from playwright.config.ts, $base_url)"
CAPTURE_WEB_DIR="$repo_root/$web_rel" \
  CAPTURE_STAGE_DIR="$run_dir" \
  CAPTURE_SEED_PATH="$run_dir/seed.json" \
  CAPTURE_EVIDENCE_PATH="$run_dir/browser-evidence.json" \
  CAPTURE_TOKEN_FILE="$token_file" \
  CAPTURE_BASE_URL="$base_url" \
  node "$run_dir/capture-driver.mjs"

[ -s "$run_dir/browser-evidence.json" ] ||
  fail "the browser step produced no evidence block, so the captures are not trustworthy."

# The captures are staged in /tmp and only now, with every capture and digest in hand, published
# into docs/assets/. Each published file is read back and its digest re-checked against the staged
# one, so what is recorded here is what is on disk.
for file in "${captures[@]}"; do
  staged="$run_dir/$file"
  [ -s "$staged" ] || fail "the capture $file was not produced; nothing further is published to $assets_rel."
  sha="$(sha256sum "$staged" | cut -d' ' -f1)"
  bytes="$(stat -c '%s' "$staged")"
  record "cp $run_dir/$file $assets_rel/$file"
  cp "$staged" "$repo_root/$assets_rel/$file"
  published="$(sha256sum "$repo_root/$assets_rel/$file" | cut -d' ' -f1)"
  [ "$published" = "$sha" ] ||
    fail "the published $file does not match the capture it was copied from; $published vs $sha."
  printf '%s/%s\t%s\t%s\n' "$assets_rel" "$file" "$sha" "$bytes" >>"$png_facts"
done

# The digests of what was actually served, and of the client sources that produced it.
record "sha256sum $publish_dir/wwwroot/index.html $publish_dir/wwwroot/assets/*"
artifacts_file="$run_dir/artifacts.txt"
: >"$artifacts_file"
while IFS= read -r artifact; do
  printf '%s\t%s\t%s\n' "$artifact" \
    "$(sha256sum "$repo_root/$artifact" | cut -d' ' -f1)" \
    "$(stat -c '%s' "$repo_root/$artifact")" >>"$artifacts_file"
done < <(
  find "$repo_root/$publish_dir/wwwroot" -type f \( -name '*.html' -o -name '*.js' -o -name '*.css' \) \
    | sort | sed "s|^$repo_root/||"
)

record "git hash-object $web_rel/index.html $web_rel/package.json $web_rel/package-lock.json $web_rel/playwright.config.ts $web_rel/src/**"
sources_file="$run_dir/sources.txt"
: >"$sources_file"
while IFS= read -r source; do
  printf '%s\t%s\n' "$source" "$(git hash-object "$source")" >>"$sources_file"
done < <(
  {
    printf '%s\n' \
      "$web_rel/index.html" \
      "$web_rel/package.json" \
      "$web_rel/package-lock.json" \
      "$web_rel/playwright.config.ts"
    find "$web_rel/src" -type f | sort
  } | sed "s|^$repo_root/||" | sort
)

# The evidence block: stdout only, and never written into the documentation.
cat >"$run_dir/evidence.mjs" <<'EVIDENCE_EOF'
/** Composes the machine-readable evidence block scripts/capture.sh prints. */
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const runDir = process.env.CAPTURE_RUN_DIR;
const repo = process.env.CAPTURE_REPO;

const tsv = (name) =>
  readFileSync(`${runDir}/${name}`, 'utf8')
    .split('\n')
    .filter((line) => line.length > 0)
    .map((line) => line.split('\t'));

const browser = JSON.parse(readFileSync(`${runDir}/browser-evidence.json`, 'utf8'));
const seed = JSON.parse(readFileSync(`${runDir}/seed.json`, 'utf8'));
const captureByFile = new Map(browser.captures.map((capture) => [capture.file, capture]));

const block = {
  schema: 'chronicleaf-capture-evidence/1',
  capturedAtUtc: execFileSync('date', ['-u', '+%Y-%m-%dT%H:%M:%SZ']).toString().trim(),
  git: {
    revision: execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD']).toString().trim(),
    branch: execFileSync('git', ['-C', repo, 'rev-parse', '--abbrev-ref', 'HEAD']).toString().trim(),
    workingTree: execFileSync('git', ['-C', repo, 'status', '--porcelain'])
      .toString()
      .split('\n')
      .filter((line) => line.length > 0),
  },
  baseUrl: browser.baseURL,
  commands: readFileSync(`${runDir}/commands.txt`, 'utf8').split('\n').filter((line) => line.length > 0),
  artifacts: tsv('pngs.txt').map(([path, sha256, bytes]) => {
    const capture = captureByFile.get(path.split('/').pop());
    if (!capture) {
      throw new Error(`the browser recorded no capture named ${path}; the artifact has no capture facts.`);
    }
    return {
      path,
      sha256,
      bytes: Number(bytes),
      viewport: `${capture.viewport.width}x${capture.viewport.height}`,
      fullPage: capture.fullPage,
      pngPixels: capture.pngPixels,
      waitFor: capture.waitFor,
      milestonesOnPage: capture.milestonesOnPage,
      visibleTextSha256: capture.visibleTextSha256,
      visibleTextSha256WithoutActorUuid: capture.visibleTextSha256WithoutActorUuid,
      visibleText: capture.visibleText,
    };
  }),
  builtClientArtifacts: tsv('artifacts.txt').map(([path, sha256, bytes]) => ({ path, sha256, bytes: Number(bytes) })),
  clientSourceBlobIds: tsv('sources.txt').map(([path, gitHashObject]) => ({ path, gitHashObject })),
  browser: {
    playwrightVersion: browser.playwrightVersion,
    build: browser.browser,
    configSource: browser.configSource,
    viewports: { desktop: browser.desktopViewport, mobile: browser.mobileViewport },
    deviceEmulation: browser.deviceEmulation,
    deterministicContextOptions: browser.deterministicContextOptions,
    steps: browser.steps,
  },
  seededContent: seed,
  checks: browser.checks,
  requests: browser.requests,
  secrets: {
    accessTokenGeneratedPerRun: true,
    accessTokenPrinted: false,
    accessTokenWrittenToATrackedFile: false,
    accessTokenHandling: 'a file inside the gitignored publish directory, mode 600, removed on exit',
  },
  notes: [
    'Every PNG is a screenshot of the application this run published, not a mockup or a design render.',
    'The access token is redacted in the commands list and appears nowhere in this block.',
    'Two values in the commands list are redacted placeholders: the server token hash and the loopback connection string.',
  ],
};

writeFileSync(`${runDir}/evidence.json`, `${JSON.stringify(block, null, 2)}\n`);
process.stdout.write(`${JSON.stringify(block, null, 2)}\n`);
EVIDENCE_EOF

printf '%s\n' "${commands[@]}" >"$run_dir/commands.txt"
printf '%s\n' "node <the temporary evidence composer> (prints the evidence block)" >>"$run_dir/commands.txt"

echo "-----BEGIN capture-evidence-----"
CAPTURE_RUN_DIR="$run_dir" CAPTURE_REPO="$repo_root" node "$run_dir/evidence.mjs"
echo "-----END capture-evidence-----"

echo "capture complete: ${#commands[@]} recorded commands, artifacts published under $assets_rel/"
