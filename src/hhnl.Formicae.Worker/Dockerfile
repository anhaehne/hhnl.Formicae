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
ENV CODEX_HOME=/tmp/codex-home \
    PATH="/root/.local/bin:/root/.cargo/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates curl gnupg python3 python3-pip python3-venv \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && curl -LsSf https://astral.sh/uv/install.sh | sh \
    && /root/.local/bin/uv python install 3.12 \
    && /root/.local/bin/uv tool install openhands --python 3.12 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "hhnl.Formicae.Worker.dll"]