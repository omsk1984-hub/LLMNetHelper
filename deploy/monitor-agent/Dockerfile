FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем solution и project files
COPY ["ArtasTools.slnx", "."]
COPY ["../LLMMetrics/", "LLMMetrics/"]
COPY ["Tools/", "Tools/"]

# Восстанавливаем пакеты
RUN dotnet restore LLMMetrics/ArtasTools.MonitorAgent/ArtasTools.MonitorAgent.csproj

# Копируем весь код и собираем
COPY . .
WORKDIR /src/LLMMetrics/ArtasTools.MonitorAgent
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

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

# Создаём пользователя
RUN groupadd -r appuser && useradd -r -g appuser appuser
RUN mkdir -p /reports/monitoring && chown -R appuser:appuser /reports

COPY --from=build /app .
USER appuser

ENTRYPOINT ["dotnet", "ArtasTools.MonitorAgent.dll"]
