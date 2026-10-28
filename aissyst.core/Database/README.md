# Database and context templates

This document describes the storage contract and the user-facing format for preparing conversation contexts. The [SQLite implementation](../../aissyst.sqlite/README.md) provides these operations; the console session selection workflow is still pending.

## Storage model

The database is managed by the application. Its internal files are not intended for manual editing. External data enters the database in two ways:

- Importing a context template to create a context.
- Importing a previously exported session to create an independent session.

`IDatabase` hides the storage implementation. Context templates and session exports provide data portability without requiring users to access the underlying database.

## Creating a context template

A context template is a ZIP archive containing UTF-8 plain text files. Prepare a directory with the following structure, then archive its contents:

```text
preview.txt
agent_initial_instructions.txt
context/
    background.txt
    objectives.txt
    relevant_experience.txt
```

The ZIP must contain `preview.txt`, `agent_initial_instructions.txt`, and `context/` directly at its root. Do not place them inside an additional enclosing directory.

### 1. Write the preview

Create `preview.txt` at the root of the template. Describe the situation, the participants, and the purpose of the conversation. This is the short introduction supplied as `Session.preview`; detailed information belongs in thematic context blocks.

### 2. Write the initial agent instructions

Create `agent_initial_instructions.txt` at the root. These instructions are sent to the LLM before the `Session` payload. Define the assistant's role, the requested behavior, and the rules for interpreting the session and subsequent requests.

The instructions must respect the application's response protocol:

- Handle session initialization and the request types `summary`, `translate`, `advice`, `reply`, `t_advice`, and `t_reply`.
- Return a single JSON object matching `DialogResponse`, with `request_id`, `error`, `summary`, `summary_ts`, and `message`.
- Echo the incoming `request_id` unchanged, including for session initialization and error responses.
- Summarize the actual conversation. Suggested replies and other generated assistance are not statements made by either participant.
- Copy `summary_ts` from the last dialogue record included in the summary from the submitted batch; return `null` if the batch contains no records. Do not invent timestamps.
- Use Markdown only inside `message`, and only when `Session.md_format` allows it. The surrounding response remains JSON.

See the [example instructions](../../contexts/template/agent_initial_instructions.txt) and the [DTO definitions](../LLMConnector/DTO) when preparing or adapting this file. The instructions and examples must stay consistent with the current DTO contract.

### 3. Add thematic blocks

Create a `context` directory and place one `.txt` file directly inside it for each topic. Each file is parsed as follows:

- The first line is the topic title, mapped to `ContextRecord.Topic`.
- All text after the first line is the body, mapped to `ContextRecord.Body`. Internal line breaks are retained.

For example, `context/relevant_experience.txt` could contain:

```text
Relevant professional experience

I develop desktop applications using C# and .NET.
My recent work includes audio processing and asynchronous application design.
```

File names are arbitrary. Numeric prefixes are optional and have no semantic meaning. The title comes from the file's first line, not its name. Block order is not part of the template contract; do not rely on file names or ZIP entry order to establish priority or resolve conflicting information.

A block must have a nonempty title. Its body may be empty to represent a topic for which information is not yet available. Empty bodies must not be interpreted by the assistant as permission to invent facts.

Only `.txt` files directly inside `context/` are context blocks. Nested directories are not read. Additional root files, such as authoring notes or a list of unfinished topics, are ignored. They do not become part of the context or the initial instructions.

Plain text is sufficient; Markdown is not required for previews or context bodies. The template can use the language appropriate to the conversation and the assistant instructions.

### 4. Package and import

1. Check that both required root files are present and each context block has its title on the first line.
2. Create a ZIP archive from the directory's contents, preserving the layout shown above.
3. Supply the archive path to `CreateContextAsync`, optionally with a user-defined context name.

If the name is omitted or contains only whitespace, the archive's file name without its extension is used. The new context receives a unique database identifier. Context names do not serve as identifiers.

The archive is read and checked in full before a context is committed. A failed import must not leave a partially created context.

The repository contains an [unpacked example template](../../contexts/template). Its `EMPTY_BLOCKS.txt` file is an authoring aid and is ignored during import.

## Sessions

Creating a session copies the selected context's preview, initial agent instructions, and thematic blocks into the session. These copies are immutable for the lifetime of that session.

A session does not depend on the original context after creation. Changing or removing the source template does not alter existing sessions. Loading and exporting a session use only its own data.

Each session receives a unique identifier. Its default display name combines the context name with the UTC session creation timestamp, for example `Interview — 2026-10-28 15:30:42`. `SessionInfo` contains `Id`, `CreationTime`, and `Name`.

Dialogue is stored as the original transcribed fragments, each with its timestamp, source, and text. Chronological order is determined by timestamps. Combining consecutive fragments from the same source into conversation turns is a text-processing responsibility.

`AddSessionDialogRecordAsync` appends a fragment. `SetSessionDialogAsync` replaces the entire stored dialogue. Context copies and initial instructions are unaffected by either operation.

`GetSessionAsync` returns the session metadata, preview, thematic blocks, and dialogue as `SessionContext`. `GetSessionInitialInstructionsAsync` returns the initial instructions belonging to that same session for delivery before the LLM receives `Session`.

## Session export and import

A session export contains all data needed to recreate the session independently: its metadata, preview, initial instructions, thematic blocks, and dialogue. No reference to the original context is required.

Unlike context templates, exports are generated by the application and are not intended for manual editing. The SQLite implementation uses GZip-compressed JSON with `format_version: 1`; `.json.gz` is a suitable file extension. Only an exactly matching format version is accepted.

Import reads and deserializes the complete export before writing to the database. Required structure and version are checked before the session is committed. A failed import must not leave a partially created session.

An imported session receives a new identifier in the destination database. Exported identifiers are not reused as local identifiers. The imported session retains its own context snapshot and conversation history, even when the destination database has no corresponding context record.

## Database operations

| Method | Purpose |
| --- | --- |
| `GetContextsAsync` | List available contexts. |
| `CreateContextAsync` | Create a context from a template ZIP and an optional name. |
| `GetSessionsAsync` | List existing sessions. |
| `CreateSessionAsync` | Create a session with a private copy of the selected context. |
| `GetSessionAsync` | Load session metadata, preview, thematic blocks, and dialogue. |
| `GetSessionInitialInstructionsAsync` | Load the session's own initial agent instructions. |
| `AddSessionDialogRecordAsync` | Append a transcribed dialogue fragment. |
| `SetSessionDialogAsync` | Replace the complete session dialogue. |
| `ExportSessionAsync` | Export a self-contained session. |
| `ImportSessionAsync` | Import a session under a new local identifier. |
