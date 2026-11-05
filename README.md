# DocRouter — сервис-«курьер» для документов

Один сервис (ASP.NET Core, 1 реплика в k8s), который «водит» файлы по шагам. **Без БД процессов**: состояние документа = где лежит файл (папки) + есть ли запись во view. При сбое шаги проходятся заново на следующем опросе, письмо уходит на каждую неудачу.

> Каркас написан без компиляции (в среде не было .NET SDK). Первый `dotnet build` может показать мелкие правки (версии пакетов, API SMBLibrary).

## Быстрый старт
```sh
./create-solution.sh && dotnet build && dotnet test
dotnet run --project src/DocRouter.Host
```
k8s: `replicas: 1`, `strategy: Recreate`; probes: `/health/live`, `/health/ready` (цикл опроса жив), `/health/deps` — только диагностика.

## Что где настраивается
**Типы документов — в коде** (`src/DocRouter.Routes/DocumentDefinitions.cs`): откуда приходят, кому писать об ошибках, какие шаги проходят. Рабочие папки передаются в шаги значениями:

```csharp
b.Type("DEPO_AGR")
    .FromDirectory(dirs.Signed, mask: "DEPO_AGR_*", withSignature: true)   // источник
    .NotifyTo(mail.Depo)                                                    // группа адресатов ошибок
    .Step<FindFile>(s => s.In = [dirs.Signed])
    .Step<ReadSignatureFile>().Step<VerifySignature>().Step<SaveSignature>()
    .Step<MoveFile>(s => s.To = dirs.Docs)
    .Step<DeleteFile>();
```
Источники: `FromView("VW", code: "...")` (по умолчанию колонки DOC_ID / FILE_NAME / DOC_TYPE, код = имя типа; вью может содержать другие коды — они молча пропускаются) и `FromDirectory(папка, mask, withSignature)`.

**Окружение — только плоские переменные** (`deploy/env.example`):
- `Dirs__*` — рабочие папки (свойства `DirectoriesOptions`; новая папка = новое свойство + переменная; пути UNC пишутся как есть);
- `Mail__*` — группы адресатов (`Mail__Brokers=a@x.ru;b@x.ru`; свойства `MailOptions`; `Mail__Default` получает ВСЕ письма);
- `Smb__*`, `SignatureService__*`, `Oracle__*`, `Smtp__*`, `Courier__PollInterval`.

Штампы (если нужны) — тоже в коде: `Infrastructure/StampCatalog.cs`, PNG-шаблоны в `Dirs__StampTemplates`.

## Как это работает
Каждые `Courier:PollInterval` (5-15 мин):
1. **Источники** возвращают документы «в работе»: записи вью и файлы в папках-источниках.
2. Для каждого документа **шаги идут с первого**. Шаги идемпотентны: `FindFile` определяет, в какой из папок `In` файл лежит сейчас (порядок — от самой продвинутой стадии к начальной), а `ApplyStamp`/`WaitForSignatureFile` пропускают уже сделанное.
3. `Next` → дальше; `Wait` → тихо до следующего опроса; `Fail`/исключение → лог + письмо (группа типа + `Mail__Default`), повтор на следующем опросе.

## Проверки на старте (сервис не запустится)
Не задана `Dirs__*` или `Mail__*`; тип без источника или без шагов; тип описан дважды; вью+код заявлены двумя типами; папку читают несколько типов, и у кого-то нет `mask`.
В рантайме: файл, подходящий под маски двух типов, не обрабатывается — системное письмо.

## Примеры типов (в `DocumentDefinitions.cs`)
| Тип | Источник | Шаги |
|---|---|---|
| `BROK_AGR` | вью `VW_DOCS` | FindFile(Docs) → RestSign → Verify → Save (файл остаётся в Docs) |
| `DEPO_AGR` | папка Signed, `DEPO_AGR_*` + `.sig` | FindFile(Signed) → ReadSignature → Verify → Save → MoveFile(Docs) → DeleteFile(.sig) |
| `DEPO_REP`, `BROK_REP` | вью `VW_REPORTS` | FindFile(своя папка) → RestSign → Verify → Save → MoveFile(Reports) |

Шаги: `FindFile`, `ApplyStamp`, `WaitForSignatureFile`, `ReadSignatureFile`, `RestSign`, `VerifySignature`, `SaveSignature`, `MoveFile`, `DeleteFile`. Процедуре сохранения подписи передаётся **ID файла из вью** (для папок — имя файла).

## Ограничения (осознанные)
- Нет истории по документам — только структурированные логи (docType, file, id в scope).
- Письмо на каждую неудачу: при недоступности SMB/REST это письмо на каждый документ на каждом опросе.
- Если сбой случится **после** записи подписи в БД, а запись из view уже исчезла (например, шаг MoveFile у отчётов), документ повторно не подхватится — разбор вручную по письму. У типов из каталога (DEPO_AGR) такого нет: триггер — сам файл.
- Имена файлов должны быть уникальны среди документов, находящихся в работе и использующих одни и те же папки (документ ищется по имени).
- Одна реплика; перезапуск пода безопасен (шаги идемпотентны).
