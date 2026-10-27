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
| `aissyst.tests` | Automated tests for processing, orchestration, sessions, and console workflows. |

The solution uses .NET 10, Generic Host, dependency injection, and R3 for event-based coordination.

## Database and context templates

See [Database operation and context template authoring](aissyst.core/Database/README.md) for instructions on preparing a context template and an overview of session storage, import, and export.

## Development status

The project is under active development. macOS audio capture, local transcription, dialogue processing, and assistance request orchestration are implemented.

Sign in with ChatGPT has been implemented and verified. Actual LLM session initialization and inference requests are not yet implemented. The planned interaction uses a WebSocket connection.

Production database storage and session selection are also pending. The current console configuration therefore requires further implementation before the complete workflow can run.
