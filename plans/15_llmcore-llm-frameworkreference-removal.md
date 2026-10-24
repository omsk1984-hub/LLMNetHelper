# План: замена FrameworkReference Microsoft.AspNetCore.App на точечные пакеты в LLMCore.Llm

> **Дата:** 2026-10-24
> **Статус:** Выполнен
> **Контекст:** Отложенный пункт плана 14 (§1.5)

---

## 1. Проблема

[`LLMCore.Llm.csproj`](../src/LLMCore/LLMCore.Llm/LLMCore.Llm.csproj:26) содержит:

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

Это тянет **весь** shared framework ASP.NET Core (~200 сборок) ради четырёх
пространств имён, реально используемых кодом:

| Используемый API | Пакет |
|---|---|
| `IHttpClientFactory`, `AddHttpClient()` | `Microsoft.Extensions.Http` |
| `IOptions<T>`, `services.Configure<T>()` | `Microsoft.Extensions.Options` (+ `.ConfigurationExtensions` для `Configure<T>(IConfiguration)`) |
| `IConfiguration.GetSection()`, `Get<T>()` | `Microsoft.Extensions.Configuration.Binder` |
| `IServiceCollection.AddSingleton()`, `GetRequiredService()` | `Microsoft.Extensions.DependencyInjection` |

Для библиотеки ядра (`LLMCore.*`) ссылка на ASP.NET Core некорректна:
проект не является веб-приложением и не должен зависеть от Kestrel, MVC,
Razor и прочих компонентов фреймворка.

## 2. Проверка офлайн-кэша

Все необходимые пакеты версии **10.0.0** присутствуют в
`C:\Users\Dionis\.nuget\packages`:

| Пакет | Версии в кэше | Берём |
|---|---|---|
| `Microsoft.Extensions.Http` | 8.0.0, 8.0.1, **10.0.0** | 10.0.0 |
| `Microsoft.Extensions.Configuration.Binder` | 2.2.0 … **10.0.0** | 10.0.0 |
| `Microsoft.Extensions.DependencyInjection` | 5.0.1 … **10.0.0**, 10.0.2 | 10.0.0 |
| `Microsoft.Extensions.Options` | 1.0.2 … **10.0.0**, 10.0.3, 10.0.8 | 10.0.0 (транзитивно через Http) |
| `Microsoft.Extensions.Configuration.Abstractions` | … **10.0.0**, 10.0.3, 10.0.8 | 10.0.0 (транзитивно) |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | … **10.0.0**, 10.0.2 … 10.0.8 | 10.0.0 (транзитивно) |
| `Microsoft.Extensions.Logging` | … **10.0.0** | 10.0.0 (транзитивно через Http) |
| `Microsoft.Extensions.Logging.Abstractions` | … **10.0.0**, 10.0.3 … 10.0.8 | 10.0.0 (транзитивно) |
| `Microsoft.Extensions.Diagnostics` | 8.0.0, 8.0.1, **10.0.0** | 10.0.0 (транзитивно через Http) |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 8.0.0, 9.0.0, **10.0.0** | 10.0.0 (транзитивно) |
| `Microsoft.Extensions.Primitives` | … **10.0.0**, 10.0.3 … 10.0.8 | 10.0.0 (транзитивно) |
| `System.Diagnostics.DiagnosticSource` | … **10.0.0**, 10.0.8 | 10.0.0 (транзитивно) |

Транзитивный граф `Microsoft.Extensions.Http` 10.0.0 (net10.0) покрывает
все зависимости: Logging, Diagnostics, Options, DI.Abstractions,
Configuration.Abstractions. Конфликтов версий не ожидается — все пакеты
одной мажорной линейки 10.0.0.

## 3. Шаги выполнения

### Шаг 1. Изменить `LLMCore.Llm.csproj`

Удалить:
```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

Добавить в `<ItemGroup>` с пакетами:
```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
```

Обновить комментарий (убрать упоминание FrameworkReference).

### Шаг 2. Верификация

```
dotnet build src/LLMNetHelper/LLMNetHelper.slnx -c Release
dotnet test  src/LLMNetHelper/LLMNetHelper.slnx
```

Критерии:
- Сборка без ошибок и без NU1510/NU1605.
- Все офлайн-тесты зелёные.
- `LLMCore.Llm.dll` не ссылается на сборки из shared framework
  `Microsoft.AspNetCore.App` (проверяется отсутствием ошибок компиляции —
  если бы код использовал типы исключительно из фреймворка, сборка упала бы).

### Шаг 3. Обновить план 14

В [`plans/14_llmcore-abstractions-extraction-plan.md`](14_llmcore-abstractions-extraction-plan.md)
§1.5 пометить пункт как выполненный.

## 4. Риски и откат

- **Риск:** транзитивный граф `Microsoft.Extensions.Http` потянет пакет,
  отсутствующий в офлайн-кэше → NU1102.
  **Митигация:** все зависимости проверены по кэшу (см. §2). При неудаче —
  вернуть `FrameworkReference` одной строкой.
- **Риск:** NU1605 (package downgrade) из-за пересечения с `Microsoft.Extensions.AI` 10.5.0.
  **Митигация:** MEAI 10.5.0 зависит от `Microsoft.Extensions.AI.Abstractions` 10.5.0,
  который не пересекается с линейкой `Microsoft.Extensions.*` 10.0.0 по типам.
  `Directory.Build.props` уже подавляет NU1605.

## 5. Затрагиваемые файлы

**Изменяемые:** [`src/LLMCore/LLMCore.Llm/LLMCore.Llm.csproj`](../src/LLMCore/LLMCore.Llm/LLMCore.Llm.csproj)
**Документация:** [`plans/14_llmcore-abstractions-extraction-plan.md`](14_llmcore-abstractions-extraction-plan.md) (пометка)
