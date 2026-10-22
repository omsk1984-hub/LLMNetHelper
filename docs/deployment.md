# Развёртывание

## Хост (основной сценарий)

Конфигурация: `src/LLMNetHelper/.env` (шаблон — `src/LLMNetHelper/.env.example`).

```powershell
Copy-Item src/LLMNetHelper/.env.example src/LLMNetHelper/.env
# вписать LLM__ENDPOINT / LLM__APIKEY / модель
```

Запуск — только через явный `pwsh` (см. [runbook.md](runbook.md)).

Однократный прогон без LLM (проверка связности с Loki/Prometheus):

```powershell
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause"
```

Демон с ночным расписанием (`Agent:RunAtUtc`) и health-эндпоинтом:

```powershell
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause -Daemon"
```

Остановка демона — только `-Stop` (останавливает свой PID из файла, чужие
процессы не трогает).

Требования: .NET SDK 10.0 (`global.json`: версия `10.0.401`,
`rollForward: latestFeature`), git (для CodeChange), доступные Loki/Prometheus.

NuGet: корневой `nuget.config` работает офлайн (только глобальный кэш).
Для сборки с сетью — `nuget.online.config`, для офлайн-кэша —
`nuget.offline.config`.

## Docker

Состав: `deploy/monitor-agent/docker-compose.monitor-agent.yml`,
образ: `deploy/monitor-agent/Dockerfile`, обёртки: `run_MonitorAgent.ps1`
(Windows) и `manage_monitor_agent.sh` (WSL/Linux).

```powershell
Copy-Item deploy/monitor-agent/monitor-agent.env.example deploy/monitor-agent/monitor-agent.env
# вписать LLM__APIKEY
pwsh -File deploy/monitor-agent/run_MonitorAgent.ps1 start
```

Отличия контейнера от хостового запуска (все зафиксированы в compose-файле):

| Параметр | Хост | Контейнер |
|---|---|---|
| `Loki__BaseUrl` | `http://localhost:3100` | `http://loki:3100` |
| `Prometheus__BaseUrl` | `http://localhost:9090` | `http://prometheus:9090` |
| `Agent__ReportsDir` | `reports\monitoring` (от корня репозитория) | `/reports/monitoring` (bind-mount) |
| Документация | `docs/` (от корня репозитория) | `/docs` (read-only mount) |
| `ROSLYN__ENABLED` | возможно `true` | всегда `false` (в runtime-образе нет MSBuild SDK) |
| `CODECHANGE__ENABLED` | возможно `true` | всегда `false` |

Сеть мониторинга по умолчанию — `monitoring_artas_monitoring` (external,
создаётся составом Loki/Prometheus). Переопределяется переменной
`MONITORING_NETWORK` без правки compose-файла.

Билд-контекст образа — `src/LLMNetHelper` (корень с `LLMNetHelper.slnx`).
`docs/` и `reports/` в контекст не входят и подключаются монтированием.

NuGet в образе: корневой `nuget.config` находится ВНЕ билд-контекста и
в образ не копируется — `dotnet restore` в Docker идёт в nuget.org и требует
сети. Для полностью офлайн-сборки образа нужно передать локальный источник
пакетов отдельно; для офлайн-окружения предпочтителен запуск на хосте.

## Статус docker-контура

Docker-развёртывание после переноса проекта в отдельный репозиторий
переведено в консистентное состояние (Dockerfile, compose, шаблон env),
но **рантайм-проверка не выполнялась** — docker-демон на целевой машине
может быть недоступен. Перед боевым использованием проверить:

```powershell
docker build -f deploy/monitor-agent/Dockerfile src/LLMNetHelper
docker compose -f deploy/monitor-agent/docker-compose.monitor-agent.yml config
```
