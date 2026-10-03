# Chronicleaf runtime image: React build stage, .NET 10 publish stage, non-root
# runtime. Every base image version is pinned explicitly; no implicit tag is used.
# The build consumes only this repository.
#
# The schema is never created or migrated here: the operator runs
# `dotnet ef database update --project src/Chronicleaf.Infrastructure
#  --startup-project src/Chronicleaf.Api` explicitly, and this image deliberately
# starts without a database so that liveness stays honest and readiness reports it.
#
# Configuration a deployment must supply at run time. The image carries no credential and no
# default for any of them; a container started without the two required values does not serve the
# access boundary at all.
#
#   ConnectionStrings__Chronicleaf   REQUIRED for readiness to be anything but
#                                    database_unavailable, e.g.
#                                    "Host=postgres;Database=chronicleaf;Username=..."
#   Chronicleaf__AccessTokenHash     REQUIRED outside Development. The SHA-256 hex, 64 lower-case
#                                    hex characters, of the operator's random setup token of at
#                                    least 32 random bytes. Generate one with:
#                                      openssl rand -hex 32 | xxd -r -p | sha256sum
#                                    The plaintext token is never configured, stored or logged; it
#                                    is exchanged once for a session cookie. Changing this value
#                                    revokes every existing session on the next start.
#   Chronicleaf__PublicOrigin        REQUIRED for mutations, e.g. "https://chronicleaf.example".
#                                    A mutation whose Origin is anything else is refused with 403,
#                                    so an unset value fails closed rather than open.
#   Chronicleaf__MediaRoot           Optional. The single directory stored images live under,
#                                    defaulting to runtime/media relative to the working
#                                    directory. It must NOT be the published wwwroot or
#                                    anywhere inside it: stored images are private, the static
#                                    file middleware serves that directory without an owner
#                                    session, and a start-up is refused outright rather than
#                                    quietly publishing every image. Mount a volume here to
#                                    keep images across container replacements, e.g.
#                                      -v chronicleaf-media:/app/runtime/media
#                                    The volume must be writable by the runtime user; a
#                                    named volume is, a host bind mount may not be.
#   ASPNETCORE_ENVIRONMENT           Optional. Only "Development" relaxes one rule, and only on a
#                                    loopback public origin: the session cookie may then omit
#                                    Secure. Every other value keeps Secure on and still requires
#                                    the access token hash.
#
# A minimal run looks like:
#   docker run --rm -p 8080:8080 \
#     -e ConnectionStrings__Chronicleaf="$CHRONICLEAF_CONNECTION" \
#     -e Chronicleaf__AccessTokenHash="$CHRONICLEAF_ACCESS_TOKEN_HASH" \
#     -e Chronicleaf__PublicOrigin="https://chronicleaf.example" \
#     -v chronicleaf-media:/app/runtime/media \
#     chronicleaf:latest
ARG NODE_IMAGE=node:24.18.0-bookworm-slim
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.4

FROM ${NODE_IMAGE} AS web
WORKDIR /web
COPY src/Chronicleaf.Web/package.json src/Chronicleaf.Web/package-lock.json ./
RUN npm ci
COPY src/Chronicleaf.Web/ ./
RUN npm run build

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
# The adopted shared storage is restored from .nupkg files committed in this repository, reached
# through the repository-relative `chronicleaf-platform-feed` source in nuget.config. Nothing here
# names a sibling checkout, an absolute path or an environment variable, which is Ruling 12 of the
# workspace ARCHITECTURE.md and is what lets this image build on a machine that has never seen
# dotnet-platform-libs. scripts/pack-platform.sh re-packs the feed and is never part of a build.
COPY nuget.config ./
COPY packages/platform-feed/ ./packages/platform-feed/
# Core -> Application -> Infrastructure -> Api, the layering the composition root depends on.
COPY src/Chronicleaf.Core/Chronicleaf.Core.csproj src/Chronicleaf.Core/
COPY src/Chronicleaf.Application/Chronicleaf.Application.csproj src/Chronicleaf.Application/
COPY src/Chronicleaf.Infrastructure/Chronicleaf.Infrastructure.csproj src/Chronicleaf.Infrastructure/
COPY src/Chronicleaf.Api/Chronicleaf.Api.csproj src/Chronicleaf.Api/
RUN dotnet restore src/Chronicleaf.Api/Chronicleaf.Api.csproj
COPY src/Chronicleaf.Core/ src/Chronicleaf.Core/
COPY src/Chronicleaf.Application/ src/Chronicleaf.Application/
COPY src/Chronicleaf.Infrastructure/ src/Chronicleaf.Infrastructure/
COPY src/Chronicleaf.Api/ src/Chronicleaf.Api/
RUN dotnet publish src/Chronicleaf.Api/Chronicleaf.Api.csproj -c Release --no-restore -o /out
COPY --from=web /web/dist/ /out/wwwroot/

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out ./
# The default MediaRoot. Created here and handed to the runtime user, because the image runs
# non-root and would otherwise fail its first start trying to create it under a root-owned /app.
# A deployment that mounts its own volume at this path overrides it and the operator keeps the
# images across container replacements.
RUN mkdir -p /app/runtime/media && chown -R $APP_UID /app/runtime
# Required configuration. Supply it at run time; the image carries no credential.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Chronicleaf.Api.dll"]
