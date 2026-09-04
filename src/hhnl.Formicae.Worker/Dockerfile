# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj src/hhnl.Formicae.Worker/
RUN dotnet restore src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj

COPY src/hhnl.Formicae.Worker/ src/hhnl.Formicae.Worker/
RUN dotnet publish src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app
ARG PLAYWRIGHT_VERSION=1.61.0
ARG PLAYWRIGHT_MCP_VERSION=0.0.80
ARG DOCKER_VERSION=29.7.2
ARG KUBECTL_VERSION=v1.37.0
ARG KIND_VERSION=v0.33.0
ENV CODEX_HOME=/tmp/codex-home \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PATH="/root/.local/bin:/root/.cargo/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates curl gnupg python3 python3-pip python3-venv \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install --global "playwright@${PLAYWRIGHT_VERSION}" "@playwright/mcp@${PLAYWRIGHT_MCP_VERSION}" \
    && playwright install --with-deps chromium \
    && playwright-mcp install-browser chromium \
    && curl -fsSLo /tmp/docker.tgz "https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_VERSION}.tgz" \
    && tar -xzf /tmp/docker.tgz --strip-components=1 -C /usr/local/bin docker/docker \
    && curl -fsSLo /usr/local/bin/kubectl "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl" \
    && curl -fsSLo /usr/local/bin/kind "https://kind.sigs.k8s.io/dl/${KIND_VERSION}/kind-linux-amd64" \
    && chmod +x /usr/local/bin/kubectl /usr/local/bin/kind \
    && curl -LsSf https://astral.sh/uv/install.sh | sh \
    && /root/.local/bin/uv python install 3.12 \
    && /root/.local/bin/uv tool install openhands --python 3.12 \
    && rm -rf /var/lib/apt/lists/* /tmp/docker.tgz /root/.npm

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "hhnl.Formicae.Worker.dll"]
