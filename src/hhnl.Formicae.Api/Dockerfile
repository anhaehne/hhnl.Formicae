# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY hhnl.Formicae.slnx ./
COPY src/hhnl.Formicae.Application/hhnl.Formicae.Application.csproj src/hhnl.Formicae.Application/
COPY src/hhnl.Formicae.Infrastructure/hhnl.Formicae.Infrastructure.csproj src/hhnl.Formicae.Infrastructure/
COPY src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj src/hhnl.Formicae.Api/
RUN dotnet restore src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj

COPY src/ src/
RUN dotnet publish src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "hhnl.Formicae.Api.dll"]
