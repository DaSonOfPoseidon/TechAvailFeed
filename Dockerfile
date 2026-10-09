# The .NET services. One image per project: docker build --build-arg PROJECT=TechAvail.Api
ARG PROJECT=TechAvail.Api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props ./
COPY src ./src
RUN dotnet publish src/$PROJECT -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
WORKDIR /app
COPY --from=build /app ./
# The apps bind HTTP_PORT themselves; the image's 8080 default only causes an override warning.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 ASSEMBLY=$PROJECT.dll ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["sh", "-c", "exec dotnet $ASSEMBLY \"$@\"", "--"]
