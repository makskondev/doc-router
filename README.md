# DocRouter — сервис «водитель документов»

Каркас v1: свой движок процессов на PostgreSQL, маршруты в коде, SMB через SMBLibrary, Oracle (view + хранимая процедура), REST-подпись, письма через outbox.

> Каркас написан без компиляции (в среде не было .NET SDK). Первый `dotnet build` может показать мелкие правки (версии пакетов, API SMBLibrary).

## Быстрый старт
```sh
./create-solution.sh                      # создаёт DocRouter.sln
dotnet build
dotnet test
dotnet ef migrations add Init -p src/DocRouter.Engine -s src/DocRouter.Host
dotnet run --project src/DocRouter.Host   # Database:AutoMigrate=true применит миграцию
```
Секреты (пароли SMB/Oracle/Postgres/SMTP) задавайте через переменные окружения k8s: `Smb__Password`, `Oracle__ConnectionString`, `ConnectionStrings__Postgres`.

## Где что
| Проект | Назначение |
|---|---|
| Core | `IStep`, `StepResult`, билдер маршрутов, модели, интерфейсы (SMB, подпись, Oracle, почта) |
| Engine | EF-хранилище состояния, захват процессов (lease + SKIP LOCKED), исполнитель, воркеры, ViewSource, outbox |
| Steps | Шаги: FindFile, ApplyStamp, WaitForSignatureFile, ReadSignatureFile, RestSign, VerifySignature, SaveSignature, MoveFile, DeleteFile |
| Routes | **Маршруты по типам документов** — основной файл разработчика (`DocumentRoutes.cs`) |
| Infrastructure | SmbFileStore, RestSignatureService, Oracle (view/процедура), SMTP |
| Tests | xUnit: билдер маршрутов, отбор файлов, шаги (на in-memory IFileStore), исполнитель (EF InMemory) |
| Host | ASP.NET Core, DI, healthchecks (`/health/live`, `/health/ready`, `/health/deps`) |

## Добавить новый тип документа
1. Секция в `DocumentTypes` (папки, получатели писем).
2. Строка в `DocumentRoutes.cs`: `r.For("NewType").UseTemplate(...)` или своя цепочка шагов.
3. Если тип приходит из view — поле `DOC_TYPE` во вью или `FixedDocType` в `Sources:Views`.

## Ручное вмешательство (v1, без UI)
Процесс в `Failed` → `UPDATE runs SET status='Pending', attempt=0, next_attempt_at=now() WHERE id='...'`
(перезапуск с шага N: дополнительно `step_name='<ИмяШага>'`, `step_index=N`); отмена: `status='Cancelled'`.
Журнал: таблица `run_steps`.

## Сценарии → маршруты (см. `DocumentRoutes.cs`)
| | Источник | Цепочка |
|---|---|---|
| A | view | FindFile → ApplyStamp → WaitForSignatureFile (.sig, до 24 ч) → ReadSignatureFile → Verify → Save → MoveFile(Result) → DeleteFile(.sig) |
| B | view | FindFile → RestSign → Verify → Save → MoveFile(Result) |
| C | каталог (`RequireSignature=false`) | то же, что B |
| D | каталог (`RequireSignature=true`, `<имя>.sig` рядом) | FindFile → ReadSignatureFile → Verify → Save → MoveFile(Result) → DeleteFile(.sig) |

Штамп: секция `Stamps:<имя>` (PNG-шаблон + строки с подстановками `{ПОЛЕ_ВЬЮ}`, `{Date}`), имя указывается в `ApplyStamp.Stamp`.
Шаблон штампа в контейнере монтируйте томом/ConfigMap (путь `TemplatePath`).
