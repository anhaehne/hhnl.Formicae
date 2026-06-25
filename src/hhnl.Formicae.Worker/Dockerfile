# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY hhnl.Formicae.slnx ./
COPY src/hhnl.Formicae.Application/hhnl.Formicae.Application.csproj src/hhnl.Formicae.Application/
COPY src/hhnl.Formicae.Infrastructure/hhnl.Formicae.Infrastructure.csproj src/hhnl.Formicae.Infrastructure/
COPY src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj src/hhnl.Formicae.Worker/
RUN dotnet restore src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj

COPY src/ src/
RUN dotnet publish src/hhnl.Formicae.Worker/hhnl.Formicae.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "hhnl.Formicae.Worker.dll"]
