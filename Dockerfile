# Chronicleaf runtime image: React build stage, .NET 10 publish stage, non-root
# runtime. Every base image version is pinned explicitly; no implicit tag is used.
# The build consumes only this repository.
#
# The schema is never created or migrated here: the operator runs
# `dotnet ef database update --project src/Chronicleaf.Infrastructure
#  --startup-project src/Chronicleaf.Api` explicitly, and this image deliberately
# starts without a database so that liveness stays honest and readiness reports it.
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
# Required configuration. Supply it at run time; the image carries no credential.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Chronicleaf.Api.dll"]
