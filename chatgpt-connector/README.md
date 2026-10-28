# ChatGPT connector

Подключение использует официальный **Sign in with ChatGPT** для open-source приложений:
https://developers.openai.com/siwc/token-sharing-open-source/sign-in

При первом запуске консольного приложения коннектор открывает системный браузер.
Войдите в нужный аккаунт ChatGPT и подтвердите доступ для `aissyst`.
Локальный callback слушает только `127.0.0.1` на свободном порту; время ожидания по умолчанию — пять минут.
После проверки подписи, issuer, audience, срока действия и nonce коннектор выводит подключённый аккаунт.
Отказ или ошибка проверки прерывают запуск приложения.

Для использования плана ChatGPT документация указывает Plus или Pro и отдельное разрешение
`chatgpt.tokens.use.direct`. Успешная авторизация и разрешение использовать план отражаются отдельно
в `ChatGPTConnector.Account`. Этот вход не даёт доступа к существующим перепискам ChatGPT.
Запросы к модели реализованы через Responses API WebSocket. Инициализация LLM-сессии отправляет контекст модели после выбора сессии и модели.

Сессия сохраняется в `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
"aissyst", "chatgpt")`, вне проекта. На macOS это обычно `~/.config/aissyst/chatgpt`.
На Unix каталог имеет права `0700`, файлы — `0600`; токены в этих файлах не зашифрованы.
Не публикуйте содержимое каталога. На Windows используется защита каталога профиля пользователя.
OAuth-токены и URL с `id_token_hint` не выводятся в журнал.

Следующий запуск использует сохранённую сессию. Если access token близок к истечению,
коннектор обновляет его, атомарно сохраняя заменённый refresh token.
При отозванной или истёкшей renewable session снова открывается браузер с сохранённым client ID.
При временной ошибке сети запуск завершается с ошибкой, без регистрации нового клиента.
Блокировка файла сессии предотвращает параллельное обновление токенов двумя экземплярами приложения.

`ChatGPTConnectorOptions` настраивает имя приложения, каталог и таймаут входа через DI.
Пока поддерживается одна сохранённая регистрация на каталог; выбор нескольких аккаунтов и выход
с отзывом сессии не реализованы. Отозвать доступ приложения можно в настройках ChatGPT.
Обновление токенов выполняется при инициализации и переподключении; срок соединения ограничивается также сроком действия access token.

Использование плана через этот способ входа находится в preview и имеет отдельные ограничения:
https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations

`ChatGPTConnector` uses the existing Sign in with ChatGPT OAuth flow and sends text through the Responses API WebSocket at `wss://api.openai.com/v1/responses`. Credentials remain in the existing private file store. No API key, Keychain migration, or HTTP inference fallback is introduced.

## Startup and model selection

After authorization, the connector fetches the account's model catalog from `GET https://api.openai.com/v1/models`. Entries with `visibility: "list"` are passed, in server order, to `IModelSelector`. The console implementation displays `display_name` and returns the selected `slug` for inference. There is no hardcoded model default.

`SessionModel` obtains the selected session's initial instructions from the database and configures `ILLMSessionLifecycle`. Session data remains in the existing application DTOs; storage entities are not serialized directly to the server.

On a new connection, the connector sends:

1. A `response.create` event with the initial instructions in a `developer` message and `generate: false`. It waits for successful `response.completed` and an API response ID, without expecting model output.
2. A generated response containing the serialized `Session` as a `user` message, chained from the warmup ID.
3. Later, serialized `DialogRequest` messages chained from the last successfully accepted API response.

Every request uses `store: false` and the named stream `interview`. Generated requests use a strict JSON schema with `request_id`, `error`, `summary`, nullable `summary_ts`, and `message`. `SummaryTimestamp` remains `DateTime?` in application code. No separate application revision counter is added.

One reader consumes complete WebSocket text messages, including fragmented messages. It handles API events separately from the JSON inside model output. Partial deltas are not parsed as completed answers. Refusals, failed/incomplete responses, schema violations, incorrect request IDs, and invalid summary boundaries cannot commit state. `confirmedResponseId` is the server-issued response ID needed for `previous_response_id`; it is distinct from the application's `request_id`.

## Cancellation and recovery

Requests are serialized on one stream. Cancellation promptly ends the caller's wait; it does not claim to cancel generation on the server. An already-sent response is drained, but its canceled or obsolete result is not applied. A fresh chain is restored when needed to avoid depending on an evicted, unaccepted response. Requests are not forked into parallel streams.

Disconnects, response timeouts, and unusable server chains trigger recovery. The interrupted assistance request fails and is **not** resubmitted. The user can request assistance again after the recovery log confirms readiness. Requests made during recovery fail without sending stale input.

Recovery opens a new authenticated socket, repeats warmup, obtains a fresh `Session` through `SessionModel.GetSession()`, and initializes that session. The reconstructed summary replaces the old in-memory summary. Requests prepared before recovery cannot overwrite it. Real fragments arriving during recovery remain in the text processor and are included in later requests when they lie beyond the accepted snapshot's timestamp.

**Current storage boundary:** live transcript persistence and reloading the text processor from the database are not implemented by this change. Recovery uses the complete current text processor history, which includes the database history loaded at startup and new in-memory fragments. Persistence must be connected before database-only recovery or recovery across application restarts can include new fragments. Summaries are not persisted, compared across connections, or used to prune transcript history.

Connection renewal is scheduled before the connection lifetime limit or access-token expiry, whichever comes first. Refresh uses the existing OAuth refresh flow and saves rotated credentials in the existing store. Authorization failures stop recovery instead of repeatedly reconnecting with rejected credentials. Other reconnection attempts are bounded and logged; exhausted recovery requires restarting the application.

## Options and validation

`ChatGPTConnectorOptions` defaults:

| Option | Default |
| --- | --- |
| `ConnectionTimeout` | 30 seconds |
| `ResponseTimeout` | 2 minutes |
| `ConnectionLifetime` | 50 minutes |
| `ReconnectDelay` | 2 seconds |
| `ReconnectAttempts` | 3 |

Automated tests use an injected socket and synthetic data. They cover initialization, model selection, response chaining, semantic validation, API failures, refusals, cancellation, timeout, idle and in-flight disconnects, lifetime renewal, recovery summaries, and disposal. They do not establish real-account model compatibility with warmup or Structured Outputs. A real-account check requires choosing an available model and running the console workflow.

Protocol references: [WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode), [Sign in with ChatGPT models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), and [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
