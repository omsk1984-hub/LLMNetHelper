# PowerShell-обёртка для запуска MonitorAgent на Windows
# Использование: .\run_MonitorAgent.ps1 [start|stop|status|logs|run-now]

param(
    [string]$Command = "start",
    [string]$ContainerName = "artas_monitor_agent"
)

$ErrorActionPreference = "Stop"

function Show-Usage {
    Write-Host "Usage: .\run_MonitorAgent.ps1 [start|stop|status|logs|run-now]"
    Write-Host ""
    Write-Host "Commands:"
    Write-Host "  start      - Запустить контейнер в фоновом режиме"
    Write-Host "  stop       - Остановить контейнер"
    Write-Host "  status     - Показать статус контейнера"
    Write-Host "  logs       - Показать логи контейнера"
    Write-Host "  run-now    - Однократный прогон"
    Write-Host "  rebuild    - Пересобрать и запустить"
}

function Start-Container {
    Write-Host "Запуск monitor-agent..."
    docker compose -f "$PSScriptRoot\docker-compose.monitor-agent.yml" up -d --build
    Write-Host "Готово. Используйте .\run_MonitorAgent.ps1 status для проверки."
}

function Stop-Container {
    Write-Host "Остановка monitor-agent..."
    docker compose -f "$PSScriptRoot\docker-compose.monitor-agent.yml" down
    Write-Host "Остановлен."
}

function Show-Status {
    Write-Host "Статус контейнера '$ContainerName':"
    docker ps --filter "name=$ContainerName" --format "table {{.ID}}\t{{.Status}}\t{{.Ports}}"
}

function Show-Logs {
    docker logs -f --tail 100 "$ContainerName"
}

function Run-Now {
    Write-Host "Выполнение однократного прогона..."
    docker exec "$ContainerName" dotnet ArtasTools.MonitorAgent.dll --run-now
}

function Rebuild {
    Write-Host "Пересборка и запуск..."
    docker compose -f "$PSScriptRoot\docker-compose.monitor-agent.yml" up -d --build --force-recreate
    Write-Host "Пересобрано и запущено."
}

switch ($Command.ToLower()) {
    "start" { Start-Container }
    "stop" { Stop-Container }
    "status" { Show-Status }
    "logs" { Show-Logs }
    "run-now" { Run-Now }
    "rebuild" { Rebuild }
    default { Show-Usage }
}
