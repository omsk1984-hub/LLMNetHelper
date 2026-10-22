# LLMNetHelper — runtime-образ (запуск в контейнере)
#
# Билд-контекст: src/LLMNetHelper (см. docker-compose.monitor-agent.yml).
#   docker build -f deploy/monitor-agent/Dockerfile src/LLMNetHelper
#
# ВАЖНО про NuGet: репозиторий работает офлайн (nuget.config в корне с <clear/>),
# но nuget.config ВНЕ билд-контекста и сюда не копируется. Restore в образе
# идёт из nuget.org и требует сети. Для полностью офлайн-сборки подключите
# локальный кеш как источник (см. nuget.offline.config) через --build-arg /
# mount, либо собирайте на хосте через run_LLMNetHelper.ps1.
#
# ВАЖНО про Roslyn: в runtime-образе нет полного MSBuild SDK, поэтому
# Roslyn:Enabled и CodeChange:Enabled в контейнере держать выключенными.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# --- Сначала только манифесты: слой restore кэшируется отдельно от исходников ---
COPY ["LLMNetHelper.slnx", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["global.json", "./"]

COPY ["LLMNetHelper.Abstractions/LLMNetHelper.Abstractions.csproj", "LLMNetHelper.Abstractions/"]
COPY ["LLMNetHelper.Core/LLMNetHelper.Core.csproj", "LLMNetHelper.Core/"]
COPY ["LLMNetHelper.Context/LLMNetHelper.Context.csproj", "LLMNetHelper.Context/"]
COPY ["LLMNetHelper.Roslyn/LLMNetHelper.Roslyn.csproj", "LLMNetHelper.Roslyn/"]
COPY ["LLMNetHelper.CodeChange/LLMNetHelper.CodeChange.csproj", "LLMNetHelper.CodeChange/"]
COPY ["LLMNetHelper.Agent/LLMNetHelper.Agent.csproj", "LLMNetHelper.Agent/"]
COPY ["LLMNetHelper.Llm/LLMNetHelper.Llm.csproj", "LLMNetHelper.Llm/"]
COPY ["LLMNetHelper.Infrastructure/LLMNetHelper.Infrastructure.csproj", "LLMNetHelper.Infrastructure/"]
COPY ["LLMNetHelper/LLMNetHelper.csproj", "LLMNetHelper/"]

# Тестовые и SK-проекты в publish не участвуют — их манифесты тоже копируем,
# чтобы `dotnet restore` по решению не падал на отсутствующих проектах.
COPY ["LLMNetHelper.Tests/LLMNetHelper.Tests.csproj", "LLMNetHelper.Tests/"]
COPY ["LLMNetHelper.Orchestration.SemanticKernel/LLMNetHelper.Orchestration.SemanticKernel.csproj", "LLMNetHelper.Orchestration.SemanticKernel/"]
COPY ["LLMNetHelper.Orchestration.SemanticKernel.Tests/LLMNetHelper.Orchestration.SemanticKernel.Tests.csproj", "LLMNetHelper.Orchestration.SemanticKernel.Tests/"]

RUN dotnet restore LLMNetHelper/LLMNetHelper.csproj

# --- Исходники и публикация ---
COPY . .
WORKDIR /src/LLMNetHelper
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

ENTRYPOINT ["dotnet", "LLMNetHelper.dll"]
