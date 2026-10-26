# Правило: поиск и анализ кода через MCP-сервер `roslyn-codelens`

MCP-сервер `roslyn-codelens` (настроен в `.roo/mcp.json`) предоставляет
65 инструментов семантического анализа C#-решения поверх Roslyn. Он видит
**реальные ссылки, вызовы, типы и диагностики**, а не совпадения строк,
поэтому его результаты точнее и полнее, чем `search_files` / `codebase_search`.

## Область применения

- **Все режимы агента** (code, architect, ask, debug, orchestrator).
- Только код единственного решения проекта —
  `src/LLMNetHelper/LLMNetHelper.slnx` (14 проектов, C#).
- Для не-C# файлов (`.ps1`, `.sql`, `.md`, `.json`, `.env`, `.yml`) —
  обычные инструменты `read_file` / `search_files` / `codebase_search`.

## Приоритет: сначала семантика, потом текст

| Вопрос | Инструмент(ы) |
|---|---|
| Где определён символ, какова сигнатура? | `go_to_definition`, `get_method_source`, `get_overloads` |
| Кто ссылается / вызывает? | `find_references`, `find_callers`, `analyze_method` |
| Что затронет изменение (blast radius)? | `analyze_change_impact`, `get_call_graph` |
| Реализации интерфейса, наследники класса? | `find_implementations`, `get_type_hierarchy` |
| Обзор типа / файла одним вызовом? | `get_type_overview`, `get_file_overview`, `get_symbol_context` |
| Ошибки и предупреждения компилятора? | `get_diagnostics` |
| Какие исключения покидают метод? | `get_exception_flow`, `find_throw_sites`, `find_catch_blocks` |
| Какие тесты что покрывают? | `get_test_summary` (источник истины), `find_uncovered_symbols` |
| Стек-трейс из лога → файл и строка? | `resolve_stack_trace` |
| Циклы зависимостей, слоистость, граф проектов? | `find_circular_dependencies`, `check_architecture`, `get_project_dependencies` |
| Здоровье проекта: сложность, god objects, naming, async, IDisposable? | `get_project_health`, `get_complexity_metrics`, `find_god_objects`, `find_naming_violations`, `find_async_violations`, `find_disposable_misuse` |
| DI: где зарегистрирован тип, как сконструировать? | `get_di_registrations`, `get_instantiation_options` |
| Поиск символа по подстроке имени | `search_symbols` |
| Extension-методы и C# 14 extension-свойства для типа | `get_extension_methods` |
| Содержание сгенерированного кода | `get_source_generators`, `get_generated_code` |
| Closed-source сборка: API / IL | `inspect_external_assembly`, `peek_il` |

`search_files` / `codebase_search` применять **только** для того, что Roslyn
не видит: комментарии, строковые литералы, конфигурация, документация,
не-C# файлы. Если семантический поиск не дал результата — текстовый как
fallback, а не как первый выбор.

## Жизненный цикл решения

1. `list_solutions` — проверить, загружено ли решение (в начале сессии).
2. Не загружено → `load_solution` с путём
   `D:\work\LLMNetHelper\src\LLMNetHelper\LLMNetHelper.slnx` (~3 с).
   Для больших выборок — `background: true` + опрос `get_task_status`.
3. Массовые правки файлов / смена `Directory.Build.props` / NuGet →
   `rebuild_solution` (долго: предпочесть `start_background_task`).
4. По завершении работы — `unload_solution` для освобождения памяти.

## Безопасность (обязательно)

Изменяющие инструменты (`rename_symbol`, `change_signature`,
`apply_code_action`) **пишут на диск напрямую**, минуя файловые ограничения
режимов, и перечислены в `alwaysAllow` `.roo/mcp.json` — подтверждение
пользователя не запрашивается. Поэтому:

1. Вызывать только с `preview: true` (значение по умолчанию), изучать `edits`
   и `conflicts` в ответе.
2. Применять (`preview: false`) только на чистом дереве git; крупные и
   кросс-проектные правки — после явного согласия пользователя.
3. `trust_solution` со `scope: "persistent"` изменяет `trust.json` на диске —
   только с разрешения пользователя; для разовых проверок — `"session"`.
4. `find_unused_symbols` — список кандидатов, а не приговор: перед удалением
   проверять `find_references` (рефлексия, сериализация и DI-активация в
   ссылки не попадают).
5. Не выгружать активное решение (`unload_solution`), пока фоновая задача
   (`list_running_tasks`) не завершена.

## Известные грабли (кратко)

- `get_exception_flow` **не поддерживает конструкторы** → для них
  `find_throw_sites` + `find_catch_blocks`.
- Источник истины по покрытию — `get_test_summary` (поле `referencedSymbols`
  каждого теста); `find_tests_for_symbol` находит не все прямые тесты.
- `analyze_data_flow` / `analyze_control_flow`: диапазон строк обязан лежать
  в пределах **одного** списка операторов, иначе —
  `statements not within the same statement list`.
- `check_architecture`: поле `to` — всегда массив строк; `allowOnly`
  игнорирует framework/metadata-цели (для их запрета — явный `forbid`).
- `resolve_stack_trace`: корректно резолвятся кадры формата
  `Exception.ToString()` (`... in File.cs:line 30`), а не `File.cs:30`.
- `generate_test_skeleton`: `suggestedFilePath` условный — сверять путь и
  неймспейс с фактической структурой
  `src/LLMNetHelper/LLMNetHelper.Tests/<Область>/`.

## Полный справочник

Перед крупным анализом или при нестандартной задаче загрузить скилл
`roslyn-codelens-usage` (`.roo/skills/roslyn-codelens-usage/SKILL.md`):
каталог всех 65 инструментов, типовые сценарии, дефекты и обходные пути.
Правило — короткий автопилот; скилл — справочник по требованию.
