# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatDock.Core/MatDock.Core.csproj", "src/MatDock.Core/"]
COPY ["src/MatDock.Web/MatDock.Web.csproj", "src/MatDock.Web/"]
RUN dotnet restore "src/MatDock.Web/MatDock.Web.csproj"

COPY . .
# Fall back to today's date when BuildDate is not supplied, so the version never becomes "…-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatDock.Web/MatDock.Web.csproj" \
    -c Release -o /app/publish \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:4455 \
    ASPNETCORE_ENVIRONMENT=Production \
    MatDock__DataPath=/data

RUN mkdir -p /data
VOLUME ["/data"]
EXPOSE 4455

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MatDock.Web.dll"]
