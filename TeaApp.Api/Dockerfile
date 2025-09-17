# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first (restore cache)
COPY TeaApp.Contracts/TeaApp.Contracts.csproj TeaApp.Contracts/
COPY TeaApp.Api/TeaApp.Api.csproj             TeaApp.Api/
RUN dotnet restore TeaApp.Api/TeaApp.Api.csproj

# Copy the rest of the source
COPY TeaApp.Contracts/ TeaApp.Contracts/
COPY TeaApp.Api/       TeaApp.Api/

# Publish
WORKDIR /src/TeaApp.Api
RUN dotnet publish -c Release -o /out

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install curl for container healthchecks (used by compose healthcheck)
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TeaApp.Api.dll"]