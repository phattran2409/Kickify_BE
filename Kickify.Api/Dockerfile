# Base Stage - Minimal Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base

# Install essential dependencies + curl for healthcheck + AWS RDS Root CA
RUN apk add --no-cache icu-libs tzdata curl ca-certificates \
    && curl -sS https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /usr/local/share/ca-certificates/aws-rds.crt \
    && update-ca-certificates \
    && rm -rf /var/cache/apk/* /tmp/* /var/tmp/*

# Create non-root user
RUN addgroup -g 1000 appuser && adduser -u 1000 -G appuser -s /bin/sh -D appuser

WORKDIR /app
EXPOSE 8080

# Restore Stage - Dependencies Only
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS restore

WORKDIR /src

# Copy only project files for layer caching
COPY *.sln ./
COPY */*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

# Restore with correct RID
RUN dotnet restore "Kickify.Api/Kickify.Api.csproj" \
    --runtime linux-musl-x64 \
    --verbosity minimal

# Build Stage
FROM restore AS build

ARG BUILD_CONFIGURATION=Release

# Copy source code
COPY . .
WORKDIR "/src/Kickify.Api"

# Build
RUN dotnet build "Kickify.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    --no-restore \
    /p:DebugType=None \
    /p:DebugSymbols=false

# Publish Stage
FROM build AS publish

ARG BUILD_CONFIGURATION=Release

# Publish
RUN dotnet publish "Kickify.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    --runtime linux-musl-x64 \
    --self-contained false \
    /p:UseAppHost=false \
    /p:DebugType=None \
    /p:DebugSymbols=false

# Clean up unnecessary files
RUN find /app/publish -type f \( -name "*.pdb" -o -name "*.xml" \) -delete && \
    find /app/publish -type f -name "*Development.json" -delete

# Final Stage - Ultra Lightweight
FROM base AS final

USER appuser
WORKDIR /app

# Copy only published files
COPY --from=publish --chown=appuser:appuser /app/publish .

# Minimal environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080

# Health check using curl
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Kickify.Api.dll"]