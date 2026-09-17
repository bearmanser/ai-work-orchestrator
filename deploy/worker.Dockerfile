# Sandbox image for coding-agent CLIs (and, in later tasks, build/test commands). Kept minimal
# here: base image, non-root user, and the tools the runner itself requires. Later tasks build
# language-specific providers on top of this image.
FROM node:22-bookworm-slim

ARG CLAUDE_CLI_VERSION=2.0.0
ARG CODEX_CLI_VERSION=0.40.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        curl \
        ca-certificates \
        libicu72 \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK, needed by later tasks' build/test commands running inside this same sandbox.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh
ENV DOTNET_ROOT=/usr/share/dotnet
ENV DOTNET_NOLOGO=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Pin exact CLI versions so an upstream release cannot silently change agent behaviour.
RUN npm install -g \
        @anthropic-ai/claude-code@${CLAUDE_CLI_VERSION} \
        @openai/codex@${CODEX_CLI_VERSION}

# Non-root user the sandbox runner always passes via `--user 10001:10001`.
RUN groupadd --gid 10001 orchestrator-agent \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin orchestrator-agent

USER 10001:10001
WORKDIR /work
