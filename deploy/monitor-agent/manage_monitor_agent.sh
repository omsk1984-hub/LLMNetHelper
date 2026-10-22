#!/bin/bash
# Обёртка для запуска MonitorAgent в WSL
# Использование: ./manage_monitor_agent.sh [start|stop|status|logs|run-now]

COMPOSE_FILE="$(dirname "$0")/docker-compose.monitor-agent.yml"
CONTAINER_NAME="artas_monitor_agent"

case "$1" in
    start)
        echo "Запуск monitor-agent..."
        docker compose -f "$COMPOSE_FILE" up -d --build
        echo "Готово. Используйте ./manage_monitor_agent.sh status для проверки."
        ;;
    stop)
        echo "Остановка monitor-agent..."
        docker compose -f "$COMPOSE_FILE" down
        echo "Остановлен."
        ;;
    status)
        echo "Статус контейнера '$CONTAINER_NAME':"
        docker ps --filter "name=$CONTAINER_NAME" --format "table {{.ID}}\t{{.Status}}\t{{.Ports}}"
        ;;
    logs)
        docker logs -f --tail 100 "$CONTAINER_NAME"
        ;;
    run-now)
        echo "Выполнение однократного прогона..."
        docker exec "$CONTAINER_NAME" dotnet ArtasTools.MonitorAgent.dll --run-now
        ;;
    rebuild)
        echo "Пересборка и запуск..."
        docker compose -f "$COMPOSE_FILE" up -d --build --force-recreate
        echo "Пересобрано и запущено."
        ;;
    *)
        echo "Использование: $0 [start|stop|status|logs|run-now|rebuild]"
        echo ""
        echo "Команды:"
        echo "  start      - Запустить контейнер в фоновом режиме"
        echo "  stop       - Остановить контейнер"
        echo "  status     - Показать статус контейнера"
        echo "  logs       - Показать логи контейнера"
        echo "  run-now    - Однократный прогон"
        echo "  rebuild    - Пересобрать и запустить"
        ;;
esac
