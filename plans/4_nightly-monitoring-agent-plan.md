# План: Ночной агент проверки Writer + PostgreSQL

## Контекст

Уже есть:
- **Loki + Promtail** — логи Writer (`writer.stdout.log`, `writer.stderr.log`)
- **Prometheus** — метрики через OTel Collector
- **PostgreSQL** в Docker (`db/docker-compose.yml`)
- **MemReport + ReportAnalyzer** — инструменты для анализа памяти из Prometheus
- **scripts/manage_monitoring.sh** — управление мониторингом в WSL

**Решение:** Не консольная утилита (C#), а **bash-скрипт**, который вызывает существующие инструменты и собирает единый Markdown-отчет. Это проще, не требует компиляции, легко тестируется в WSL и вписывается в текущую архитектуру.

---

## Архитектура

```
┌─────────────────────────────────────────────────────────┐
│               WSL2 (ночной cron / systemd timer)         │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │  nightly_check.sh (главный скрипт)               │   │
│  │                                                   │   │
│  │  ┌────────────────┐  ┌────────────────────┐     │   │
│  │  │ Стадия 1: Loki  │  │ Стадия 2: PG       │     │   │
│  │  │ анализ логов    │  │ диагностика        │     │   │
│  │  └────────────────┘  └────────────────────┘     │   │
│  │                                                   │   │
│  │  ┌────────────────┐  ┌────────────────────┐     │   │
│  │  │ Стадия 3: OTel │  │ Стадия 4: MemReport │     │   │
│  │  │ / Prometheus    │  │ (из существующего)  │     │   │
│  │  │ метрики         │  │                    │     │   │
│  │  └────────────────┘  └────────────────────┘     │   │
│  └──────────────────────────────────────────────────┘   │
│                          ↓                                │
│              reports/nightly_YYYY-MM-DD.md                │
└─────────────────────────────────────────────────────────┘
```

---

## Структура файлов

```
scripts/nightly_check/
├── nightly_check.sh              # Главный: запускает все стадии, обрабатывает ошибки
├── check_loki.sh                 # Стадия 1: Loki API queries
├── check_postgres.sh             # Стадия 2: SQL-запросы через psql
├── check_metrics.sh              # Стадия 3: Prometheus queries
├── run_memreport.sh              # Обертка для запуска MemReport
├── generate_report.sh            # Стадия 4: Сборка Markdown из JSON + шаблон
├── config.env                    # Переменные окружения
├── templates/
│   └── report_template.md        # Шаблон Markdown-отчета
└── README.md                     # Документация

scripts/
└── run_nightly_check.sh          # Обертка для запуска из корня проекта
```

---

## Фаза 1: Стадии проверки

### Стадия 1: Анализ логов через Loki API (`check_loki.sh`)

**Цель:** Найти все ошибки, предупреждения и аномалии в логах Writer за последние 24 часа.

**Запросы к Loki API** (`http://localhost:3100/loki/api/v1/query_range`):

```bash
# 1. Все ошибки и критические логи за последние 24ч
curl -s "http://localhost:3100/loki/api/v1/query_range?query={job=\"artas_writer\",lvl=~\"fail|crit\"}&start=$(date -d '24 hours ago' +%s)000000000&end=$(date +%s)000000000&limit=1000"

# 2. Предупреждения
curl -s "http://localhost:3100/loki/api/v1/query_range?query={job=\"artas_writer\",lvl=\"warn\"}&start=...&end=...&limit=1000"

# 3. Проверка на отсутствие логов (Writer мог не писать)
curl -s "http://localhost:3100/loki/api/v1/query_range?query={job=\"artas_writer\"}&start=...&end=...&limit=1"
```

**Что проверять:**
- Количество `fail`/`crit` за 24ч
- Топ-5 повторяющихся ошибок (дедупликация)
- Ошибки подключения к Finam gRPC
- Ошибки записи в PostgreSQL
- Ошибки детектирования фракталов
- Пустые лога (если Writer не запускался)
- Аномальный всплеск ошибок за определённый период

**Результат:** JSON-файл `reports/nightly_loki_<date>.json`

---

### Стадия 2: Диагностика PostgreSQL (`check_postgres.sh`)

**Цель:** Проверить здоровье, производительность и размер БД.

**Запросы к PostgreSQL** (через `docker exec -i postgres_artas psql -U artas -d artas`):

```sql
-- 1. Размер базы данных
SELECT pg_size_pretty(pg_database_size('artas'));

-- 2. Размер таблиц по символам
SELECT schemaname, relname, pg_size_pretty(pg_total_relation_size(schemaname||'.'||relname))
FROM pg_stat_user_tables ORDER BY pg_total_relation_size(schemaname||'.'||relname) DESC;

-- 3. Активные подключения
SELECT count(*) FROM pg_stat_activity WHERE datname = 'artas';

-- 4. Медленные запросы (если pg_stat_statements включен)
SELECT query, calls, mean_exec_time, total_exec_time
FROM pg_stat_statements
ORDER BY mean_exec_time DESC LIMIT 20;

-- 5. Заблокированные запросы
SELECT blocked_queries.pid AS blocked_pid,
       blocked_queries.query AS blocked_query,
       blocking_queries.pid AS blocking_pid,
       blocking_queries.query AS blocking_query
FROM pg_stat_activity blocked_queries
LEFT JOIN pg_stat_activity blocking_queries
  ON blocked_queries.waiting = true
  AND blocked_queries.waiting_pid = blocking_queries.pid
WHERE blocked_queries.datname = 'artas' AND blocked_queries.waiting = true;

-- 6. Проверка advisory lock Writer
SELECT * FROM pg_locks WHERE locktype = 'advisory';

-- 7. Количество записей в fractals за последние сутки
SELECT symbol, count(*) FROM fractals
WHERE created_at > NOW() - INTERVAL '24 hours'
GROUP BY symbol ORDER BY count(*) DESC;

-- 8. Проверка на пустые интервалы (Writer мог пропустить данные)
SELECT symbol, MAX(created_at) as last_fractal,
       NOW() - MAX(created_at) as gap
FROM fractals
GROUP BY symbol
HAVING NOW() - MAX(created_at) > INTERVAL '2 hours'
ORDER BY last_fractal;

-- 9. Статус autovacuum
SELECT relname, last_vacuum, last_autovacuum, vacuum_count, autovacuum_count
FROM pg_stat_user_tables
ORDER BY last_autovacuum IS NULL, last_autovacuum;

-- 10. Пул подключений
SELECT count(*) as active_connections,
       max_connections,
       count(*) * 100.0 / max_connections as utilization_pct
FROM pg_stat_activity;
```

**Что проверять:**
- Размер БД и тренд роста
- Размер каждой таблицы по символам
- Активные/заблокированные подключения
- Заблокированные запросы
- Пустые интервалы данных (Writer молчал >2ч)
- Последний записанный фрактал по каждому символу
- Статус autovacuum (не отвалился ли)
- Утилизация пула подключений

**Результат:** JSON-файл `reports/nightly_pg_<date>.json`

---

### Стадия 3: Анализ метрик из Prometheus (`check_metrics.sh`)

**Цель:** Проверить метрики Writer через Prometheus (те же, что использует MemReport).

**Запросы к Prometheus API** (`http://localhost:9090/api/v1/`):

```bash
# 1. Количество обработанных фракталов за 24ч
sum(rate(fractals_detected_total[24h])) by (symbol)

# 2. Ошибки подключения (если есть метрика)
sum(rate(grpc_exceptions_total{job="artas-writer"}[24h]))

# 3. Uptime Writer
up{job="artas-writer"}

# 4. Память (RSS)
process_resident_memory_bytes{job="artas-writer"}
```

**Что проверять:**
- Uptime Writer (были ли рестарты)
- Количество обнаруженных фракталов (аномальный спад?)
- Ошибки gRPC подключения к Finam
- Использование памяти (базовая проверка, детальный анализ — MemReport)

**Результат:** JSON-файл `reports/nightly_metrics_<date>.json`

---

### Стадия 4: Запуск существующего MemReport

**Цель:** Использовать уже существующий инструмент для анализа утечек памяти.

```bash
cd src/Tools/MemReport
dotnet run -- --hours 24 --job artas-writer --prometheus-url http://localhost:9090
```

Это создаст `reports/MemReport_<date>.json` и `reports/MemReport_<date>.md` — берём их как есть.

---

## Фаза 2: Генерация отчета (`generate_report.sh`)

Собирает все JSON-файлы в единый красивый Markdown-отчет:

```markdown
# Ночной отчёт проверки ArtasWriter — 2026-10-20

## 📊 Сводка
- **Статус:** ✅ OK / ⚠️ Warning / ❌ Critical
- **Период:** 2026-10-19 18:00 — 2026-10-20 18:00
- **Writer uptime:** 99.8% (1 рестарт в 03:15)
- **PG размер:** 2.4 GB (+120 MB за сутки)

---

## 🔴 Ошибки Loki (24ч)
### Критические ошибки: 3
1. `[03:15:22] Failed to connect to Finam gRPC` — повторился 3 раза
   - **Рекомендация:** Проверить сеть/токен Finam, возможно нужен рефреш токена
2. `[14:22:10] PostgreSQL connection timeout`
   - **Рекомендация:** Увеличить ConnectionTimeout в appsettings.json (текущий: 30s)

### Предупреждения: 15
...

---

## 🐘 PostgreSQL
### Размер БД: 2.4 GB
| Символ | Размер | Записей (24ч) |
|--------|--------|---------------|
| CNYRUBF | 1.2 GB | 45,230 |
| ... | ... | ... |

### Проблемы:
- ⚠️ Пустой интервал для MOEXBR: 3 часа (12:00-15:00)
- ✅ Autovacuum в норме

---

## 📈 Метрики
| Метрика | Значение | Норма |
|---------|----------|-------|
| Фракталов обнаружено | 125,430 | >100,000 |
| gRPC ошибок | 12 | <50 |
| Память (RSS) | 256 MB | <512 MB |

---

## 🔍 Анализ памяти (MemReport)
[Вставка из MemReport]

---

## ✅ Рекомендации
1. **Высокий приоритет:** Проверить подключение Finam в 03:15 — 3 повторения
2. **Средний приоритет:** Увеличить ConnectionTimeout до 60s
3. **Низкий приоритет:** Размер БД растёт на 120 MB/день — через 2 месяца будет 10 GB,需要考虑 партицирование
```

---

## Фаза 3: Автоматизация

### Вариант A: Cron в WSL

```bash
# Добавить в crontab WSL (crontab -e)
0 6 * * * /mnt/d/work/Artas2026/scripts/run_nightly_check.sh >> /var/log/artas/nightly_check.log 2>&1
```

### Вариант B: systemd timer (более надёжно)

```ini
# /etc/systemd/system/artas-nightly-check.service
[Unit]
Description=ArtasWriter Nightly Check
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
ExecStart=/mnt/d/work/Artas2026/scripts/run_nightly_check.sh
WorkingDirectory=/mnt/d/work/Artas2026

[Install]
WantedBy=multi-user.target
```

```ini
# /etc/systemd/system/artas-nightly-check.timer
[Unit]
Description=Run nightly check daily at 6:00

[Timer]
OnCalendar=*-*-* 06:00:00
Persistent=true

[Install]
WantedBy=timers.target
```

---

## Технические детали

### Парсинг JSON в bash
Используем `jq` (есть в WSL):

```bash
# Пример: подсчитать количество ошибок
errors_count=$(curl -s "$LOKI_URL/query_range?query=..." | jq '.data.result[0].values | length')
```

### Подключение к PG
```bash
docker exec -i postgres_artas psql -U artas -d artas -t -A -c "SELECT ..."
# или через psql напрямую, если PG в том же Docker network
```

### Обработка ошибок
- Каждая стадия возвращает код: 0 = OK, 1 = warning, 2 = critical
- Итоговый статус отчета = максимум из всех стадий
- Ошибка на стадии не прерывает остальные (логгируется)

### Хранение отчетов
- Markdown-отчет в `reports/nightly_YYYY-MM-DD.md`
- JSON-файлы можно коммитить в git для истории
- Очистка старых отчетов > 90 дней (через cron)

---

## Порядок реализации

| # | Задача | Статус |
|---|--------|--------|
| 1 | Создать структуру папок `scripts/nightly_check/` | ⬜ |
| 2 | Создать `config.env` с переменными окружения | ⬜ |
| 3 | Написать `check_loki.sh` — запросы к Loki API | ⬜ |
| 4 | Написать `check_postgres.sh` — SQL-запросы через psql | ⬜ |
| 5 | Написать `check_metrics.sh` — запросы к Prometheus | ⬜ |
| 6 | Написать `run_memreport.sh` — обертка для MemReport | ⬜ |
| 7 | Написать `generate_report.sh` — сборка Markdown | ⬜ |
| 8 | Написать `nightly_check.sh` — оркестратор всех стадий | ⬜ |
| 9 | Протестировать вручную: `./scripts/run_nightly_check.sh` | ⬜ |
| 10 | Настроить cron/systemd для автоматического запуска ночью | ⬜ |
| 11 | **(Опционально)** Добавить отправку отчета в Telegram | ⬜ |

---

## Зависимости

- **WSL2** с доступом к Docker
- **Docker** запущен (PostgreSQL, Loki, Promtail, Prometheus, OTel)
- **jq** — для парсинга JSON в bash
- **curl** — для HTTP-запросов к API
- **psql** — для подключения к PostgreSQL (или `docker exec ... psql`)
- **.NET 10 SDK** — для запуска MemReport

---

## Критерии готовности

1. ✅ Скрипт запускается одной командой из корня проекта
2. ✅ Генерируется единый Markdown-отчет в `reports/`
3. ✅ Все 4 стадии выполняются даже при ошибке на предыдущей
4. ✅ Итоговый статус (OK/Warning/Critical) корректен
5. ✅ Настроен автоматический запуск ночью через cron/systemd
6. ✅ Логи выполнения пишутся в `/var/log/artas/nightly_check.log`
