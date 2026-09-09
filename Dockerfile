# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/StreetHighlighter.csproj", "src/"]
RUN dotnet restore "src/StreetHighlighter.csproj"

COPY . .
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
        arm64) DOTNET_RID="linux-arm64" ;; \
        *) DOTNET_RID="linux-x64" ;; \
    esac \
    && dotnet publish "src/StreetHighlighter.csproj" \
        -c Release \
        -o /app/publish \
        -r "${DOTNET_RID}" \
        --self-contained false \
        /p:UseAppHost=false \
    && mkdir -p /app/publish/Cache

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final

LABEL org.opencontainers.image.source="https://github.com/serega404/StreetHighlighter"

WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "StreetHighlighter.dll"]
