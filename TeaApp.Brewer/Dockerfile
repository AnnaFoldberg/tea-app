# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first (restore cache)
COPY TeaApp.Contracts/TeaApp.Contracts.csproj TeaApp.Contracts/
COPY TeaApp.Brewer/TeaApp.Brewer.csproj       TeaApp.Brewer/
RUN dotnet restore TeaApp.Brewer/TeaApp.Brewer.csproj

# Copy the rest of the source
COPY TeaApp.Contracts/ TeaApp.Contracts/
COPY TeaApp.Brewer/    TeaApp.Brewer/

# Publish self-contained app files
WORKDIR /src/TeaApp.Brewer
RUN dotnet publish -c Release -o /out

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Curl for container healthchecks
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out .

# Host health endpoints on 8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TeaApp.Brewer.dll"]