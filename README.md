# DocRouter — сервис-«курьер» для документов

Один сервис (ASP.NET Core, 1 реплика в k8s), который «водит» файлы по маршрутам. **Без БД процессов**: состояние документа = где лежит файл (папки) + есть ли запись во view. При сбое маршрут просто проходится заново на следующем опросе, письмо уходит на каждую неудачу.

> Каркас написан без компиляции (в среде не было .NET SDK). Первый `dotnet build` может показать мелкие правки (версии пакетов, API SMBLibrary).

## Быстрый старт
```sh
./create-solution.sh && dotnet build && dotnet test
dotnet run --project src/DocRouter.Host
```
Секреты задавайте переменными окружения k8s: `Smb__Password`, `Oracle__ConnectionString`, `Smtp__Password`.
k8s: `replicas: 1`, `strategy: Recreate`; probes: `/health/live`, `/health/ready` (цикл опроса жив), `/health/deps` — только диагностика.

## Как это работает
Каждые `Courier:PollInterval` (5-15 мин):
1. **Источники** возвращают документы «в работе»: записи вью (`Sources:Views`) и файлы в `Incoming` маршрутов (`Sources:Directories`); тип документа = маршрут.
2. Для каждого документа **маршрут (цепочка шагов) идёт с первого шага**. Шаги идемпотентны и «знают своё место»: `FindFile` определяет, где файл лежит (Signed → ToSign → Incoming), и шаги, уже выполненные раньше, пропускаются (`ApplyStamp` не штампует повторно; `WaitForSignatureFile` ждёт молча).
3. `Next` → дальше; `Wait` → тихо до следующего опроса; `Fail`/исключение → лог + письмо (адресаты: тип + маршрут + по умолчанию), повтор на следующем опросе.

## Модель конфигурации: тип документа = маршрут
- **Routes** — ключ = имя маршрута = тип документа. Внутри: папки `Incoming/ToSign/Signed/Result`, `Stamp` (имя штампа, по умолчанию = имя маршрута), `ErrorRecipients`. Один каталог = один тип.
- **Sources** — откуда приходят документы:
  - `Views`: у вью задаётся либо `Route` (все записи — один тип), либо `DocTypeColumn` (значение колонки = имя маршрута; во вью могут быть разные типы). ID файла — колонка `IdColumn`.
  - `Directories`: `Route` — маршрут, чей `Incoming` опрашивается.
- **Stamps** — шаблоны штампов (ключ = имя штампа).
- Порядок шагов — в коде: `src/DocRouter.Routes/DocumentRoutes.cs`. Общая логика нескольких типов — шаблоны (`Template` + `UseTemplate`): например, `Invoice` и `Act` делят цепочку `RestSign`, но имеют свои папки и адресатов в конфиге.
- Письмо об ошибке: `Routes:<маршрут>:ErrorRecipients` + `Courier:DefaultRecipients`.

## Сценарии → маршруты
| | Источник | Цепочка |
|---|---|---|
| A (`Agreement`) | view | FindFile → ApplyStamp → WaitForSignatureFile (.sig, до 24 ч) → ReadSignatureFile → Verify → Save → MoveFile(Result) → DeleteFile(.sig) |
| B (`Invoice`, `Act`) | view | FindFile → RestSign → Verify → Save → MoveFile(Result) |
| C (`Letter`) | каталог | то же, что B |
| D (`SignedLetter`) | каталог, `RequireSignature=true` | FindFile → ReadSignatureFile → Verify → Save → MoveFile(Result) → DeleteFile(.sig) |

Процедуре сохранения подписи передаётся **ID файла из вью** (для каталогов — имя файла).

## Ограничения (осознанные)
- Нет истории по документам — только структурированные логи (route, docType, file, id в scope).
- Письмо на каждую неудачу: при недоступности SMB/REST это письмо на каждый документ на каждом опросе.
- Если сбой случится **после** записи подписи в БД (шаги MoveFile/DeleteFile), запись из view уже исчезла и документ повторно не подхватится — нужно разобрать вручную по письму.
- Имена файлов должны быть уникальны среди документов маршрута, находящихся в работе.
- Одна реплика; перезапуск пода безопасен (шаги идемпотентны).
