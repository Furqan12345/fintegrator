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
RUN dotnet restore src/SimpleIPaaS.Api/SimpleIPaaS.Api.csproj
RUN dotnet restore src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj
RUN dotnet restore src/SimpleIPaaS.Client/SimpleIPaaS.Client.csproj
COPY src/ src/
RUN dotnet publish src/SimpleIPaaS.Api/SimpleIPaaS.Api.csproj -c Release -o /out/api --no-restore
RUN dotnet publish src/SimpleIPaaS.Engine/SimpleIPaaS.Engine.csproj -c Release -o /out/engine --no-restore
RUN dotnet publish src/SimpleIPaaS.Client/SimpleIPaaS.Client.csproj -c Release -o /out/client --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
WORKDIR /app
RUN useradd --uid 1001 --create-home --shell /usr/sbin/nologin ipaas \
    && mkdir -p /data \
    && chown ipaas:ipaas /data
COPY --from=build --chown=ipaas:ipaas /out/api /app/api
COPY --from=build --chown=ipaas:ipaas /out/engine /app/engine
USER ipaas
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__DefaultConnection="Data Source=/data/ipaas.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/api/SimpleIPaaS.Api.dll"]

FROM caddy:2 AS web
COPY --from=build /out/client/wwwroot /srv
COPY ops/azure/Caddyfile /etc/caddy/Caddyfile
