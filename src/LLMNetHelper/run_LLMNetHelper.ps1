# LLMNetHelper launcher (.NET 10, nightly LLM agent)
#
# Использование:
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1                     # одиночный прогон: только сбор фактов (-DryRun по умолчанию)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -RunNow             # полный прогон с LLM-диагностикой
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -RunNow -Hours 12   # своё окно анализа (часов назад)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Daemon             # планировщик + health-эндпоинт (foreground)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Daemon -Background # то же, в фоне (логи в <ReportsDir>\logs)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Stop               # остановить фоновый демон, запущенный этим скриптом
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Check              # проверить доступность Loki и Prometheus
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Test               # dotnet test по решению LLMNetHelper.slnx
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -CloneAll           # скачать/обновить все репозитории группы GitLab
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -CloneAll -ForceClone  # переклонировать заново (удалить существующие)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Chat               # интерактивный чат с ИИ по скачанным репозиториям (GitLab:CloneRoot)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Chat -Ask "вопрос" # один вопрос и выход (для скриптов/CI)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Chat -Repo name    # начать чат с одним репозиторием из CloneRoot
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Chat -Tools        # начать чат в агентском режиме (ReAct + repo_*-инструменты)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -Info               # показать разрешённую конфигурацию
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -NoBuild ...        # без пересборки
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -NoPause ...        # не ждать клавиши (автозапуски/CI)
#   pwsh -File src/LLMNetHelper/run_LLMNetHelper.ps1 -SkipPull ...       # не обновлять репозиторий перед прогоном
#
# Режимы (взаимно исключают друг друга, проверяются в этом порядке):
#   -Test > -Check > -Info > -Stop > -CloneAll > -Chat > -Daemon > -RunNow > -DryRun (умолчание)
#
# Режим -Chat: интерактивный REPL по вопросам из GitLab:CloneRoot (см. -CloneAll).
# Hosted-сервисы (health/планировщик) в этом режиме стартуют ВСЕГДА — порт
# health должен быть свободен (остановите демона: -Stop). Команды чата:
# /help /exit /repos /repo <имя> /tools /clear /status. LLM-креды (Endpoint/ApiKey)
# валидирует само приложение (Program.cs, код возврата 4) по слитной конфигурации
# appsettings.json + .env; GITLAB__TOKEN не требуется (работа с локальными копиями).
#
# УМОЛЧАНИЕ — -DryRun: детерминированный сбор фактов из Loki/Prometheus и
# snapshot-отчёт БЕЗ обращения к LLM. Это безопасно (не тратит токены, не
# требует API-ключа) и совпадает с принципом «сначала диагностика, потом модель».
# Полный прогон с LLM — только явным -RunNow.
#
# Безопасные дефолты конфигурации (см. README): Roslyn:Enabled=false,
# CodeChange:Enabled=false. Флаги -Roslyn / -CodeChange включают их только
# для этого запуска (переменные окружения имеют приоритет над .env).
#
# Коды возврата приложения:
#   0 — OK / WARNING (или dry-run успешен)
#   1 — сбой сбора фактов или LLM
#   2 — вердикт CRITICAL
#   3 — прогон уже идёт
#   4 — неверная конфигурация (валидируется здесь: код 4 возвращается скриптом)
#
# ВАЖНО (запуск из терминала/проводника): расширение .ps1 в этой системе
# ассоциировано с notepad.exe, поэтому вызывайте интерпретатор ЯВНО:
#   pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause"
# Подробности — .roo/rules-code/02-powershell.md

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # --- Режимы ---
    [switch]$DryRun,
    [switch]$RunNow,
    [switch]$Daemon,
    [switch]$Background,
    [switch]$Foreground,
    [switch]$Stop,
    [switch]$Check,
    [switch]$Info,
    [switch]$Test,
    [switch]$CloneAll,
    [switch]$ForceClone,
    [switch]$Chat,
    [string]$Ask = "",
    [string]$Repo = "",
    [switch]$Tools,

    # --- Параметры прогона ---
    [int]$Hours = 0,
    [string]$Since = "",

    # --- Сборка/опции ---
    [switch]$NoBuild,
    [switch]$NoSemanticKernel,
    [switch]$Roslyn,
    [switch]$CodeChange,
    [switch]$NoPause,
    [switch]$SkipPull
)

$ErrorActionPreference = "Stop"

$ScriptDir   = $PSScriptRoot
# Корень репозитория (src/LLMNetHelper -> ../..). База для .env и нормализации
# относительных каталогов (ReportsDir, Context-путь и т.д.).
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$Project     = Join-Path $ScriptDir "LLMNetHelper\LLMNetHelper.csproj"
# Собственное решение проекта (автономно: не зависит от других репозиториев).
$Solution    = Join-Path $ScriptDir "LLMNetHelper.slnx"
$TestsProj   = Join-Path $ScriptDir "LLMNetHelper.Tests\LLMNetHelper.Tests.csproj"
# SK-тесты живут в LLMCore (переименовано из LLMNetHelper.Orchestration.SemanticKernel.Tests)
$SkTestsProj = Join-Path $ScriptDir "..\LLMCore\LLMCore.Orchestration.SemanticKernel.Tests\LLMCore.Orchestration.SemanticKernel.Tests.csproj"

# --- Выход с ожиданием клавиши --------------------------------
# При запуске "Выполнить с помощью PowerShell" из проводника окно создаётся
# хостом powershell.exe и закрывается сразу после завершения скрипта.
# Ожидание реализовано внешним `cmd /c pause` — он гарантированно блокируется
# до нажатия клавиши. Отключается флагом -NoPause (обязательно для CI/автозапуска).
function Exit-WithPause([int]$Code) {
    if (-not $NoPause) {
        Write-Host ""
        Write-Host "[run] Нажмите любую клавишу для выхода..." -ForegroundColor DarkGray
        try {
            & cmd.exe /c "pause > nul"
        } catch {
            try { $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") } catch { }
        }
    }
    exit $Code
}

# --- Самолечение кодировки ------------------------------------
# Работает, только когда скрипт запущен СВОИМ процессом (pwsh -File ...).
# При вложенном вызове через & тот же хост не переоткрывает выходной поток —
# там UTF-8 нужно задавать на ВНЕШНЕМ вызове (см. правила проекта).
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

# --- Файл .env -------------------------------------------------
# Парсер: KEY=VALUE, первый '=', кавычки обрезаются,
# УЖЕ заданные переменные НЕ перезаписываются.
# Грузятся ОБА файла — локальный .env агента (приоритет) и корневой .env
# (если существует), не перекрывая друг друга.
function Import-EnvFile([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    Write-Host "[run] Loading env file: $Path" -ForegroundColor DarkGray
    Get-Content -LiteralPath $Path -Encoding UTF8 | ForEach-Object {
        $line = $_.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { return }
        $idx = $line.IndexOf("=")
        if ($idx -lt 1) { return }
        $key = $line.Substring(0, $idx).Trim()
        $value = $line.Substring($idx + 1).Trim().Trim('"').Trim("'")
        if (-not [System.Environment]::GetEnvironmentVariable($key, "Process")) {
            [System.Environment]::SetEnvironmentVariable($key, $value, "Process")
        }
    }
}

# Флаги -Roslyn / -CodeChange задаются ДО загрузки .env: установленная
# переменная окружения не перезаписывается значением из .env, поэтому флаг
# выигрывает у конфигурации файла.
if ($Roslyn) {
    [System.Environment]::SetEnvironmentVariable("ROSLYN__ENABLED", "true", "Process")
    Write-Host "[run] Roslyn-анализ включён (-Roslyn)" -ForegroundColor Yellow
}
if ($CodeChange) {
    [System.Environment]::SetEnvironmentVariable("CODECHANGE__ENABLED", "true", "Process")
    Write-Host "[run] Применение правок включено (-CodeChange): агент может создавать ветки и коммитить" -ForegroundColor Red
}

Import-EnvFile (Join-Path $ScriptDir ".env")
Import-EnvFile (Join-Path $ProjectRoot ".env")

# --- Разрешённая конфигурация ----------------------------------
# Значения читаются из окружения (после .env), с fallback на дефолты кода.
function Get-EnvOr([string]$Name, [string]$Default) {
    $v = [System.Environment]::GetEnvironmentVariable($Name, "Process")
    if ([string]::IsNullOrWhiteSpace($v)) { return $Default }
    return $v
}

# Нормализация каталожных путей: относительные разрешаются от корня
# репозитория, а не от произвольного рабочего каталога процесса. Результат
# всегда абсолютный — это делает .env переносимым и независимым от способа
# запуска (см. plans/7_standalone-migration-plan.md, R2).
function Resolve-ProjectPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($ProjectRoot, $Path))
}

# Экспортируем нормализованные значения обратно в окружение, чтобы процесс
# приложения (dotnet run) получил их уже абсолютными. Уже заданные процессом
# значения ПЕРЕЗАПИСЫВАЮТСЯ нормализованным: источник — тот же .env/окружение,
# меняется только форма (относительный -> абсолютный).
function Set-EnvNorm([string]$Name, [string]$Value) {
    $norm = Resolve-ProjectPath $Value
    [System.Environment]::SetEnvironmentVariable($Name, $norm, "Process")
    return $norm
}

$ReportsDir   = Set-EnvNorm "AGENT__REPORTSDIR" (Get-EnvOr "AGENT__REPORTSDIR" (Join-Path $ProjectRoot "reports\monitoring"))
$DatabasePath = Set-EnvNorm "AGENT__DATABASEPATH" (Get-EnvOr "AGENT__DATABASEPATH" (Join-Path $ReportsDir "facts.db"))
$HealthPort   = Get-EnvOr "AGENT__HEALTHPORT" "8091"
$LokiUrl      = Get-EnvOr "Loki__BaseUrl" "http://localhost:3100"
$PromUrl      = Get-EnvOr "Prometheus__BaseUrl" "http://localhost:9090"
$LlmProvider  = Get-EnvOr "LLM__PROVIDER" "custom"
$LlmEndpoint  = Get-EnvOr "LLM__ENDPOINT" ""
$LlmModel     = Get-EnvOr "LLM__MODEL" "qwen3.8-omni-flash"
$LlmApiKey    = Get-EnvOr "LLM__APIKEY" ""
# ВАЖНО: $LlmEndpoint/$LlmApiKey здесь — только env-слой, он используется для
# отображения (-Info/-Chat баннер). Валидация LLM-конфигурации (endpoint/key,
# включая appsettings.json) перенесена в приложение (Program.cs, код 4) —
# скрипт НЕ является гейтом для LLM-настроек.
$RoslynOn     = Get-EnvOr "ROSLYN__ENABLED" "false"
$ChangeOn     = Get-EnvOr "CODECHANGE__ENABLED" "false"
$Orchestrator = Get-EnvOr "AGENT__ORCHESTRATION__ORCHESTRATOR" "native"

# Нормализуем остальные каталожные опции (относительные -> от корня проекта).
foreach ($normVar in @(
    "Context__Sources__local-folder__Path",
    "ROSLYN__SOLUTIONPATH",
    "CODECHANGE__REPOSITORYPATH",
    "CODECHANGE__SOLUTIONPATH",
    "GITLAB__CLONEROOT"
)) {
    $cur = [System.Environment]::GetEnvironmentVariable($normVar, "Process")
    if ($cur) {
        [System.Environment]::SetEnvironmentVariable($normVar, (Resolve-ProjectPath $cur), "Process")
    }
}

# GitLab: дефолт протокола TLS. На рабочих станциях с Windows-прокси/SChannel
# рукопожатие TLS 1.3 может падать с Win32Exception 0x80004005 («Не удается
# установить связь с локальной системой безопасности») — Tls12 лечит это.
# Ставится ПОСЛЕ .env и только если значение ещё не задано, чтобы .env/окружение
# имели приоритет.
if (-not (Get-EnvOr "GITLAB__TLSPROTOCOLS" "")) {
    [System.Environment]::SetEnvironmentVariable("GITLAB__TLSPROTOCOLS", "Tls12", "Process")
}

$GitLabToken     = Get-EnvOr "GITLAB__TOKEN" ""
$GitLabCloneRoot = Get-EnvOr "GITLAB__CLONEROOT" (Join-Path $ProjectRoot "repos\GPM")
$GitLabGroup     = Get-EnvOr "GITLAB__GROUPNAME" "GPM/backend"

# Каталог логов агента — <ReportsDir>\logs. Отдельного общего LOGS_DIR
# (Writer/Stat/Promtail) в этом репозитории нет — проект автономен.
$AgentLogDir = Join-Path $ReportsDir "logs"
$PidFile     = Join-Path $AgentLogDir "monitor-agent.pid"

# Каталог Markdown-транскриптов диалогов чата (--chat): один файл на сеанс,
# logs\dialogs в корне репозитория. Нормализуется до абсолютного и
# экспортируется в окружение, чтобы процесс приложения писал ровно сюда,
# независимо от рабочего каталога (как GITLAB__CLONEROOT).
$DialogLogDir = Resolve-ProjectPath (Get-EnvOr "CHAT__DIALOGLOG__DIRECTORY" (Join-Path $ProjectRoot "logs\dialogs"))
[System.Environment]::SetEnvironmentVariable("CHAT__DIALOGLOG__DIRECTORY", $DialogLogDir, "Process")

# --- dotnet CLI ------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) {
        Write-Host "[run] dotnet CLI not found. Install .NET 10 SDK: https://aka.ms/dotnet/download" -ForegroundColor Red
        Exit-WithPause 4
    }
} else {
    $dotnet = "dotnet"
}

if (-not (Test-Path $Project)) {
    Write-Host "[run] Project not found: $Project" -ForegroundColor Red
    Exit-WithPause 4
}

# --- Проверка доступности порта -------------------------------
function Test-AgentPort([int]$Port) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $iar = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(500)
        if ($ok) { $client.EndConnect($iar) }
        $client.Close()
        return $ok
    } catch {
        return $false
    }
}

# --- Сборка ----------------------------------------------------
function Invoke-Build {
    if ($NoBuild) {
        Write-Host "[run] Сборка пропущена (-NoBuild)" -ForegroundColor DarkGray
        return
    }
    $buildArgs = @("build", $Project, "-c", $Configuration)
    if ($NoSemanticKernel) {
        # SK-проекты компилируются пустыми: быстрее и без ~30 пакетов SK в офлайн-кэше.
        $buildArgs += "-p:EnableSemanticKernel=false"
        Write-Host "[run] Semantic Kernel отключён (-NoSemanticKernel)" -ForegroundColor DarkGray
    }
    Write-Host "[run] Building LLMNetHelper ($Configuration)..." -ForegroundColor Cyan
    & $dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[run] Build failed with code $LASTEXITCODE" -ForegroundColor Red
        Exit-WithPause $LASTEXITCODE
    }
}

# ===============================================================
# РЕЖИМ: -Info (разрешённая конфигурация)
# ===============================================================
if ($Info) {
    Write-Host ""
    Write-Host "=== LLMNetHelper: разрешённая конфигурация ===" -ForegroundColor Cyan
    Write-Host "Каталог скрипта   : $ScriptDir"
    Write-Host "Корень репозитория: $ProjectRoot"
    Write-Host "Проект            : $Project"
    Write-Host "Решение           : $Solution  (существует: $(Test-Path $Solution))"
    Write-Host ""
    Write-Host "--- Отчёты и хранение ---" -ForegroundColor Cyan
    Write-Host "ReportsDir        : $ReportsDir  (существует: $(Test-Path $ReportsDir))"
    Write-Host "DatabasePath      : $DatabasePath"
    Write-Host "Каталог логов     : $AgentLogDir"
    Write-Host "Транскрипты чата  : $DialogLogDir"
    Write-Host ""
    Write-Host "--- Источники фактов ---" -ForegroundColor Cyan
    Write-Host "Loki              : $LokiUrl"
    Write-Host "Prometheus        : $PromUrl"
    Write-Host ""
    Write-Host "--- LLM (env-слой; приложение дополнительно мержит appsettings.json) ---" -ForegroundColor Cyan
    Write-Host "Provider          : $LlmProvider"
    Write-Host "Endpoint          : $(if ($LlmEndpoint) { $LlmEndpoint } else { '<в .env не задан — проверит приложение по appsettings.json>' })"
    Write-Host "Model             : $LlmModel"
    Write-Host "ApiKey            : $(if ($LlmApiKey) { "задан (${($LlmApiKey.Length)} симв.)" } else { '<в .env не задан — проверит приложение по appsettings.json>' })"
    Write-Host ""
    Write-Host "--- Агент ---" -ForegroundColor Cyan
    Write-Host "Orchestrator      : $Orchestrator"
    Write-Host "RunAtUtc          : $(Get-EnvOr 'AGENT__RUNATUTC' '03:00')"
    Write-Host "WindowHours       : $(Get-EnvOr 'AGENT__WINDOWHOURS' '24')"
    Write-Host "HealthPort        : $HealthPort  (занят: $(Test-AgentPort ([int]$HealthPort)))"
    Write-Host "Roslyn enabled    : $RoslynOn"
    Write-Host "CodeChange enabled: $ChangeOn"
    Write-Host "Context path      : $(Get-EnvOr 'Context__Sources__local-folder__Path' 'docs')  (существует: $(Test-Path (Get-EnvOr 'Context__Sources__local-folder__Path' (Join-Path $ProjectRoot 'docs'))))"
    Write-Host "DomainContext     : $(if (Get-EnvOr 'AGENT__ORCHESTRATION__DOMAINCONTEXT' '') { 'задан' } else { '<пусто — нейтральный промпт>' })"
    Write-Host ""
    Write-Host "--- Файлы ---" -ForegroundColor Cyan
    Write-Host "appsettings.json  : $(if (Test-Path (Join-Path $ScriptDir 'LLMNetHelper\appsettings.json')) { 'есть (валидируется приложением; .env/окружение приоритетнее)' } else { 'нет — конфигурация только из .env/окружения' })"
    Write-Host ".env (агента)     : $(if (Test-Path (Join-Path $ScriptDir '.env')) { 'есть' } else { 'нет — выполните: Copy-Item .env.example .env' })"
    Write-Host ".env (корня)      : $(if (Test-Path (Join-Path $ProjectRoot '.env')) { 'есть' } else { 'нет' })"
    Exit-WithPause 0
}

# ===============================================================
# РЕЖИМ: -Check (доступность Loki/Prometheus)
# ===============================================================
if ($Check) {
    $lokiOk = $false
    $promOk = $false
    Write-Host "[run] Проверка источников фактов..." -ForegroundColor Cyan

    # Loki: /ready. ВАЖНО: сразу после старта контейнера Loki отдаёт 503
    # («prologue: not ready» — прогрев TSDB-индекса и compactor), поэтому
    # 503 трактуется как «прогревается» и повторяется, а не как фатальный сбой.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            $r = Invoke-WebRequest -Uri "$LokiUrl/ready" -UseBasicParsing -TimeoutSec 10
            Write-Host "[check] Loki      OK  $LokiUrl/ready -> $($r.StatusCode) $([string]$r.Content)".TrimEnd() -ForegroundColor Green
            $lokiOk = $true
            break
        } catch {
            $status = $null
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
            if ($status -eq 503) {
                Write-Host "[check] Loki      прогревается ($attempt/5): 503 /ready -> $($_.Exception.Message)" -ForegroundColor Yellow
                Start-Sleep -Seconds 2
            } else {
                Write-Host "[check] Loki      FAIL $LokiUrl/ready -> $($_.Exception.Message)" -ForegroundColor Red
                break
            }
        }
    }
    if (-not $lokiOk) {
        Write-Host "[check] Loki не стал ready. Проверяем API чтения меток..." -ForegroundColor DarkGray
    }

    # Loki: список меток — показывает, какие потоки вообще есть (ожидается artas_writer/artas_stat).
    # Это ключевой признак: агент читает именно query_range, а не /ready.
    try {
        $r = Invoke-RestMethod -Uri "$LokiUrl/loki/api/v1/labels" -TimeoutSec 10
        $labels = @($r.data)
        $lokiOk = $true
        Write-Host "[check] Loki labels ($($labels.Count)): $($labels -join ', ')" -ForegroundColor DarkGray
        $jobs = @()
        foreach ($name in @("job", "app")) {
            try {
                $v = Invoke-RestMethod -Uri "$LokiUrl/loki/api/v1/label/$name/values" -TimeoutSec 10
                $jobs += "$name = $(@($v.data) -join ', ')"
            } catch { }
        }
        if ($jobs.Count) { Write-Host "[check] Loki values: $($jobs -join ' | ')" -ForegroundColor DarkGray }
    } catch {
        Write-Host "[check] Loki labels FAIL -> $($_.Exception.Message)" -ForegroundColor Red
    }

    # Prometheus: buildinfo — дешёвый признак живого API v1.
    try {
        $r = Invoke-RestMethod -Uri "$PromUrl/api/v1/status/buildinfo" -TimeoutSec 10
        Write-Host "[check] Prometheus OK  $PromUrl (version $($r.data.version))" -ForegroundColor Green
        $promOk = $true
    } catch {
        Write-Host "[check] Prometheus FAIL $PromUrl -> $($_.Exception.Message)" -ForegroundColor Red
    }

    # Целевые метки, на которые опираются запросы агента.
    if ($promOk) {
        try {
            $r = Invoke-RestMethod -Uri "$PromUrl/api/v1/labels" -TimeoutSec 10
            Write-Host "[check] Prometheus labels: $(@($r.data).Count) всего" -ForegroundColor DarkGray
        } catch { }
    }

    $bad = @()
    if (-not $lokiOk) { $bad += "Loki ($LokiUrl)" }
    if (-not $promOk) { $bad += "Prometheus ($PromUrl)" }

    if ($bad.Count -gt 0) {
        Write-Host ("[check] Недоступно: {0}" -f ($bad -join ', ')) -ForegroundColor Red
        Write-Host "[check] Без источника прогон соберёт пустую сводку." -ForegroundColor Yellow
        Write-Host "[check] Поднимите мониторинг: monitoring/docker-compose.monitoring.yml (см. docs/loki_integration.md)" -ForegroundColor DarkGray
        Exit-WithPause 1
    }
    Write-Host "[check] Все источники доступны." -ForegroundColor Green
    Exit-WithPause 0
}

# ===============================================================
# РЕЖИМ: -Test (dotnet test)
# ===============================================================
if ($Test) {
    # Правила проекта (.roo/rules-code/01-test.md): тесты только через `dotnet test`
    # для соответствующего решения. Все проекты входят в src/LLMNetHelper/LLMNetHelper.slnx.
    if ($NoSemanticKernel) {
        Write-Host "[run] dotnet test: только основные тесты агента (SK отключён)" -ForegroundColor Cyan
        $targets = @($TestsProj)
    } elseif (Test-Path $Solution) {
        Write-Host "[run] dotnet test решение: $Solution" -ForegroundColor Cyan
        $targets = @($Solution)
    } else {
        Write-Host "[run] Решение не найдено, тестируем проекты LLMNetHelper напрямую" -ForegroundColor Yellow
        $targets = @($TestsProj)
        if (Test-Path $SkTestsProj) { $targets += $SkTestsProj }
    }

    $failed = $false
    foreach ($t in $targets) {
        Write-Host "[test] $t" -ForegroundColor DarkGray
        & $dotnet test $t -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[test] Провал на $t (код $LASTEXITCODE)" -ForegroundColor Red
            $failed = $true
            break
        }
    }
    if ($failed) { Exit-WithPause $LASTEXITCODE }
    Write-Host "[test] Все тесты пройдены." -ForegroundColor Green
    Exit-WithPause 0
}

# ===============================================================
# РЕЖИМ: -Stop (остановка фонового демона)
# ===============================================================
if ($Stop) {
    # Останавливаем ТОЛЬКО процесс, запущенный этим скриптом (PID из файла),
    # чтобы не задеть Writer/Stat/Reader (правило проекта: процессы не трогать
    # без явного разрешения; разрешение здесь — сам флаг -Stop).
    if (-not (Test-Path $PidFile)) {
        Write-Host "[run] Файл PID не найден: $PidFile" -ForegroundColor Yellow
        Write-Host "[run] Демон, запущенный этим скриптом, не обнаружен. Остановка не выполнялась." -ForegroundColor DarkGray
        Exit-WithPause 0
    }

    $procId = 0
    if (-not [int]::TryParse((Get-Content -LiteralPath $PidFile -Raw).Trim(), [ref]$procId)) {
        Write-Host "[run] Повреждённый файл PID: $PidFile" -ForegroundColor Red
        Exit-WithPause 1
    }

    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($null -eq $proc) {
        Write-Host "[run] Процесс $procId уже завершён. Убираю файл PID." -ForegroundColor DarkGray
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        Exit-WithPause 0
    }

    Write-Host "[run] Останавливаю демон MonitorAgent (PID $procId, $($proc.ProcessName))..." -ForegroundColor Cyan
    Stop-Process -Id $procId -ErrorAction SilentlyContinue

    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
            Write-Host "[run] Демон остановлен." -ForegroundColor Green
            Exit-WithPause 0
        }
    }

    Write-Host "[run] Процесс $procId не завершился за 15 с." -ForegroundColor Red
    Exit-WithPause 1
}

# ===============================================================
# РЕЖИМ: -CloneAll (скачать/обновить репозитории группы GitLab)
# ===============================================================
if ($CloneAll -or $ForceClone) {
    if (-not $GitLabToken) {
        Write-Host "[clone] GITLAB__TOKEN пуст — клонирование приватных репозиториев невозможно." -ForegroundColor Red
        Write-Host "[clone] Задайте токен в $ScriptDir\.env" -ForegroundColor DarkGray
        Exit-WithPause 4
    }

    Invoke-Build

    # Включаем GitLab-подсистему только для этого запуска (флаг = переменная окружения,
    # которая имеет приоритет над appsettings.json). Каталог клонирования передаём
    # нормализованным абсолютным путём — относительный путь разрешился бы от рабочего
    # каталога процесса, а не от корня репозитория.
    [System.Environment]::SetEnvironmentVariable("GITLAB__ENABLED", "true", "Process")
    [System.Environment]::SetEnvironmentVariable("GITLAB__CLONEROOT", $GitLabCloneRoot, "Process")

    $appArgs = @("--clone-all")
    if ($ForceClone) { $appArgs += "--force-clone" }

    Write-Host "[clone] Группа  : $GitLabGroup" -ForegroundColor Cyan
    Write-Host "[clone] Каталог : $GitLabCloneRoot" -ForegroundColor Cyan
    Write-Host "[clone] Режим   : $(if ($ForceClone) { 'принудительное переклонирование' } else { 'клонирование новых + обновление существующих' })" -ForegroundColor Cyan

    New-Item -ItemType Directory -Force $AgentLogDir | Out-Null
    $logFile = Join-Path $AgentLogDir ("monitor-clone.{0:yyyy-MM-dd_HH-mm-ss}.log" -f (Get-Date))

    $dotnetArgs = @("run", "--project", $Project, "-c", $Configuration, "--no-build", "--") + $appArgs

    $exitCode = 0
    try {
        & $dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $logFile
        $exitCode = $LASTEXITCODE
    } catch {
        Write-Host "[clone] Ошибка запуска: $_" -ForegroundColor Red
        $exitCode = 1
    }

    switch ($exitCode) {
        0 { Write-Host "[clone] Готово: все репозитории синхронизированы." -ForegroundColor Green }
        1 { Write-Host "[clone] Есть неудачные операции — см. лог $logFile" -ForegroundColor Red }
        4 { Write-Host "[clone] Неверная конфигурация GitLab (токен/группа/права)." -ForegroundColor Red }
        default { Write-Host "[clone] Код возврата $exitCode." -ForegroundColor Yellow }
    }

    Exit-WithPause $exitCode
}

# ===============================================================
# РЕЖИМ: -Chat (консольный чат с ИИ по скачанным репозиториям)
# ===============================================================
# -Ask / -Repo / -Tools подразумевают -Chat.
if ($Ask -or $Repo -or $Tools) { $Chat = $true }

if ($Chat) {
    # LLM-креды НЕ проверяем здесь: валидация Endpoint/ApiKey (по слитной
    # конфигурации appsettings.json + .env) — в приложении, Program.cs (код 4).
    if (-not (Test-Path $GitLabCloneRoot)) {
        Write-Host "[chat] Каталог репозиториев не найден: $GitLabCloneRoot" -ForegroundColor Red
        Write-Host "[chat] Сначала скачайте репозитории: run_LLMNetHelper.ps1 -CloneAll" -ForegroundColor DarkGray
        Exit-WithPause 4
    }
    if ($Repo -and -not (Test-Path (Join-Path $GitLabCloneRoot $Repo))) {
        Write-Host "[chat] Репозиторий не найден: $GitLabCloneRoot\$Repo" -ForegroundColor Red
        Write-Host "[chat] Список репозиториев: run_LLMNetHelper.ps1 -Chat затем /repos" -ForegroundColor DarkGray
        Exit-WithPause 4
    }
    # Hosted-сервисы (HealthServer + планировщик) стартуют всегда — и в чате,
    # для наблюдаемости. Занятый порт health = демон уже работает.
    if (Test-AgentPort ([int]$HealthPort)) {
        Write-Host "[chat] Порт health ($HealthPort) уже занят — демон, вероятно, запущен." -ForegroundColor Yellow
        Write-Host "[chat] Чат не займёт порт. Остановите демона: run_LLMNetHelper.ps1 -Stop" -ForegroundColor DarkGray
        Write-Host "[chat] Либо задайте другой AGENT__HEALTHPORT в .env." -ForegroundColor DarkGray
        Exit-WithPause 4
    }

    Invoke-Build

    # Каталог клонирования передаём нормализованным абсолютным путём (как в
    # -CloneAll): относительный разрешился бы от рабочего каталога процесса.
    [System.Environment]::SetEnvironmentVariable("GITLAB__CLONEROOT", $GitLabCloneRoot, "Process")

    $appArgs = @("--chat")
    if ($Ask)  { $appArgs += "--ask=$Ask" }
    if ($Repo) { $appArgs += "--repo=$Repo" }
    if ($Tools) { $appArgs += "--tools" }

    Write-Host "[chat] Каталог : $GitLabCloneRoot" -ForegroundColor Cyan
    # Пустой env-endpoint — не ошибка: приложение смёржит appsettings.json
    # (Llm:Endpoint). Полную конфигурацию покажет баннер самого чата.
    Write-Host ("[chat] Модель  : " + $(if ($LlmEndpoint) { "$LlmModel @ $LlmEndpoint" } else { "$LlmModel @ <из appsettings.json>" })) -ForegroundColor Cyan
    Write-Host "[chat] Режим   : $(if ($Tools) { 'агентский (/tools, ReAct)' } else { 'поиск контекста (BM25)' })$(if ($Repo) { "; репозиторий $Repo" } else { '' })" -ForegroundColor Cyan
    Write-Host "[chat] Транскрипт: $DialogLogDir\chat-<UTC-дата-время>.md (Chat:DialogLog)" -ForegroundColor Cyan
    if (-not $Ask) {
        Write-Host "[chat] Команды : /help — справка, /exit — выход. Health: http://localhost:$HealthPort/health" -ForegroundColor DarkGray
    }

    # ВАЖНО: без Tee-Object/перенаправлений — интерактивный stdin должен
    # доходить до процесса dotnet. Логи приложения — через Serilog в logs.
    $dotnetArgs = @("run", "--project", $Project, "-c", $Configuration, "--no-build", "--") + $appArgs

    $exitCode = 0
    try {
        & $dotnet @dotnetArgs
        $exitCode = $LASTEXITCODE
    } catch {
        Write-Host "[chat] Ошибка запуска: $_" -ForegroundColor Red
        $exitCode = 1
    }

    switch ($exitCode) {
        0 { Write-Host "[chat] Завершено." -ForegroundColor Green }
        1 { Write-Host "[chat] Сбой чата (LLM/IO/хост)." -ForegroundColor Red }
        4 { Write-Host "[chat] Неверная конфигурация (LLM-креды / CloneRoot / порт health)." -ForegroundColor Red }
        default { Write-Host "[chat] Код возврата $exitCode." -ForegroundColor Yellow }
    }

    Exit-WithPause $exitCode
}

# ===============================================================
# РЕЖИМ: -Daemon (планировщик + health-эндпоинт)
# ===============================================================
if ($Daemon -and ($Background -or -not $Foreground)) {
    # Фоновый режим по умолчанию (как у run_Writer.ps1): закрытие консоли
    # не останавливает агента. -Foreground возвращает запуск в текущий терминал.
    if (Test-AgentPort ([int]$HealthPort)) {
        Write-Host "[run] Порт health ($HealthPort) уже занят — демон, вероятно, уже запущен." -ForegroundColor Yellow
        Write-Host "[run] Повторный запуск отменён. Остановка: run_LLMNetHelper.ps1 -Stop" -ForegroundColor DarkGray
        Exit-WithPause 0
    }

    Invoke-Build

    New-Item -ItemType Directory -Force $AgentLogDir | Out-Null
    $stdoutLog = Join-Path $AgentLogDir "monitor-agent.stdout.log"
    $stderrLog = Join-Path $AgentLogDir "monitor-agent.stderr.log"

    # Ротация: не более 7 дней вывода демона.
    $maxAge = 7 * 24 * 60 * 60
    $now = Get-Date
    Get-ChildItem $AgentLogDir -Filter "monitor-agent*.log" -ErrorAction SilentlyContinue | Where-Object {
        ($now - $_.LastWriteTime).TotalSeconds -gt $maxAge
    } | Remove-Item -Force -ErrorAction SilentlyContinue

    $dotnetArgs = @("run", "--project", $Project, "-c", $Configuration, "--no-build")

    Write-Host "[run] Starting MonitorAgent ($Configuration) в фоне..." -ForegroundColor Green
    $proc = Start-Process -FilePath $dotnet `
        -ArgumentList $dotnetArgs `
        -WorkingDirectory $ScriptDir `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -PassThru

    Set-Content -LiteralPath $PidFile -Value $proc.Id -Encoding UTF8

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { break }
        if (Test-AgentPort ([int]$HealthPort)) { $ready = $true; break }
    }

    if ($proc.HasExited) {
        Write-Host "[run] Демон завершился сразу (код $($proc.ExitCode))." -ForegroundColor Red
        Write-Host "[run] Смотрите $stderrLog / $stdoutLog" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        Exit-WithPause 1
    }

    if (-not $ready) {
        Write-Host "[run] Health-порт $HealthPort не поднялся за 20 с." -ForegroundColor Yellow
        Write-Host "[run] HttpListener на http://localhost:$HealthPort/ требует резервирования URL без прав админа:" -ForegroundColor DarkGray
        Write-Host "[run]   netsh http add urlacl url=http://localhost:$HealthPort/ user=$env:USERDOMAIN\$env:USERNAME" -ForegroundColor DarkGray
        Write-Host "[run] Логи: $stderrLog" -ForegroundColor DarkGray
        Exit-WithPause 1
    }

    Write-Host "[run] MonitorAgent запущен в фоне (PID $($proc.Id))." -ForegroundColor Green
    Write-Host "[run] Health: http://localhost:$HealthPort/health , http://localhost:$HealthPort/status" -ForegroundColor DarkGray
    Write-Host "[run] Логи  : $stdoutLog" -ForegroundColor DarkGray
    Write-Host "[run] Стоп  : run_LLMNetHelper.ps1 -Stop" -ForegroundColor DarkGray
    Exit-WithPause 0
}

# ===============================================================
# РЕЖИМ: -Daemon -Foreground
# ===============================================================
if ($Daemon) {
    if (Test-AgentPort ([int]$HealthPort)) {
        Write-Host "[run] Порт health ($HealthPort) уже занят — демон, вероятно, уже запущен." -ForegroundColor Yellow
        Exit-WithPause 0
    }
    Invoke-Build
    New-Item -ItemType Directory -Force $AgentLogDir | Out-Null
    $stdoutLog = Join-Path $AgentLogDir "monitor-agent.foreground.log"
    Write-Host "[run] Starting MonitorAgent ($Configuration) в текущем терминале (Ctrl+C для остановки)..." -ForegroundColor Green
    Write-Host "[run] Логи: $stdoutLog" -ForegroundColor DarkGray
    $dotnetArgs = @("run", "--project", $Project, "-c", $Configuration, "--no-build")
    try {
        & $dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $stdoutLog
        $exitCode = $LASTEXITCODE
    } catch {
        Write-Host "[run] Ошибка запуска: $_" -ForegroundColor Red
        $exitCode = 1
    }
    Exit-WithPause $exitCode
}

# ===============================================================
# РЕЖИМ: одиночный прогон (-RunNow / -DryRun по умолчанию)
# ===============================================================
# Валидация конфигурации до запуска (код возврата 4 — «неверная конфигурация»).
if ($RunNow) {
    # LLM-креды НЕ проверяем здесь: валидация Endpoint/ApiKey (по слитной
    # конфигурации appsettings.json + .env) — в приложении, Program.cs (код 4).
    if ($Orchestrator -eq "semantic-kernel") {
        Write-Host "[run] Orchestrator=semantic-kernel требует AddSemanticKernelOrchestrator() в DependencyInjection.cs" -ForegroundColor Red
        Write-Host "[run] (сейчас не вызывается — см. README). Используйте AGENT__ORCHESTRATION__ORCHESTRATOR=native." -ForegroundColor DarkGray
        Exit-WithPause 4
    }
} else {
    Write-Host "[run] Режим по умолчанию: -DryRun (сбор фактов + snapshot, LLM не вызывается)." -ForegroundColor DarkGray
    Write-Host "[run] Полный прогон с диагностикой: run_LLMNetHelper.ps1 -RunNow" -ForegroundColor DarkGray
    $DryRun = $true
}

# Аргументы приложения. ФОРМАТ ВАЖЕН: Program.cs разбирает их префиксом
# "--hours=" / "--since=" (со знаком '='), иначе параметр не подхватится.
$appArgs = @()
if ($DryRun) { $appArgs += "--dry-run" } else { $appArgs += "--run-now" }
if ($Hours -gt 0) { $appArgs += "--hours=$Hours" }
if ($Since) { $appArgs += "--since=$Since" }

# --- Обновление репозитория перед прогоном ----------------------
if (-not $SkipPull) {
    $repoRoot = $ProjectRoot
    if (Test-Path (Join-Path $repoRoot ".git")) {
        Write-Host "[pre] git fetch --all --prune..." -ForegroundColor DarkGray
        $fetch = & git -C $repoRoot fetch --all --prune 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[pre] git fetch OK" -ForegroundColor DarkGray
            # Pull hard — сброс к origin/main
            $branch = & git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null
            $remoteBranch = $branch.Trim()
            if (-not $remoteBranch) { $remoteBranch = "main" }

            Write-Host "[pre] git reset --hard origin/$remoteBranch..." -ForegroundColor DarkGray
            & git -C $repoRoot reset --hard "origin/$remoteBranch" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "[pre] fallback: git reset --hard origin/main..." -ForegroundColor DarkGray
                & git -C $repoRoot reset --hard origin/main 2>&1 | Out-Null
            }

            Write-Host "[pre] git clean -fd..." -ForegroundColor DarkGray
            & git -C $repoRoot clean -fd 2>&1 | Out-Null
        } else {
            Write-Host "[pre] git fetch failed (не критично): $($fetch -join ' ')" -ForegroundColor DarkGray
        }
    }
}

Invoke-Build

New-Item -ItemType Directory -Force $AgentLogDir | Out-Null
$logFile = Join-Path $AgentLogDir ("monitor-agent.{0:yyyy-MM-dd_HH-mm-ss}.log" -f (Get-Date))

$dotnetArgs = @("run", "--project", $Project, "-c", $Configuration, "--no-build", "--") + $appArgs

Write-Host "[run] Запуск прогона ($Configuration): $($appArgs -join ' ')" -ForegroundColor Green
Write-Host "[run] Loki=$LokiUrl  Prometheus=$PromUrl" -ForegroundColor DarkGray
Write-Host "[run] Отчёты: $ReportsDir" -ForegroundColor DarkGray
Write-Host "[run] Лог   : $logFile" -ForegroundColor DarkGray

$exitCode = 0
try {
    & $dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $logFile
    $exitCode = $LASTEXITCODE
} catch {
    Write-Host "[run] Ошибка запуска: $_" -ForegroundColor Red
    $exitCode = 1
}

switch ($exitCode) {
    0 { Write-Host "[run] Прогон завершён: OK / WARNING." -ForegroundColor Green }
    1 { Write-Host "[run] Прогон завершён СБОЕМ (сбор фактов или LLM)." -ForegroundColor Red }
    2 { Write-Host "[run] Вердикт CRITICAL — требуется вмешательство." -ForegroundColor Red }
    3 { Write-Host "[run] Прогон уже идёт другим экземпляром." -ForegroundColor Yellow }
    4 { Write-Host "[run] Неверная конфигурация агента." -ForegroundColor Red }
    default { Write-Host "[run] Код возврата $exitCode." -ForegroundColor Yellow }
}

Exit-WithPause $exitCode
