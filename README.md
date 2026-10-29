# aissyst

aissyst is a desktop conversation assistant that captures speech, transcribes it locally, and provides help on demand using an LLM. Its purpose is to support live conversations with translations, suggested wording, and possible replies based on a context prepared by the user and the conversation so far.

The application is currently being developed for macOS, with Windows support planned. The current user interface is a console application.

## How it works

1. The user prepares a conversation context and selects a session and audio sources.
2. The application listens to the microphone and a selected application's audio. These sources represent the two participants in the conversation.
3. Audio is split into fragments when silence is detected, a duration limit is reached, or the user presses a hotkey. Listening continues after each fragment.
4. Whisper transcribes the fragments locally. Consecutive fragments from the same source are combined into conversation turns.
5. When the user requests assistance, the application supplies the LLM with the prepared context, a conversation summary, new dialogue records, and the requested type of help.
6. The response is displayed to the user, and the conversation summary is updated for subsequent requests.

Assistance is requested explicitly rather than automatically for every transcribed fragment. Supported request types are translation, a reply suggestion, a full reply, and suggestions or replies combined with translation.

## Audio and transcription

- Microphone and application audio are captured independently.
- Local transcription currently uses the English Whisper `small.en-q5_1` model.
- The model is downloaded when needed and is not stored in the repository.
- Bounded queues separate audio capture, transcription, and text processing. When a queue fills up, the oldest pending items are discarded with a log message.
- Hotkeys allow the user to finish the current audio fragments, stop listening, clear pending work, and request assistance.

Local transcription does not require sending audio to the LLM. Conversation text and the prepared context are used for assistance requests.

## Project structure

| Project | Responsibility |
| --- | --- |
| `aissyst.core` | Application orchestration, audio and transcription contracts, dialogue processing, session models, and storage contracts. |
| `aissyst.console` | Console interaction, configurable hotkeys, user output, and dependency injection setup. |
| `aissyst.macos` | macOS audio capture implementation. |
| `aissyst.windows` | Placeholder for Windows audio capture. |
| `chatgpt-connector` | ChatGPT authentication and the LLM connector implementation. |
| `aissyst.sqlite` | SQLite storage, context template import, and self-contained session export and import. |
| `aissyst.tests` | Automated tests for processing, orchestration, sessions, and console workflows. |

The solution uses .NET 10, Generic Host, dependency injection, and standard C# events for notifications.

`SessionModel` owns request snapshots, response validation, and the confirmed summary/continuation state. The connector returns parsed replies with opaque continuation leases and reports connection loss; it does not apply application state. `DialogCoordinator` serializes transcript processing and restoration and notifies the controller, which owns display updates.

## Database and context templates

See [Database operation and context template authoring](aissyst.core/Database/README.md) for instructions on preparing a context template and an overview of session storage, import, and export.

## Development status

The project is under active development. macOS audio capture, local transcription, dialogue processing, and assistance request orchestration are implemented.

Sign in with ChatGPT has been implemented and verified. The [ChatGPT connector](chatgpt-connector/README.md) now implements Responses API WebSocket initialization, account-specific model selection, JSON requests and responses, cancellation, token renewal, and connection recovery. LLM requests run one at a time; busy requests are rejected immediately, and emergency cancellation closes the socket and triggers recovery. A minimal real-account WebSocket request succeeded without `stream_id`; the complete console inference workflow still needs live verification.

SQLite storage is implemented in [aissyst.sqlite](aissyst.sqlite/README.md) and registered in the console application. At startup, select an existing session or choose `Create New`, then select a context or `Create from Template`. After selecting a session, choose `(o)open`, `(n)return to list`, or `(d)delete`. After selecting a context, choose `(c)create new session`, `(n)return to list`, or `(d)delete`. Deletion requires typing the exact word `delete`; any other response returns to the refreshed list. Deleting a context preserves existing sessions; deleting a session removes its dialogue. Empty lists advance directly to the next step. Importing a template immediately creates and selects a session. Invalid menu numbers are retried; template or database failures end the application with a nonzero exit code. Closing standard input or pressing Ctrl+C stops startup.

The database is stored at `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aissyst", "aissyst.sqlite")`, independent of the working directory. Template paths may contain spaces and may be enclosed in single or double quotes. The context name defaults to the archive filename. If session creation fails after a successful context import, the imported context remains available on the next launch.

Session selection happens before ChatGPT authorization, model selection, and Whisper initialization. Each original, unmerged transcription fragment enters an in-memory unsaved buffer and the text processor history. Every new fragment triggers an atomic attempt to save the entire buffer to SQLite. A database error is logged and leaves the buffer intact for the next attempt. On reconnection, the processor reloads the selected session from SQLite and completely replaces its history with the database snapshot plus unsaved fragments before a fresh Session is sent to the LLM. Saving and reloading are serialized so concurrent fragments are not lost or duplicated. At startup and during recovery, the controller clears the chat display and prints the full history, including the pending turn, through `IOutput.PrintMultipleDialogMessages`. History replacement and live output are synchronized. Console clearing is skipped when stdout is redirected. Graceful completion waits for pending processing; processor disposal also makes a final save attempt. Unsaved fragments can still be lost on an application crash.

## Working with a separate Codex account

Use `start-codex.sh` to work on this project with a ChatGPT account separate from the default Codex account. Sign in once, choosing the intended account in the browser:

```sh
./start-codex.sh login
./start-codex.sh login status
```

Then start a project session:

```sh
./start-codex.sh
```

The launcher works from any directory, selects this repository as the working directory, and forwards arguments to Codex. For a first session that can inspect files without editing them, use `./start-codex.sh --sandbox read-only`.

Configuration is shared in `.codex-account/config.toml`. Credentials, sessions, logs, and other runtime state stay in `.codex-account/` and are ignored by Git. The launcher uses file-based ChatGPT authentication, a separate state directory, and no shared daemon. Inherited API credentials are removed only from the launched process. It does not change the parent shell or the default account used by ordinary `codex` launches.

On another machine, clone the repository and run the login command again. Existing default-account sessions are not copied. To continue this project's conversation, ask Codex to read `AGENTS.md`, `PROJECT_CONTEXT.md`, and `CHAT_HANDOFF.txt` if present.
