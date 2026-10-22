# ArtasTools.MonitorAgent (LLMMetrics) launcher (.NET 10, nightly LLM agent)
#
# Использование:
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1                     # одиночный прогон: только сбор фактов (-DryRun по умолчанию)
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -RunNow             # полный прогон с LLM-диагностикой
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -RunNow -Hours 12   # своё окно анализа (часов назад)
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Daemon             # планировщик + health-эндпоинт (foreground)
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Daemon -Background # то же, в фоне (логи в <ReportsDir>\logs)
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Stop               # остановить фоновый демон, запущенный этим скриптом
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Check              # проверить доступность Loki и Prometheus
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Test               # dotnet test по решению ArtasTools.slnx
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -Info               # показать разрешённую конфигурацию
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -NoBuild ...        # без пересборки
#   pwsh -File src/LLMMetrics/run_LLMMetrics.ps1 -NoPause ...        # не ждать клавиши (автозапуски/CI)
#
# Режимы (взаимно исключают друг друга, проверяются в этом порядке):
#   -Test > -Check > -Info > -Stop > -Daemon > -RunNow > -DryRun (умолчание)
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
#   pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMMetrics/run_LLMMetrics.ps1' -NoPause"
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

    # --- Параметры прогона ---
    [int]$Hours = 0,
    [string]$Since = "",

    # --- Сборка/опции ---
    [switch]$NoBuild,
    [switch]$NoSemanticKernel,
    [switch]$Roslyn,
    [switch]$CodeChange,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

$ScriptDir   = $PSScriptRoot
# Корень репозитория (src/LLMMetrics -> ../..). База для .env и fallback-каталогов.
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$Project     = Join-Path $ScriptDir "ArtasTools.MonitorAgent\ArtasTools.MonitorAgent.csproj"
$Solution    = Join-Path $ProjectRoot "src\Tools\ArtasTools.slnx"
$TestsProj   = Join-Path $ScriptDir "ArtasTools.MonitorAgent.Tests\ArtasTools.MonitorAgent.Tests.csproj"
$SkTestsProj = Join-Path $ScriptDir "ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests\ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests.csproj"

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
# Парсер идентичен run_Writer.ps1 / run_Stat.ps1: KEY=VALUE, первый '=',
# кавычки обрезаются, УЖЕ заданные переменные НЕ перезаписываются.
# Отличие: здесь грузятся ОБА файла — локальный .env агента (приоритет)
# и корневой .env (LOGS_DIR и общие настройки), не перекрывая друг друга.
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

$ReportsDir   = Get-EnvOr "AGENT__REPORTSDIR" (Join-Path $ProjectRoot "reports\monitoring")
$DatabasePath = Get-EnvOr "AGENT__DATABASEPATH" (Join-Path $ReportsDir "facts.db")
$HealthPort   = Get-EnvOr "AGENT__HEALTHPORT" "8091"
$LokiUrl      = Get-EnvOr "Loki__BaseUrl" "http://localhost:3100"
$PromUrl      = Get-EnvOr "Prometheus__BaseUrl" "http://localhost:9090"
$LlmProvider  = Get-EnvOr "LLM__PROVIDER" "custom"
$LlmEndpoint  = Get-EnvOr "LLM__ENDPOINT" ""
$LlmModel     = Get-EnvOr "LLM__MODEL" "gpt-4.1-mini"
$LlmApiKey    = Get-EnvOr "LLM__APIKEY" ""
$RoslynOn     = Get-EnvOr "ROSLYN__ENABLED" "false"
$ChangeOn     = Get-EnvOr "CODECHANGE__ENABLED" "false"
$Orchestrator = Get-EnvOr "AGENT__ORCHESTRATION__ORCHESTRATOR" "native"

# Каталог логов агента. НАМЕРЕННО не общий LOGS_DIR: Promtail собирает
# <LOGS_DIR>\*.log и индексирует в Loki только writer.* / stat.*, а вывод
# агента в Loki не нужен (он сам их читает и создал бы обратную связь).
$AgentLogDir = Join-Path $ReportsDir "logs"
$PidFile     = Join-Path $AgentLogDir "monitor-agent.pid"

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
    Write-Host "[run] Building ArtasTools.MonitorAgent ($Configuration)..." -ForegroundColor Cyan
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
    Write-Host "=== ArtasTools.MonitorAgent: разрешённая конфигурация ===" -ForegroundColor Cyan
    Write-Host "Каталог скрипта   : $ScriptDir"
    Write-Host "Корень репозитория: $ProjectRoot"
    Write-Host "Проект            : $Project"
    Write-Host "Решение           : $Solution  (существует: $(Test-Path $Solution))"
    Write-Host ""
    Write-Host "--- Отчёты и хранение ---" -ForegroundColor Cyan
    Write-Host "ReportsDir        : $ReportsDir"
    Write-Host "DatabasePath      : $DatabasePath"
    Write-Host "Каталог логов     : $AgentLogDir"
    Write-Host "LOGS_DIR (общий)  : $(Get-EnvOr 'LOGS_DIR' '<не задан>')"
    Write-Host ""
    Write-Host "--- Источники фактов ---" -ForegroundColor Cyan
    Write-Host "Loki              : $LokiUrl"
    Write-Host "Prometheus        : $PromUrl"
    Write-Host ""
    Write-Host "--- LLM ---" -ForegroundColor Cyan
    Write-Host "Provider          : $LlmProvider"
    Write-Host "Endpoint          : $(if ($LlmEndpoint) { $LlmEndpoint } else { '<пусто> — реальный прогон невозможен' })"
    Write-Host "Model             : $LlmModel"
    Write-Host "ApiKey            : $(if ($LlmApiKey) { "задан (${($LlmApiKey.Length)} симв.)" } else { '<пусто>' })"
    Write-Host ""
    Write-Host "--- Агент ---" -ForegroundColor Cyan
    Write-Host "Orchestrator      : $Orchestrator"
    Write-Host "RunAtUtc          : $(Get-EnvOr 'AGENT__RUNATUTC' '03:00')"
    Write-Host "WindowHours       : $(Get-EnvOr 'AGENT__WINDOWHOURS' '24')"
    Write-Host "HealthPort        : $HealthPort  (занят: $(Test-AgentPort ([int]$HealthPort)))"
    Write-Host "Roslyn enabled    : $RoslynOn"
    Write-Host "CodeChange enabled: $ChangeOn"
    Write-Host "Context path      : $(Get-EnvOr 'Context__Sources__local-folder__Path' 'docs')"
    Write-Host ""
    Write-Host "--- Файлы ---" -ForegroundColor Cyan
    Write-Host "appsettings.json  : $(if (Test-Path (Join-Path $ScriptDir 'ArtasTools.MonitorAgent\appsettings.json')) { 'есть (переопределит .env-молча)' } else { 'нет — конфигурация только из .env/окружения' })"
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
    # для соответствующего решения. Проекты LLMMetrics входят в src/Tools/ArtasTools.slnx.
    if ($NoSemanticKernel) {
        Write-Host "[run] dotnet test: только основные тесты агента (SK отключён)" -ForegroundColor Cyan
        $targets = @($TestsProj)
    } elseif (Test-Path $Solution) {
        Write-Host "[run] dotnet test решение: $Solution" -ForegroundColor Cyan
        $targets = @($Solution)
    } else {
        Write-Host "[run] Решение не найдено, тестируем проекты LLMMetrics напрямую" -ForegroundColor Yellow
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
# РЕЖИМ: -Daemon (планировщик + health-эндпоинт)
# ===============================================================
if ($Daemon -and ($Background -or -not $Foreground)) {
    # Фоновый режим по умолчанию (как у run_Writer.ps1): закрытие консоли
    # не останавливает агента. -Foreground возвращает запуск в текущий терминал.
    if (Test-AgentPort ([int]$HealthPort)) {
        Write-Host "[run] Порт health ($HealthPort) уже занят — демон, вероятно, уже запущен." -ForegroundColor Yellow
        Write-Host "[run] Повторный запуск отменён. Остановка: run_LLMMetrics.ps1 -Stop" -ForegroundColor DarkGray
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
    Write-Host "[run] Стоп  : run_LLMMetrics.ps1 -Stop" -ForegroundColor DarkGray
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
    if (-not $LlmEndpoint) {
        Write-Host "[run] LLM__ENDPOINT пуст: полный прогон (-RunNow) не сможет обратиться к модели." -ForegroundColor Red
        Write-Host "[run] Задайте LLM__ENDPOINT/LLM__APIKEY в $ScriptDir\.env или используйте -DryRun." -ForegroundColor DarkGray
        Exit-WithPause 4
    }
    if (-not $LlmApiKey) {
        Write-Host "[run] LLM__APIKEY пуст: запрос к $($LlmEndpoint) получит 401." -ForegroundColor Red
        Write-Host "[run] Задайте ключ в $ScriptDir\.env или используйте -DryRun (LLM не вызывается)." -ForegroundColor DarkGray
        Exit-WithPause 4
    }
    if ($Orchestrator -eq "semantic-kernel") {
        Write-Host "[run] Orchestrator=semantic-kernel требует AddSemanticKernelOrchestrator() в DependencyInjection.cs" -ForegroundColor Red
        Write-Host "[run] (сейчас не вызывается — см. README). Используйте AGENT__ORCHESTRATION__ORCHESTRATOR=native." -ForegroundColor DarkGray
        Exit-WithPause 4
    }
} else {
    Write-Host "[run] Режим по умолчанию: -DryRun (сбор фактов + snapshot, LLM не вызывается)." -ForegroundColor DarkGray
    Write-Host "[run] Полный прогон с диагностикой: run_LLMMetrics.ps1 -RunNow" -ForegroundColor DarkGray
    $DryRun = $true
}

# Аргументы приложения. ФОРМАТ ВАЖЕН: Program.cs разбирает их префиксом
# "--hours=" / "--since=" (со знаком '='), иначе параметр не подхватится.
$appArgs = @()
if ($DryRun) { $appArgs += "--dry-run" } else { $appArgs += "--run-now" }
if ($Hours -gt 0) { $appArgs += "--hours=$Hours" }
if ($Since) { $appArgs += "--since=$Since" }

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
