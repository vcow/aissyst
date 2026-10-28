# SQLite storage

`SqliteDatabase` implements the complete [`IDatabase`](../aissyst.core/Database/IDatabase.cs) contract using Microsoft.Data.Sqlite. This .NET 10 project depends on `aissyst.core` and keeps SQLite dependencies outside the core library.

For user-authored template rules, see [Database and context templates](../aissyst.core/Database/README.md).

## Usage

Reference `aissyst.sqlite` from the application that constructs the database, then supply a persistent file path:

```csharp
using aissyst.core.Database;
using aissyst.sqlite;

var path = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "aissyst", "aissyst.sqlite");
IDatabase database = new SqliteDatabase(path);

var context = await database.CreateContextAsync("interview.zip", "Interview");
var session = await database.CreateSessionAsync(context.Id);
var data = await database.GetSessionAsync(session.Id);
```

The constructor does not open or create files. The directory, database, and schema are created on the first database operation. An in-memory database is not supported. Console registration and session selection are left to the application's user interaction workflow.

## Schema and consistency

- `contexts`: ID, name, initial agent instructions, and preview.
- `context_blocks`: ID, context foreign key, topic, and body.
- `sessions`: ID, name, UTC creation time, initial instructions, preview, and context serialized as a JSON array of topic/body objects.
- `dialog_records`: ID, session foreign key, timestamp, role, and message.

Session creation copies all context data in a transaction. Sessions have no foreign key to their source context. Changing or removing a context cannot change a previously created session.

Foreign keys are enabled on every connection. Indexes support context block lookup and session dialogue retrieval. Each dialogue fragment has an independent integer ID; timestamps are not unique keys. Records are read in timestamp order, with the internal ID breaking ties without discarding records.

Timestamps are stored as UTC .NET ticks, preserving 100-nanosecond precision. Local timestamps are converted to UTC; `DateTimeKind.Unspecified` timestamps are treated as UTC. Loaded timestamps have `DateTimeKind.Utc`.

Audio source mapping is `Microphone` to `interviewee`, `Application` to `interviewer`, and `None` to `unknown`. A combined source is rejected for a single dialogue record.

Create, append, replace, delete, and import operations are transactional. `DeleteContextAsync` cascades to context blocks while preserving existing sessions. `DeleteSessionAsync` cascades to dialogue records. Missing context or session IDs produce `KeyNotFoundException`. Invalid templates and exports produce `InvalidDataException`; malformed UTF-8 produces a decoding error. Storage failures propagate to the caller.

Operations on one instance are serialized. Separate instances use SQLite transactions and locking. WAL is enabled, and each operation opens and closes its own connection. Since Microsoft.Data.Sqlite performs database I/O synchronously, operations run on a thread-pool thread. Cancellation is checked before work, between records, and before commit; it does not interrupt an SQL statement already executing or undo a committed transaction.

The database schema version is stored in `PRAGMA user_version`. Version 1 is supported; other nonzero versions are rejected. No migrations from older application schemas are provided yet.

## Import and export

Context creation reads the template ZIP directly, without extracting files. The complete template is parsed before a transaction inserts it. Block names do not establish semantic order.

Session exports are GZip-compressed UTF-8 JSON with these fields:

- `format_version`: currently `1`.
- `name` and `creation_time`: session metadata, retained during import.
- `instructions` and `preview`: session-local copies.
- `context`: an array of `topic`/`body` objects.
- `dialog`: an array of `timestamp`/`role`/`message` objects.

Local database IDs are omitted. Import assigns a new destination-local ID; its numeric value can coincide with the source ID in a different database. No source context needs to exist in the destination.

Export reads a consistent database snapshot, writes a temporary file beside the destination, and replaces the destination only after writing finishes. The destination directory must exist. Import deserializes and checks the version and required values before starting the write transaction. Invalid files or failed inserts cannot leave a partially imported session.

Session exports are distinct from user-authored template ZIPs; the two formats are not interchangeable.
