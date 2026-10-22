# Правило: запуск PowerShell-скриптов (.ps1) в проекте Artas2026

## Контекст проблемы

Терминал, в который Roo отправляет команды (`execute_command`), стартует в
**`cmd.exe`**, а не в PowerShell. При этом в системе ассоциация расширения `.ps1`
переназначена на `notepad.exe`:

```
ftype Microsoft.PowerShellScript.1 = "C:\Windows\System32\notepad.exe" "%1"
```

Из-за этого команда вида `.\run_Reader.ps1` **не исполняется**, а передаётся
диспетчеру ассоциаций Windows, который открывает файл в **Блокноте**.
Дополнительно PowerShell стартует с `[Console]::OutputEncoding = CP866`, тогда
как читатель вывода ожидает UTF-8 — кириллица превращается в «кракозябры».

## ОБЯЗАТЕЛЬНО: как запускать любой .ps1

**Никогда не запускать скрипт как `.\script.ps1` или `script.ps1`.**

Всегда вызывать интерпретатор PowerShell 7 явно, через `pwsh` с флагом `-File`,
предваряя установкой UTF-8 для корректного вывода кириллицы:

```
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & '<путь_к_скрипту>' <параметры>"
```

Для простых скриптов без вложенных вызовов допускается сокращённая форма:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File "<путь_к_скрипту>" <параметры>
```

но она **не гарантирует корректную кириллицу** в выводе — используйте её только
когда вывод ASCII-only.

### Правила вызова

- `-NoProfile` — не грузить пользовательский профиль (детерминированный запуск).
- `-ExecutionPolicy Bypass` — страховка от ExecutionPolicy (в проекте
  `RemoteSigned`, но Bypass делает запуск независимым от политики).
- `-File` / оператор `&` — принципиально важно: заставляет PowerShell **исполнять**
  файл, минуя ассоциацию `.ps1` → notepad.
- Путь указывать относительно корня репозитория `d:/work/Artas2026`
  (например `src/Reader/run_Reader.ps1`), т.к. `cwd` фиксирован рабочей папкой.
- Параметры скрипта передавать после пути к скрипту.
- Для автоматических (неинтерактивных) запусков всегда добавлять `-NoPause`,
  иначе скрипт будет ждать нажатия клавиши и терминал «зависнет».

### Блок установки UTF-8 (обязательный префикс)

Внутри `-Command` (до оператора `&`) всегда выполнять:

```powershell
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding            = [Text.Encoding]::UTF8
chcp 65001 > $null
```

Причина: процесс PowerShell стартует с `[Console]::OutputEncoding = CP866`, а в
Windows PowerShell 5.1 `$OutputEncoding` вообще `US-ASCII (20127)`. Без этого
кириллица в выводе ломается.

## Область применения

Правило действует для **всех** `.ps1` в репозитории, в том числе:

- `src/Reader/run_Reader.ps1`
- `src/Writer/run_Writer.ps1`
- `src/Stat/run_Stat.ps1`
- `src/Tools/run_MemDiagnostics.ps1`
- `src/Tools/MemReport/run_MemReport.ps1`
- `src/Tools/ReportAnalyzer/run_ReportAnalyzer.ps1`
- `db/apply-migrations.ps1`, `db/update.ps1`
- `scripts/*.ps1`

## Примеры

Запуск проверки БД Reader без GUI:

```
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/Reader/run_Reader.ps1' -NoBuild -NoPause -CheckDb Si"
```

Запуск тестов Stat-скрипта в фоне без пересборки:

```
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/Stat/run_Stat.ps1' -NoBuild -NoPause -Symbol USDRUBF"
```

## ВАЖНО: кириллица при ВЛОЖЕННЫХ .ps1 (`& script.ps1`)

Проверено экспериментально (см. подробный разбор в
[`docs/powershell-nested-cyrillic-encoding.md`](../docs/powershell-nested-cyrillic-encoding.md)).

Вложенный `.ps1`, вызванный через оператор `&` (как `run_*.ps1` вызывают друг
друга), исполняется в **том же .NET-хосте PowerShell** и пишет в **общий
консольный handle** родителя. Кодировка вывода фиксируется при старте хоста,
поэтому:

- Если родитель запущен **с UTF-8-префиксом** → вложенный скрипт наследует
  `utf-8`, кириллица корректна на всех уровнях.
- Если родитель в **CP866** → кириллица битая **и во внешнем, и во вложенном**
  скрипте. Собственная установка `[Console]::OutputEncoding = UTF8` **внутри**
  вложенного скрипта при этом **НЕ помогает** — тот же хост не переоткрывает
  выходной поток.
- Если вложенный скрипт запущен **отдельным процессом** (`pwsh -File ...`), он
  открывает новый консольный handle — здесь самолечение UTF-8 **работает**, даже
  если родитель в CP866.

Вывод: кодировку настраивать **один раз — на самом внешнем вызове**
(UTF-8-префикс выше). Полагаться на настройку внутри вложенных `.ps1` нельзя.

## ВАЖНО: кириллица из ДОЧЕРНИХ .NET-процессов

UTF-8-префикс в `pwsh -Command` лечит только вывод **самого PowerShell**
(`Write-Host` из скрипта). Вывод **дочерних .NET-приложений** он НЕ лечит.

Причина: дочерний процесс (`dotnet run`, `dotnet <dll>`) стартует со своей
`Console.OutputEncoding`, равной **коду консоли (CP866)**, и пишет байты в CP866.
Родительский pwsh (уже в UTF-8) читает эти байты как UTF-8 → кириллица искажается
(«кракозябры»). Проверено: искажение остаётся даже при `| Out-File -Encoding utf8`,
т.е. данные теряются на стороне дочернего процесса.

Места, где выводит C#-код (потенциально искажённая кириллица):

- `src/Reader/ArtasChart.Services/DiagnosticsService.cs` — `Console.WriteLine` для `-CheckDb`.
- `src/Reader/ArtasChart.App/Program.cs` — `Console.WriteLine("[start] ...")`.
- аналогично в `Stat.Worker`, `ArtasWriter.Worker`, `Tools/*`.

Лечится **только внутри C#** — установить UTF-8 на старте приложения, до первого
`Console.WriteLine`:

```csharp
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;
```

Установка переменной окружения у родителя (`chcp`, `$OutputEncoding`) на
дочерний .NET-процесс не влияет. Настройка родителя и настройка C# — две
независимые вещи, нужны обе, если нужен полностью корректный вывод.

## Запрещено

- `.\run_Reader.ps1` (уйдёт в Блокнот).
- `run_Reader.ps1` (то же самое).
- `powershell -File ...` без установки `[Console]::OutputEncoding` — сломает
  кириллицу (особенно Windows PowerShell 5.1 с `$OutputEncoding = US-ASCII`).
- Запуск без `-NoPause` в неинтерактивном контексте — терминал заблокируется на
  `pause`.
- Ожидать корректную кириллицу от дочернего `dotnet`/`dotnet run` без
  `Console.OutputEncoding = UTF8` в C#-коде приложения.
