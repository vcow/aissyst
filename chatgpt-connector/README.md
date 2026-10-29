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

`SessionModel` obtains the selected session's initial instructions from the database and passes them directly to `ILLMConnector.InitializeSession`. Session data remains in the existing application DTOs; storage entities are not serialized directly to the server. There is no session lifecycle callback interface.

`ILLMConnector` returns `LLMResponse`: the parsed `DialogResponse` plus an opaque `ILLMContinuation`. A continuation identifies its socket and provider response ID and supports independent `Retain`/`Dispose` handles. It does not pin server-side cached state. These are transport resources, not application DTO fields. The connector neither applies summaries nor decides which request is current.

`SessionModel.SendRequestAsync` owns the entire request operation. It creates a snapshot, retains the confirmed continuation, awaits the connector outside the state lock, validates the returned payload against the submitted snapshot, and under one short lock replaces the immutable confirmed state: request ID, summary, summary timestamp, and continuation. It disposes rejected continuations. Errors leave the previous state intact; stale replies return null. The controller only displays accepted answers and preserves their display order if task continuations are scheduled out of order.

On a new connection, the connector sends:

1. A `response.create` event with the initial instructions in a `developer` message and `generate: false`. It waits for successful `response.completed` and an API response ID, without expecting model output.
2. A generated response containing the serialized `Session` as a `user` message, chained from the warmup ID.
3. Later, serialized `DialogRequest` messages chained from the last successfully accepted API response.

Every request uses `store: false`. No request includes `stream_id`; the connector processes one request at a time on the default WebSocket lane. Generated requests use a strict JSON schema with `request_id`, `error`, `summary`, nullable `summary_ts`, and `message`. `SummaryTimestamp` remains `DateTime?` in application code. No separate application revision counter is added.

One reader consumes complete WebSocket text messages, including fragmented messages. It handles API events separately from the JSON inside model output. Partial deltas are not parsed as completed answers. Refusals, failed/incomplete responses, schema violations, incorrect request IDs, and invalid summary boundaries cannot commit state. The server-issued response ID needed for `previous_response_id` is encapsulated in `ILLMContinuation` and remains distinct from the application's `request_id`.

Server errors are logged with the model, event type, response ID, HTTP status, error code/type, parameter, server request ID, and diagnostic message when available. `ResponsesApiException` also preserves the server's explanation in its message, including errors without a code, `detail` payloads, failed responses, and incomplete-response reasons. Connection interruption logs include the exception. Diagnostic logging does not dump outgoing instructions, session context, or authorization headers.

## Cancellation and recovery

`ILLMConnector.IsBusy` reports an active transport operation or cancellation cleanup. Session initialization holds it across warmup and the Session exchange. Assistance requests fail immediately with `InvalidOperationException` when busy; they are not queued and do not cancel the current request. The connector atomically reserves its active operation, so checking `IsBusy` is optional for correctness but useful to callers. `SessionModel` checks the flag and also prevents a second model request while the preceding reply is being validated and committed. Audio capture, transcription, and the UI remain responsive while awaiting the LLM.

`CancelAsync` is an emergency stop. While busy it cancels the operation, closes the WebSocket, waits for the operation and reader to finish, and raises `ConnectionLost` to start normal session recovery. Cancellation calls arriving while cancellation is in progress return without waiting or affecting a subsequent operation. An idle cancellation is a no-op. A response timeout or caller cancellation also closes an unfinished generation rather than leaving server-side work running after `IsBusy` resets. The existing `ResponseTimeout` remains two minutes by default.

Only successful, validated responses whose `request_id` exceeds the last accepted ID can update summary or UI. A request retains its submitted summary boundary until completion. Request errors, refusals, and invalid output leave confirmed application state intact. The server may discard a previous-response cache entry after a failed continuation; `previous_response_not_found` triggers recovery if that parent is reused.

Disconnects, timeouts, emergency cancellation, and unusable server chains trigger recovery. The interrupted assistance request fails and is **not** resubmitted. The user can request assistance again after the recovery log confirms readiness. Requests made during recovery fail without sending stale input.

On actual socket loss, the connector raises `ConnectionLost` and fails affected wire operations. `SessionModel` marks the session unavailable and runs bounded recovery: reconnect, reload the dialog, and initialize a fresh Session with the initial instructions. The connector has no callback into the model and does not retry application requests. The reconstructed summary and continuation replace confirmed state together. Connection identity and request ID prevent old replies from overwriting restored state.

`DialogCoordinator` serializes live processing and history replacement, including synchronous notifications. The controller handles `MessageCompleted` by printing one turn and `HistoryReplaced` by clearing the display and printing the complete history, including a pending turn. The model reloads history through this coordinator; it never calls an output component or a controller-provided restoration callback. Fragments arriving after the initialization snapshot remain raw for subsequent requests.

**Transcript storage:** `TextProcessor.ProcessAsync` always adds each original fragment to its unsaved buffer and in-memory history, then attempts one atomic batch append through `AddSessionMultipleDialogRecordsAsync`. Successful commits clear the buffer; failed commits are logged and retain all fragments for the next save. `IReloadableTextProcessor.ReloadAsync` fully replaces history with a sorted database snapshot plus the remaining unsaved fragments, without deduplicating by time or text. Saves and reloads share one gate. Failed reads leave the previous history intact. Disposal attempts a final flush; an application crash can lose the unsaved buffer. Summaries remain in memory and are reconstructed after reconnection. The optional file-only text processor retains its separate storage workflow.

There is no timer-driven reconnection. SessionModel starts recovery after the connector reports an actual disconnect or a transport/chain failure. Access-token expiry is checked before each connection attempt. Refresh uses the existing OAuth refresh flow and saves rotated credentials in the existing store. Authorization failures stop recovery instead of repeatedly reconnecting with rejected credentials. Other reconnection attempts are bounded and logged; exhausted recovery requires restarting the application.

## Options and validation

`ChatGPTConnectorOptions` defaults:

| Option | Default |
| --- | --- |
| `ConnectionTimeout` | 30 seconds |
| `ResponseTimeout` | 2 minutes |


`SessionRecoveryOptions` belongs to the session model: `Delay` defaults to 2 seconds and `Attempts` to 3. It is registered separately in DI.

Automated tests use an injected socket and synthetic data. They cover initialization, model selection, response chaining, semantic validation, API failures, refusals, cancellation, timeout, idle and in-flight disconnects, access-token retrieval on reconnection, recovery summaries, and disposal. A minimal real-account check on GPT-6.1 Sol returned HTTP 400 for `stream_id` and completed with `OK` when that field was omitted. Full application compatibility with warmup, continuation, Structured Outputs, and cancellation still requires a live console run. A real-account check requires choosing an available model and running the console workflow.

Protocol references: [WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode), [Sign in with ChatGPT models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), and [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
