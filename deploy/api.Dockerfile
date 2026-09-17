# The orchestrator API host (also runs the Hangfire server in-process - see
# Program.cs's AddHangfireServer call). Built from the repository root, not from
# this deploy/ directory, so it can see src/ and the solution file:
#   docker compose -f deploy/docker-compose.yml build api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Orchestrator.sln .
COPY src/ src/

RUN dotnet publish src/Orchestrator.Api/Orchestrator.Api.csproj \
    --configuration Release \
    --output /app \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Matches worker.Dockerfile's sandbox user id, though this container never runs
# an agent - kept consistent so a bind-mounted git root's file ownership is the
# same regardless of which container touched it last.
RUN groupadd --gid 10001 orchestrator \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin orchestrator

COPY --from=build /app .

# The docker.sock mount (see docker-compose.yml) is owned by root:docker on the
# host in the common case; SandboxedProcessRunner needs to be able to run `docker
# run` for the coding-agent sandbox, which is why this image does NOT drop to the
# unprivileged user the way worker.Dockerfile's sandbox does.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Orchestrator.Api.dll"]
