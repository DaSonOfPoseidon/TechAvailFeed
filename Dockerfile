# The .NET services. One image per project: docker build --build-arg PROJECT=TechAvail.Api --target api
# The api target also serves the dashboard (web/); the ingest uses the plain runtime target.
ARG PROJECT=TechAvail.Api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props ./
COPY src ./src
RUN dotnet publish src/$PROJECT -c Release -o /app

FROM node:24-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
WORKDIR /app
COPY --from=build /app ./
# The apps bind HTTP_PORT themselves; the image's 8080 default only causes an override warning.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 ASSEMBLY=$PROJECT.dll ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["sh", "-c", "exec dotnet $ASSEMBLY \"$@\"", "--"]

FROM runtime AS api
COPY --from=web /web/dist/web/browser ./wwwroot
