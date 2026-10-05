# Chronicleaf Community quickstart (operator path)

Two services, one database, your machine. No Redis, no queue service, no Cloud
account: the application and PostgreSQL, with private media on a named volume and
background work (screenshots, syncs, generations, exports) on the in-process
Hangfire queue against the same database.

What you need: Docker with Compose v2, `dotnet` SDK 10 with the `ef` tool
(`dotnet tool restore`), `openssl`, `curl`, and `psql`-compatible backup tooling
(the `postgres:17.6-alpine` image itself, which is also the restore side).

## 1. Configure (four values, no working secret in the repository)

```sh
cp .env.example .env   # .env is git-ignored; fill in:
```

| `.env` name | Value |
|---|---|
| `CHRONICLEAF_DB_PASSWORD` | PostgreSQL password for the `chronicleaf` role |
| `CHRONICLEAF_ACCESS_TOKEN_HASH` | SHA-256 hex of your random setup token (`openssl rand -hex 32 \| xxd -r -p \| sha256sum`); the plaintext token itself is exchanged once for a session cookie and never stored |
| `CHRONICLEAF_PUBLIC_ORIGIN` | The exact origin the browser uses, e.g. `http://127.0.0.1:5080`; mutations from any other origin are refused with 403 |
| `CHRONICLEAF_ENCRYPTION_KEY_FILE` | Host path to a 32-byte key file (`umask 177 && openssl rand -out /run/chronicleaf-encryption.key 32`), mounted read-only; until it exists, credential routes refuse with `credential_vault_unavailable` and nothing is stored in the clear |

The application binds to loopback by default (`127.0.0.1:5080`). Serving wider is
an explicit decision: put a reverse proxy in front that terminates TLS and forwards
to `http://127.0.0.1:5080`, and set `CHRONICLEAF_PUBLIC_ORIGIN` to the public
`https://` origin the browser actually uses. The cookie is `Secure` everywhere
except a loopback origin in Development.

## 2. Start, migrate, check readiness

```sh
docker compose up -d --build
docker compose exec postgres pg_isready -U chronicleaf -d chronicleaf
ConnectionStrings__Chronicleaf="Host=127.0.0.1;Port=5432;Database=chronicleaf;Username=chronicleaf;Password=..." \
Chronicleaf__AccessTokenHash="<the same sha256 hex>" \
  dotnet ef database update --project src/Chronicleaf.Infrastructure --startup-project src/Chronicleaf.Api
curl http://127.0.0.1:5080/api/health   # 200: the process is alive
curl http://127.0.0.1:5080/api/ready    # 200: schema current, database reachable
```

Migrations run by this explicit operator command, never on HTTP startup, and the
application never overwrites existing data on restart. Then provision the singleton
installation row (operator state, never self-claimed by a visitor):

```sh
docker compose exec postgres psql -U chronicleaf -d chronicleaf -c \
  "INSERT INTO installation (id, schema_version, created_at) VALUES (gen_random_uuid(), 1, now()) ON CONFLICT (id) DO NOTHING;"
```

`/api/health` stays 200 while `/api/ready` reports `database_unavailable` until the database answers.

## 3. The first complete workflow

Sign in with your setup token, then: connect a repository to a project → create a
Milestone (manual records are confirmed on save; imported candidates need your
explicit confirm) → record evidence → request a screenshot → generate a grounded
Markdown draft with your own provider key (BYOK) → edit, approve and export the
Markdown → read the project's Timeline. Approval binds the content revision, the
body hash, the evidence head, the voice snapshot and the media selection; editing
the body or appending evidence afterwards ends the approval until you approve
again, and export without a live approval is refused.

Third-party transfer notice: generating a draft sends the evidence you selected
to the provider you chose, under the consent your preview authorized (15-minute
TTL, SHA-256 bound). Nothing is sent before you authorize that preview, the
stored credential never leaves the vault except as a leased bearer header on
that one call, and provider error bodies are never logged. GitHub collection
likewise reads only the public repository (or the PAT-scoped one) you connected.

## 4. Back up before every upgrade, restore into fresh volumes to prove it

```sh
docker compose exec -T postgres pg_dump -U chronicleaf -d chronicleaf -F c -f /tmp/chronicleaf-backup.dump
docker compose cp postgres:/tmp/chronicleaf-backup.dump ./chronicleaf-backup.dump
cp -r "$(docker volume inspect chronicleaf_media --format '{{.Mountpoint}}')" ./media-backup  # or: docker run --rm -v chronicleaf_media:/media -v "$PWD":/out alpine tar czf /out/media-backup.tgz -C /media .
```

Restore (fresh volumes, same encryption key — without it, credentials stay sealed
and generation-dependent steps refuse rather than guess):

```sh
docker compose down
docker volume rm chronicleaf_database chronicleaf_media   # or fresh names on another host
docker compose up -d postgres
cat ./chronicleaf-backup.dump | docker compose exec -T postgres pg_restore -U chronicleaf -d chronicleaf
# restore media files back into the media volume, then:
docker compose up -d app
curl http://127.0.0.1:5080/api/ready   # 200 before any workflow read
```

`scripts/verify-community.sh` performs exactly this cycle against disposable
volumes and re-reads project ids, media bytes and the approved export
byte-for-byte afterwards.

## 5. Capture sandbox (Linux-only hardened capture)

Screenshots run a real Chromium confined to an egress proxy: container
initialization (`scripts/chronicleaf-entrypoint.sh`) creates the API and capture
UIDs, the media/browser-job directories and the capture-UID egress firewall, then
starts the non-root API with the in-process supervisor. That firewall step needs
`CAP_NET_ADMIN` (declared in `compose.yaml`) and `nft`/`iptables`; without them
the server still starts and serves everything else, but every capture job answers
`capture_sandbox_unavailable` before a browser starts — a visible refusal, never
a capture with weaker isolation. Native builds and hosts without that sandbox can
test the whole domain/API surface (including the request-time refusal of a
private destination, which needs no browser) without pretending the sandbox
exists. The Chromium sandbox is never disabled and webpage processing never runs
as root.

## 6. What this release does not do

Repository publication stays yours: an undecided GitHub visibility means no push
is performed by any script here, and no script contains a publish command — the
release test asserts that. Managed AI, multi-tenancy, teams and hosted archives
are separate Cloud packages the Community install does not depend on.
