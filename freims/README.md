# Учебная веб-служба бронирования переговорки

Служба моделирует процесс бронирования переговорки из нескольких шагов. Она хранит состояние в памяти, выдерживает повторную доставку событий по ключу идемпотентности, выполняет компенсацию при частичном сбое и показывает базовую наблюдаемость через журналы, health endpoints и метрики.

## Запуск

Из корня репозитория:

```powershell
dotnet run --project .\freims\freims\freims.csproj
```

Из директории `freims`:

```powershell
dotnet run --project .\freims\freims.csproj
```

После запуска служба печатает адрес в консоль, обычно это `http://localhost:5000` или другой свободный порт.

## Структура проекта

```text
freims/
  README.md
  freims.sln
  freims/
    freims.csproj
    Program.cs
    Models/
    Processes/
    Observability/
```

### `freims.sln`

Файл решения Visual Studio / .NET CLI. Через него удобно запускать сборку всего проекта:

```powershell
dotnet build .\freims\freims.sln
```

### `freims/freims.csproj`

Файл проекта ASP.NET Core. В нем указан `Microsoft.NET.Sdk.Web`, целевая платформа `net8.0`, включены nullable-типы и implicit usings.

### `freims/Program.cs`

Точка входа приложения. Здесь написано:

- настройка логирования с `IncludeScopes`, чтобы в журнал попадали `CorrelationId`, `ProcessKey` и `IdempotencyKey`;
- настройка JSON, чтобы enum-значения принимались и отдавались строками;
- регистрация сервисов в DI: `BookingProcessStore`, `MetricsStore`, `ReadinessState`;
- HTTP-маршруты службы.

Основные маршруты:

- `GET /` - краткая информация о службе и доступных endpoints;
- `POST /events` - прием события процесса;
- `GET /processes/{processKey}` - просмотр текущего состояния процесса;
- `GET /health/live` - проверка живости;
- `GET /health/ready` - проверка готовности;
- `POST /degradation/critical` - учебное включение/выключение критической деградации;
- `GET /metrics` - показатели в Prometheus-подобном текстовом формате.

## Директория `Models`

В `Models` лежат простые контракты данных: состояния, события, запросы и ответы.

### `BookingState.cs`

Enum состояний процесса:

- `New` - новый процесс;
- `ApplicationAccepted` - заявка принята;
- `ResourceBooked` - переговорка забронирована;
- `AccessGranted` - доступ выдан;
- `Completed` - процесс успешно завершен;
- `CompensationCompleted` - компенсация выполнена;
- `Error` - процесс завершился ошибкой.

### `BookingEventName.cs`

Enum событий, которые двигают машину состояний:

- `AcceptApplication`;
- `BookResource`;
- `GrantAccess`;
- `Complete`;
- `Fail`.

### `BookingEvent.cs`

Запрос клиента для `POST /events`.

Поля:

- `processKey` - ключ процесса, уникально определяет конкретное бронирование;
- `idempotencyKey` - ключ идемпотентности, уникально определяет событие внутри процесса;
- `eventName` - имя события;
- `correlationId` - сквозной идентификатор корреляции для журналов и ответа;
- `simulateFailure` - учебный флаг для имитации сбоя шага.

В этом же файле есть метод `Validate()`, который проверяет обязательные `processKey` и `idempotencyKey`.

### `EventResponse.cs`

Ответ на обработку события. В нем возвращается:

- `correlationId`;
- `processKey`;
- текущее `state`;
- признак успешности;
- признак повторной доставки;
- признак выполненной компенсации;
- текстовое сообщение.

### `ProcessSnapshot.cs`

Снимок процесса для `GET /processes/{processKey}`. Показывает текущее состояние, активна ли бронь, выдан ли доступ, последнюю ошибку и список уже обработанных ключей идемпотентности.

### `CriticalDegradationRequest.cs`

Запрос для `POST /degradation/critical`. Содержит одно поле `enabled`, которое включает или выключает критическую деградацию.

## Директория `Processes`

В `Processes` лежит доменная логика процесса бронирования: хранение состояния, применение событий, идемпотентность, переходы и компенсация.

### `BookingProcess.cs`

Объект одного процесса бронирования. Хранит:

- `ProcessKey`;
- текущее `State`;
- флаг `ReservationActive`;
- флаг `AccessIssued`;
- `LastError`;
- словарь `ProcessedEvents` с уже обработанными `idempotencyKey`.

Метод `ToSnapshot()` возвращает безопасный снимок процесса для API.

### `BookingProcessStore.cs`

Главное место, где реализована машина состояний.

Что делает `Apply()`:

1. Находит или создает процесс по `processKey`.
2. Проверяет `idempotencyKey`.
3. Если событие уже было обработано, возвращает сохраненный ответ и не меняет состояние.
4. Если событие новое, выполняет переход машины состояний.
5. Сохраняет результат обработки по ключу идемпотентности.
6. Пишет в журнал переход, повтор, ошибку или компенсацию.

Основные переходы:

- `New` + `AcceptApplication` -> `ApplicationAccepted`;
- `ApplicationAccepted` + `BookResource` -> `ResourceBooked`;
- `ResourceBooked` + `GrantAccess` -> `AccessGranted`;
- `AccessGranted` + `Complete` -> `Completed`.

Компенсация:

- если процесс находится в `ResourceBooked` и ломается следующий шаг `GrantAccess`, бронь отменяется;
- `ReservationActive` становится `false`;
- `AccessIssued` становится `false`;
- состояние становится `CompensationCompleted`;
- в журнал пишется запись о компенсации.

### `ApplyResult.cs`

Внутренний результат применения события. Нужен `Program.cs`, чтобы правильно сформировать HTTP-ответ и обновить метрики.

### `TransitionResult.cs`

Внутренний результат перехода машины состояний. Содержит HTTP-статус, успешность, признак компенсации и сообщение.

## Директория `Observability`

В `Observability` лежит все, что помогает понять состояние службы снаружи: метрики и готовность.

### `MetricsStore.cs`

Хранилище учебных метрик в памяти.

Считает:

- `booking_transition_success_total` - успешные переходы;
- `booking_transition_error_total` - ошибочные переходы;
- `booking_duplicate_delivery_total` - повторные доставки;
- `booking_compensation_total` - выполненные компенсации;
- `booking_step_latency_ms` - грубую оценку задержки по шагам, среднюю и максимальную.

Метод `ToPrometheusText()` формирует текст для `GET /metrics`.

### `ReadinessState.cs`

Хранит флаг `CriticalDegradation`. Если он включен, `GET /health/ready` возвращает `503`, а `GET /health/live` продолжает возвращать успешный ответ.

## Событие процесса

`POST /events`

```json
{
  "processKey": "booking-42",
  "idempotencyKey": "event-1",
  "eventName": "AcceptApplication",
  "correlationId": "corr-42"
}
```

Повторная доставка того же `idempotencyKey` в рамках одного `processKey` возвращает сохраненный результат и не меняет состояние.

## Пример успешного сценария

```powershell
$base = "http://localhost:5000"

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-42",
  "idempotencyKey": "event-1",
  "eventName": "AcceptApplication",
  "correlationId": "corr-42"
}'

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-42",
  "idempotencyKey": "event-2",
  "eventName": "BookResource",
  "correlationId": "corr-42"
}'

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-42",
  "idempotencyKey": "event-3",
  "eventName": "GrantAccess",
  "correlationId": "corr-42"
}'

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-42",
  "idempotencyKey": "event-4",
  "eventName": "Complete",
  "correlationId": "corr-42"
}'
```

## Пример компенсации

Сначала надо принять заявку и забронировать ресурс:

```powershell
$base = "http://localhost:5000"

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-fail",
  "idempotencyKey": "event-1",
  "eventName": "AcceptApplication",
  "correlationId": "corr-fail"
}'

Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-fail",
  "idempotencyKey": "event-2",
  "eventName": "BookResource",
  "correlationId": "corr-fail"
}'
```

Потом имитировать сбой выдачи доступа:

```powershell
Invoke-RestMethod "$base/events" -Method Post -ContentType "application/json" -Body '{
  "processKey": "booking-fail",
  "idempotencyKey": "event-3",
  "eventName": "GrantAccess",
  "correlationId": "corr-fail",
  "simulateFailure": true
}'
```

Ожидаемый результат: HTTP `500`, состояние процесса `CompensationCompleted`, бронь отменена.

Проверить состояние:

```powershell
Invoke-RestMethod "$base/processes/booking-fail"
```

## Health

- `GET /health/live` - проверка живости, успешна при работающем процессе службы;
- `GET /health/ready` - проверка готовности, возвращает `503` при критической деградации;
- `POST /degradation/critical` с телом `{"enabled": true}` или `{"enabled": false}` управляет критической деградацией для учебной демонстрации.

Пример:

```powershell
Invoke-RestMethod "$base/degradation/critical" -Method Post -ContentType "application/json" -Body '{"enabled": true}'
Invoke-RestMethod "$base/health/ready"
```

## Метрики

`GET /metrics` возвращает текст в Prometheus-подобном формате:

```text
booking_transition_success_total 3
booking_transition_error_total 1
booking_duplicate_delivery_total 1
booking_compensation_total 1
booking_step_latency_ms{step="GrantAccess",kind="avg"} 0.42
```

## Журналы

В журналы пишутся:

- успешные переходы;
- ошибочные переходы;
- повторные доставки;
- компенсации.

В каждой такой записи есть `CorrelationId`, `ProcessKey` и `IdempotencyKey`, поэтому можно связать клиентский ответ, конкретный процесс и записи журнала.
