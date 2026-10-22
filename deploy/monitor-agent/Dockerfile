# ArtasTools.MonitorAgent — runtime-образ (запуск в контейнере)
#
# Билд-контекст: src/LLMMetrics (см. docker-compose.monitor-agent.yml).
#   docker build -f deploy/monitor-agent/Dockerfile src/LLMMetrics
#
# ВАЖНО про NuGet: репозиторий работает офлайн (nuget.config в корне с <clear/>),
# но nuget.config ВНЕ билд-контекста и сюда не копируется. Restore в образе
# идёт из nuget.org и требует сети. Для полностью офлайн-сборки подключите
# локальный кеш как источник (см. nuget.offline.config) через --build-arg /
# mount, либо собирайте на хосте через run_LLMMetrics.ps1.
#
# ВАЖНО про Roslyn: в runtime-образе нет полного MSBuild SDK, поэтому
# Roslyn:Enabled и CodeChange:Enabled в контейнере держать выключенными.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# --- Сначала только манифесты: слой restore кэшируется отдельно от исходников ---
COPY ["LLMNetHelper.slnx", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["global.json", "./"]

COPY ["ArtasTools.MonitorAgent.Abstractions/ArtasTools.MonitorAgent.Abstractions.csproj", "ArtasTools.MonitorAgent.Abstractions/"]
COPY ["ArtasTools.MonitorAgent.Core/ArtasTools.MonitorAgent.Core.csproj", "ArtasTools.MonitorAgent.Core/"]
COPY ["ArtasTools.MonitorAgent.Context/ArtasTools.MonitorAgent.Context.csproj", "ArtasTools.MonitorAgent.Context/"]
COPY ["ArtasTools.MonitorAgent.Roslyn/ArtasTools.MonitorAgent.Roslyn.csproj", "ArtasTools.MonitorAgent.Roslyn/"]
COPY ["ArtasTools.MonitorAgent.CodeChange/ArtasTools.MonitorAgent.CodeChange.csproj", "ArtasTools.MonitorAgent.CodeChange/"]
COPY ["ArtasTools.MonitorAgent.Agent/ArtasTools.MonitorAgent.Agent.csproj", "ArtasTools.MonitorAgent.Agent/"]
COPY ["ArtasTools.MonitorAgent.Llm/ArtasTools.MonitorAgent.Llm.csproj", "ArtasTools.MonitorAgent.Llm/"]
COPY ["ArtasTools.MonitorAgent.Infrastructure/ArtasTools.MonitorAgent.Infrastructure.csproj", "ArtasTools.MonitorAgent.Infrastructure/"]
COPY ["ArtasTools.MonitorAgent/ArtasTools.MonitorAgent.csproj", "ArtasTools.MonitorAgent/"]

# Тестовые и SK-проекты в publish не участвуют — их манифесты тоже копируем,
# чтобы `dotnet restore` по решению не падал на отсутствующих проектах.
COPY ["ArtasTools.MonitorAgent.Tests/ArtasTools.MonitorAgent.Tests.csproj", "ArtasTools.MonitorAgent.Tests/"]
COPY ["ArtasTools.MonitorAgent.Orchestration.SemanticKernel/ArtasTools.MonitorAgent.Orchestration.SemanticKernel.csproj", "ArtasTools.MonitorAgent.Orchestration.SemanticKernel/"]
COPY ["ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests/ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests.csproj", "ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests/"]

RUN dotnet restore ArtasTools.MonitorAgent/ArtasTools.MonitorAgent.csproj

# --- Исходники и публикация ---
COPY . .
WORKDIR /src/ArtasTools.MonitorAgent
RUN dotnet publish -c Release -o /app /p:UseAppHost=false --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl для healthcheck, tzdata для UTC
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl tzdata && \
    ln -fs /usr/share/zoneinfo/UTC /etc/localtime && \
    echo "UTC" > /etc/timezone && \
    rm -rf /var/cache/apt/*

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LANG=C.UTF-8
ENV TZ=UTC

# Каталог отчётов (монтируется хостом) и каталог документации (тоже mount:
# docs/ живёт в корне репозитория, вне билд-контекста образа).
RUN groupadd -r appuser && useradd -r -g appuser appuser
RUN mkdir -p /reports/monitoring /docs && chown -R appuser:appuser /reports

COPY --from=build /app .
USER appuser

ENTRYPOINT ["dotnet", "ArtasTools.MonitorAgent.dll"]
