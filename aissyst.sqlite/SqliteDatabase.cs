using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.Processor;
using Microsoft.Data.Sqlite;

namespace aissyst.sqlite;

public sealed class SqliteDatabase : IDatabase
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true
	};
	private readonly string _databasePath;
	private readonly string _connectionString;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private bool _initialized;

	public SqliteDatabase(string databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		if (databasePath == ":memory:")
		{
			throw new ArgumentException("A persistent database file path is required.", nameof(databasePath));
		}
		_databasePath = Path.GetFullPath(databasePath);
		_connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = _databasePath,
			ForeignKeys = true,
			Pooling = false
		}.ToString();
	}

	public Task<IReadOnlyList<ContextInfo>> GetContextsAsync(CancellationToken cancellationToken = default) =>
		RunAsync<IReadOnlyList<ContextInfo>>((connection, transaction) =>
		{
			using var command = Command(connection, transaction, "SELECT id, name FROM contexts ORDER BY id");
			using var reader = command.ExecuteReader();
			var result = new List<ContextInfo>();
			while (reader.Read())
			{
				cancellationToken.ThrowIfCancellationRequested();
				result.Add(new ContextInfo(reader.GetInt32(0), reader.GetString(1)));
			}
			return result.AsReadOnly();
		}, false, cancellationToken);

	public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
		RunAsync<IReadOnlyList<SessionInfo>>((connection, transaction) =>
		{
			using var command = Command(connection, transaction, "SELECT id, creation_time, name FROM sessions ORDER BY creation_time, id");
			using var reader = command.ExecuteReader();
			var result = new List<SessionInfo>();
			while (reader.Read())
			{
				cancellationToken.ThrowIfCancellationRequested();
				result.Add(new SessionInfo(reader.GetInt32(0), UtcDate(reader.GetInt64(1)), reader.GetString(2)));
			}
			return result.AsReadOnly();
		}, false, cancellationToken);

	public async Task<ContextInfo> CreateContextAsync(string contextTemplatePath, string? contextName = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contextTemplatePath);
		var template = await Task.Run(() => ContextTemplate.Read(contextTemplatePath, cancellationToken), cancellationToken);
		var name = string.IsNullOrWhiteSpace(contextName) ? Path.GetFileNameWithoutExtension(contextTemplatePath) : contextName.Trim();
		return await RunAsync((connection, transaction) =>
		{
			using var command = Command(connection, transaction, """
				INSERT INTO contexts(name, agent_initial_instructions, preview) VALUES($name, $instructions, $preview);
				SELECT last_insert_rowid();
				""", ("$name", name), ("$instructions", template.Instructions), ("$preview", template.Preview));
			var id = checked((int)(long)command.ExecuteScalar()!);
			foreach (var block in template.Context)
			{
				cancellationToken.ThrowIfCancellationRequested();
				using var insert = Command(connection, transaction,
					"INSERT INTO context_blocks(context_id, topic, body) VALUES($id, $topic, $body)",
					("$id", id), ("$topic", block.Topic), ("$body", block.Body));
				insert.ExecuteNonQuery();
			}
			return new ContextInfo(id, name);
		}, true, cancellationToken);
	}

	public Task<SessionInfo> CreateSessionAsync(int contextId, CancellationToken cancellationToken = default) =>
		RunAsync((connection, transaction) =>
		{
			string name;
			string instructions;
			string preview;
			using (var command = Command(connection, transaction,
				"SELECT name, agent_initial_instructions, preview FROM contexts WHERE id = $id", ("$id", contextId)))
			using (var reader = command.ExecuteReader())
			{
				if (!reader.Read())
				{
					throw new KeyNotFoundException($"Context {contextId} does not exist.");
				}
				name = reader.GetString(0);
				instructions = reader.GetString(1);
				preview = reader.GetString(2);
			}
			var blocks = new List<ContextRecord>();
			using (var command = Command(connection, transaction,
				"SELECT topic, body FROM context_blocks WHERE context_id = $id ORDER BY id", ("$id", contextId)))
			using (var reader = command.ExecuteReader())
			{
				while (reader.Read())
				{
					cancellationToken.ThrowIfCancellationRequested();
					blocks.Add(new ContextRecord(reader.GetString(0), reader.GetString(1)));
				}
			}
			var now = DateTime.UtcNow;
			return InsertSession(connection, transaction, new SessionDocument
			{
				FormatVersion = 1,
				Name = name + " — " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
				CreationTime = now, Instructions = instructions, Preview = preview,
				Context = blocks.ToArray(), Dialog = []
			}, cancellationToken);
		}, true, cancellationToken);

	public Task<SessionContext> GetSessionAsync(int sessionId, CancellationToken cancellationToken = default) =>
		RunAsync((connection, transaction) =>
		{
			var document = ReadSession(connection, transaction, sessionId, cancellationToken);
			return new SessionContext(new SessionInfo(sessionId, document.CreationTime, document.Name),
				document.Preview, Array.AsReadOnly(document.Context), Array.AsReadOnly(document.Dialog));
		}, false, cancellationToken);

	public Task<string> GetSessionInitialInstructionsAsync(int sessionId, CancellationToken cancellationToken = default) =>
		RunAsync((connection, transaction) =>
		{
			using var command = Command(connection, transaction,
				"SELECT agent_initial_instructions FROM sessions WHERE id = $id", ("$id", sessionId));
			return command.ExecuteScalar() as string ?? throw MissingSession(sessionId);
		}, false, cancellationToken);

	public Task AddSessionDialogRecordAsync(int sessionId, DialogMessage message, CancellationToken cancellationToken = default)
	{
		var record = ToRecord(message);
		return RunAsync((connection, transaction) =>
		{
			RequireSession(connection, transaction, sessionId);
			InsertRecord(connection, transaction, sessionId, record);
			return true;
		}, true, cancellationToken);
	}

	public Task SetSessionDialogAsync(int sessionId, IReadOnlyList<DialogMessage> dialog, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(dialog);
		var records = dialog.Select(ToRecord).ToArray();
		return RunAsync((connection, transaction) =>
		{
			RequireSession(connection, transaction, sessionId);
			using var delete = Command(connection, transaction, "DELETE FROM dialog_records WHERE session_id = $id", ("$id", sessionId));
			delete.ExecuteNonQuery();
			foreach (var record in records)
			{
				cancellationToken.ThrowIfCancellationRequested();
				InsertRecord(connection, transaction, sessionId, record);
			}
			return true;
		}, true, cancellationToken);
	}

	public async Task ExportSessionAsync(int sessionId, string filePath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		var fullPath = Path.GetFullPath(filePath);
		if (fullPath == _databasePath || fullPath == _databasePath + "-wal" || fullPath == _databasePath + "-shm")
		{
			throw new ArgumentException("The export must not overwrite the database files.", nameof(filePath));
		}
		var document = await RunAsync((connection, transaction) => ReadSession(connection, transaction, sessionId, cancellationToken),
			false, cancellationToken);
		var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
			await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
			{
				await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken);
			}
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, fullPath, overwrite: true);
		}
		finally
		{
			File.Delete(temporaryPath);
		}
	}

	public async Task<SessionInfo> ImportSessionAsync(string filePath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		await using var file = File.OpenRead(filePath);
		await using var gzip = new GZipStream(file, CompressionMode.Decompress);
		SessionDocument document;
		try
		{
			document = await JsonSerializer.DeserializeAsync<SessionDocument>(gzip, JsonOptions, cancellationToken)
				?? throw new InvalidDataException("The session export must contain a JSON object.");
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("The session export does not match the required JSON structure.", exception);
		}
		document.Validate();
		return await RunAsync((connection, transaction) => InsertSession(connection, transaction, document, cancellationToken),
			true, cancellationToken);
	}

	private async Task<T> RunAsync<T>(Func<SqliteConnection, SqliteTransaction, T> operation, bool write,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			// Microsoft.Data.Sqlite executes database I/O synchronously. Keep it off the calling thread.
			return await Task.Run(() =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
				using var connection = new SqliteConnection(_connectionString);
				connection.Open();
				EnsureSchema(connection);
				using var transaction = connection.BeginTransaction(deferred: !write);
				var result = operation(connection, transaction);
				cancellationToken.ThrowIfCancellationRequested();
				transaction.Commit();
				return result;
			}, cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	private void EnsureSchema(SqliteConnection connection)
	{
		if (_initialized)
		{
			return;
		}
		using (var wal = connection.CreateCommand())
		{
			wal.CommandText = "PRAGMA journal_mode=WAL";
			wal.ExecuteNonQuery();
		}
		using var transaction = connection.BeginTransaction();
		using var version = Command(connection, transaction, "PRAGMA user_version");
		var currentVersion = (long)version.ExecuteScalar()!;
		if (currentVersion != 0 && currentVersion != 1)
		{
			throw new InvalidDataException($"Unsupported database schema version: {currentVersion}.");
		}
		if (currentVersion == 0)
		{
			using var schema = Command(connection, transaction, """
				CREATE TABLE contexts (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    name TEXT NOT NULL,
				    agent_initial_instructions TEXT NOT NULL,
				    preview TEXT NOT NULL
				);
				CREATE TABLE context_blocks (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    context_id INTEGER NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
				    topic TEXT NOT NULL,
				    body TEXT NOT NULL
				);
				CREATE INDEX ix_context_blocks_context ON context_blocks(context_id);
				CREATE TABLE sessions (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    name TEXT NOT NULL,
				    creation_time INTEGER NOT NULL,
				    agent_initial_instructions TEXT NOT NULL,
				    preview TEXT NOT NULL,
				    context TEXT NOT NULL
				);
				CREATE TABLE dialog_records (
				    id INTEGER PRIMARY KEY AUTOINCREMENT,
				    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
				    ts INTEGER NOT NULL,
				    role TEXT NOT NULL,
				    message TEXT NOT NULL
				);
				CREATE INDEX ix_dialog_records_session_ts ON dialog_records(session_id, ts);
				PRAGMA user_version=1;
				""");
			schema.ExecuteNonQuery();
		}
		transaction.Commit();
		_initialized = true;
	}

	private static SessionDocument ReadSession(SqliteConnection connection, SqliteTransaction transaction, int sessionId,
		CancellationToken cancellationToken)
	{
		string name;
		DateTime creationTime;
		string instructions;
		string preview;
		ContextRecord[] context;
		using (var command = Command(connection, transaction,
			"SELECT name, creation_time, agent_initial_instructions, preview, context FROM sessions WHERE id = $id", ("$id", sessionId)))
		using (var reader = command.ExecuteReader())
		{
			if (!reader.Read())
			{
				throw MissingSession(sessionId);
			}
			name = reader.GetString(0);
			creationTime = UtcDate(reader.GetInt64(1));
			instructions = reader.GetString(2);
			preview = reader.GetString(3);
			context = JsonSerializer.Deserialize<ContextRecord[]>(reader.GetString(4), JsonOptions)
				?? throw new InvalidDataException("The stored session context is null.");
		}
		var records = new List<DialogRecord>();
		using (var command = Command(connection, transaction,
			"SELECT ts, role, message FROM dialog_records WHERE session_id = $id ORDER BY ts, id", ("$id", sessionId)))
		using (var reader = command.ExecuteReader())
		{
			while (reader.Read())
			{
				cancellationToken.ThrowIfCancellationRequested();
				records.Add(new DialogRecord(UtcDate(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
			}
		}
		return new SessionDocument
		{
			FormatVersion = 1, Name = name, CreationTime = creationTime, Instructions = instructions,
			Preview = preview, Context = context, Dialog = records.ToArray()
		};
	}

	private static SessionInfo InsertSession(SqliteConnection connection, SqliteTransaction transaction, SessionDocument document,
		CancellationToken cancellationToken)
	{
		var creationTime = ToUtc(document.CreationTime);
		using var command = Command(connection, transaction, """
			INSERT INTO sessions(name, creation_time, agent_initial_instructions, preview, context)
			VALUES($name, $time, $instructions, $preview, $context);
			SELECT last_insert_rowid();
			""", ("$name", document.Name), ("$time", creationTime.Ticks), ("$instructions", document.Instructions),
			("$preview", document.Preview), ("$context", JsonSerializer.Serialize(document.Context, JsonOptions)));
		var id = checked((int)(long)command.ExecuteScalar()!);
		foreach (var record in document.Dialog)
		{
			cancellationToken.ThrowIfCancellationRequested();
			InsertRecord(connection, transaction, id, record);
		}
		return new SessionInfo(id, creationTime, document.Name);
	}

	private static void InsertRecord(SqliteConnection connection, SqliteTransaction transaction, int sessionId, DialogRecord record)
	{
		using var command = Command(connection, transaction,
			"INSERT INTO dialog_records(session_id, ts, role, message) VALUES($id, $ts, $role, $message)",
			("$id", sessionId), ("$ts", ToUtc(record.Timestamp).Ticks), ("$role", record.Role), ("$message", record.Message));
		command.ExecuteNonQuery();
	}

	private static void RequireSession(SqliteConnection connection, SqliteTransaction transaction, int sessionId)
	{
		using var command = Command(connection, transaction, "SELECT 1 FROM sessions WHERE id = $id", ("$id", sessionId));
		if (command.ExecuteScalar() == null)
		{
			throw MissingSession(sessionId);
		}
	}

	private static DialogRecord ToRecord(DialogMessage message)
	{
		ArgumentNullException.ThrowIfNull(message);
		ArgumentNullException.ThrowIfNull(message.Text);
		var role = message.Source switch
		{
			AudioSourceType.Microphone => "interviewee",
			AudioSourceType.Application => "interviewer",
			AudioSourceType.None => "unknown",
			_ => throw new ArgumentException("A dialog record must have a single audio source.", nameof(message))
		};
		return new DialogRecord(ToUtc(message.Timestamp), role, message.Text);
	}

	private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
		? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
	private static DateTime UtcDate(long ticks) => new(ticks, DateTimeKind.Utc);
	private static KeyNotFoundException MissingSession(int id) => new($"Session {id} does not exist.");

	private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
		params (string Name, object Value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = sql;
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}
		return command;
	}
}
