# Chronicleaf runtime shell image: React build stage, .NET 10 publish stage, non-root
# runtime. Every base image version is pinned explicitly; no implicit tag is used.
# The build consumes only this repository.
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
COPY src/Chronicleaf.Api/Chronicleaf.Api.csproj src/Chronicleaf.Api/
RUN dotnet restore src/Chronicleaf.Api/Chronicleaf.Api.csproj
COPY src/Chronicleaf.Api/ src/Chronicleaf.Api/
RUN dotnet publish src/Chronicleaf.Api/Chronicleaf.Api.csproj -c Release --no-restore -o /out
COPY --from=web /web/dist/ /out/wwwroot/

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Chronicleaf.Api.dll"]
