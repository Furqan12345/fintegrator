FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY SimpleIPaaS.slnx ./
COPY src/SimpleIPaaS.Domain/*.csproj src/SimpleIPaaS.Domain/
COPY src/SimpleIPaaS.Application/*.csproj src/SimpleIPaaS.Application/
COPY src/SimpleIPaaS.Infrastructure/*.csproj src/SimpleIPaaS.Infrastructure/
COPY src/SimpleIPaaS.Shared/*.csproj src/SimpleIPaaS.Shared/
COPY src/SimpleIPaaS.Engine/*.csproj src/SimpleIPaaS.Engine/
COPY src/SimpleIPaaS.Api/*.csproj src/SimpleIPaaS.Api/
COPY src/SimpleIPaaS.Client/*.csproj src/SimpleIPaaS.Client/
RUN dotnet restore src/SimpleIPaaS.Api/SimpleIPaaS.Api.csproj && `
    dotnet restore src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj

COPY src/ src/
RUN dotnet publish src/SimpleIPaaS.Api/SimpleIPaaS.Api.csproj -c Release -o /app --no-restore && `
    dotnet publish src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1001 --create-home --shell /usr/sbin/nologin ipaas \
    && mkdir -p /data \
    && chown -R ipaas:ipaas /data /app
USER ipaas

COPY --from=build --chown=ipaas:ipaas /app ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__DefaultConnection="Data Source=/data/ipaas.db"

VOLUME ["/data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SimpleIPaaS.Api.dll"]
