using System.IO.Compression;
using System.Text;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.Processor;
using aissyst.sqlite;
using Microsoft.Data.Sqlite;

namespace aissyst.tests;

public sealed class SqliteDatabaseTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "aissyst-sqlite-tests-" + Guid.NewGuid().ToString("N"));
	private readonly string _path;
	private readonly SqliteDatabase _database;

	public SqliteDatabaseTests()
	{
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "database.sqlite");
		_database = new SqliteDatabase(_path);
	}

	[Fact]
	public async Task TemplateImportPreservesTextAndIgnoresAncillaryFiles()
	{
		var template = Template();
		var context = await _database.CreateContextAsync(template, "  Custom context  ");
		Assert.Equal("Custom context", context.Name);
		Assert.Equal(context, Assert.Single(await _database.GetContextsAsync()));
		var info = await _database.CreateSessionAsync(context.Id);
		Assert.StartsWith("Custom context — ", info.Name);
		var session = await new SqliteDatabase(_path).GetSessionAsync(info.Id);
		Assert.Equal(info, session.SessionInfo);
		Assert.Equal("Preview\nSecond paragraph", session.Preview);
		Assert.Equal("Agent instructions", await _database.GetSessionInitialInstructionsAsync(info.Id));
		Assert.Equal(2, session.Context.Count);
		Assert.Contains(new ContextRecord("Background", "\r\nLine one\r\nLine two"), session.Context);
		Assert.Contains(new ContextRecord("Unknown topic", ""), session.Context);
		Assert.Empty(session.Dialog);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public async Task MissingContextNameUsesArchiveFileName(string? name)
	{
		var context = await _database.CreateContextAsync(Template(), name);
		Assert.Equal("template", context.Name);
	}

	[Fact]
	public async Task SessionsRetainIndependentSnapshots()
	{
		var context = await _database.CreateContextAsync(Template());
		var first = await _database.CreateSessionAsync(context.Id);
		Execute("UPDATE contexts SET preview='Changed', agent_initial_instructions='Changed'; UPDATE context_blocks SET body='Changed'");
		var second = await _database.CreateSessionAsync(context.Id);
		Execute("DELETE FROM contexts");
		Assert.Equal("Preview\nSecond paragraph", (await _database.GetSessionAsync(first.Id)).Preview);
		Assert.Equal("Agent instructions", await _database.GetSessionInitialInstructionsAsync(first.Id));
		Assert.Equal("Changed", (await _database.GetSessionAsync(second.Id)).Preview);
		Assert.Equal("Changed", await _database.GetSessionInitialInstructionsAsync(second.Id));
		Assert.Empty(await _database.GetContextsAsync());
	}

	[Fact]
	public async Task DialogIsOrderedByTimestampAndPreservesPrecisionAndSources()
	{
		var session = await Session();
		var now = DateTime.UtcNow.AddTicks(123);
		await _database.AddSessionDialogRecordAsync(session.Id, new DialogMessage(now.AddTicks(1), AudioSourceType.Application, "second"));
		await _database.AddSessionDialogRecordAsync(session.Id, new DialogMessage(now, AudioSourceType.Microphone, "first"));
		await _database.AddSessionDialogRecordAsync(session.Id, new DialogMessage(now, AudioSourceType.None, "same timestamp"));
		var records = (await _database.GetSessionAsync(session.Id)).Dialog;
		Assert.Equal(["first", "same timestamp", "second"], records.Select(record => record.Message));
		Assert.Equal(["interviewee", "unknown", "interviewer"], records.Select(record => record.Role));
		Assert.Equal(now, records[0].Timestamp);
		Assert.Equal(now.AddTicks(1), records[2].Timestamp);
		Assert.Equal(DateTimeKind.Utc, records[0].Timestamp.Kind);
		var other = await Session();
		Assert.Empty((await _database.GetSessionAsync(other.Id)).Dialog);
	}

	[Fact]
	public async Task ReplacementIsAtomicIfAnInsertFails()
	{
		var session = await Session();
		var original = new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "original");
		await _database.AddSessionDialogRecordAsync(session.Id, original);
		Execute("""
			CREATE TRIGGER fail_dialog BEFORE INSERT ON dialog_records WHEN NEW.message='fail'
			BEGIN SELECT RAISE(ABORT, 'Simulated insert failure'); END;
			""");
		await Assert.ThrowsAsync<SqliteException>(() => _database.SetSessionDialogAsync(session.Id,
			[original with { Text = "replacement" }, original with { Text = "fail" }]));
		Assert.Equal("original", Assert.Single((await _database.GetSessionAsync(session.Id)).Dialog).Message);
		await _database.SetSessionDialogAsync(session.Id, [original with { Text = "new" }]);
		Assert.Equal("new", Assert.Single((await _database.GetSessionAsync(session.Id)).Dialog).Message);
		await _database.SetSessionDialogAsync(session.Id, []);
		Assert.Empty((await _database.GetSessionAsync(session.Id)).Dialog);
	}

	[Fact]
	public async Task ExportCanBeImportedIntoAnotherDatabaseWithoutSourceContext()
	{
		var original = await Session();
		await _database.AddSessionDialogRecordAsync(original.Id,
			new DialogMessage(DateTime.UtcNow.AddTicks(7), AudioSourceType.Application, "Text with \nnewlines and Unicode: привет"));
		var exportPath = Path.Combine(_directory, "session.json.gz");
		await _database.ExportSessionAsync(original.Id, exportPath);
		var destination = new SqliteDatabase(Path.Combine(_directory, "destination.sqlite"));
		var context = await destination.CreateContextAsync(Template());
		await destination.CreateSessionAsync(context.Id);
		var imported = await destination.ImportSessionAsync(exportPath);
		Assert.NotEqual(original.Id, imported.Id);
		Assert.Equal(original.Name, imported.Name);
		Assert.Equal(original.CreationTime, imported.CreationTime);
		var expected = await _database.GetSessionAsync(original.Id);
		var actual = await destination.GetSessionAsync(imported.Id);
		Assert.Equal(expected.Preview, actual.Preview);
		Assert.Equal(expected.Context, actual.Context);
		Assert.Equal(expected.Dialog, actual.Dialog);
		Assert.Equal(await _database.GetSessionInitialInstructionsAsync(original.Id),
			await destination.GetSessionInitialInstructionsAsync(imported.Id));
		Assert.Equal(2, (await destination.GetSessionsAsync()).Count);
		var fresh = new SqliteDatabase(Path.Combine(_directory, "empty.sqlite"));
		await fresh.ImportSessionAsync(exportPath);
		Assert.Empty(await fresh.GetContextsAsync());
		Assert.Single(await fresh.GetSessionsAsync());
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("not JSON")]
	[InlineData("{\"format_version\":2,\"name\":\"x\",\"creation_time\":\"2026-01-01T00:00:00Z\",\"instructions\":\"\",\"preview\":\"\",\"context\":[],\"dialog\":[]}")]
	[InlineData("{\"format_version\":1,\"name\":null,\"creation_time\":\"2026-01-01T00:00:00Z\",\"instructions\":\"\",\"preview\":\"\",\"context\":[],\"dialog\":[]}")]
	[InlineData("{\"format_version\":1,\"name\":\"x\",\"creation_time\":\"2026-01-01T00:00:00Z\",\"instructions\":\"\",\"preview\":\"\",\"context\":[{}],\"dialog\":[]}")]
	public async Task InvalidExportsDoNotCreateSessions(string json)
	{
		var path = Path.Combine(_directory, "invalid.gz");
		await using (var stream = File.Create(path))
		await using (var gzip = new GZipStream(stream, CompressionMode.Compress))
		{
			await gzip.WriteAsync(Encoding.UTF8.GetBytes(json));
		}
		await Assert.ThrowsAsync<InvalidDataException>(() => _database.ImportSessionAsync(path));
		Assert.Empty(await _database.GetSessionsAsync());
	}

	[Theory]
	[InlineData("preview.txt")]
	[InlineData("agent_initial_instructions.txt")]
	[InlineData("empty-title")]
	public async Task InvalidTemplatesDoNotCreateContexts(string invalidPart)
	{
		var template = Template(invalidPart);
		await Assert.ThrowsAsync<InvalidDataException>(() => _database.CreateContextAsync(template));
		Assert.Empty(await _database.GetContextsAsync());
	}

	[Fact]
	public async Task ConcurrentInstancesCanAppendWithoutLosingRecords()
	{
		var session = await Session();
		var second = new SqliteDatabase(_path);
		var now = DateTime.UtcNow;
		await Task.WhenAll(Enumerable.Range(0, 30).Select(index =>
			(index % 2 == 0 ? _database : second).AddSessionDialogRecordAsync(session.Id,
				new DialogMessage(now.AddTicks(index), AudioSourceType.Application, index.ToString()))));
		Assert.Equal(Enumerable.Range(0, 30).Select(index => index.ToString()),
			(await _database.GetSessionAsync(session.Id)).Dialog.Select(record => record.Message));
	}

	[Fact]
	public async Task FailedContextWriteDoesNotLeavePartialContext()
	{
		await _database.GetContextsAsync();
		Execute("""
			CREATE TRIGGER fail_block BEFORE INSERT ON context_blocks WHEN NEW.topic='Unknown topic'
			BEGIN SELECT RAISE(ABORT, 'Simulated block failure'); END;
			""");
		await Assert.ThrowsAsync<SqliteException>(() => _database.CreateContextAsync(Template()));
		Assert.Empty(await _database.GetContextsAsync());
	}

	[Fact]
	public async Task FailedImportWriteDoesNotLeavePartialSession()
	{
		var original = await Session();
		var now = DateTime.UtcNow;
		await _database.SetSessionDialogAsync(original.Id,
			[new DialogMessage(now, AudioSourceType.Microphone, "first"),
			 new DialogMessage(now.AddTicks(1), AudioSourceType.Application, "fail")]);
		var path = Path.Combine(_directory, "session.gz");
		await _database.ExportSessionAsync(original.Id, path);
		Execute("""
			CREATE TRIGGER fail_import BEFORE INSERT ON dialog_records WHEN NEW.message='fail'
			BEGIN SELECT RAISE(ABORT, 'Simulated import failure'); END;
			""");
		await Assert.ThrowsAsync<SqliteException>(() => _database.ImportSessionAsync(path));
		Assert.Equal(original, Assert.Single(await _database.GetSessionsAsync()));
		Assert.Equal(2, (await _database.GetSessionAsync(original.Id)).Dialog.Count);
	}

	[Fact]
	public async Task MissingSessionsAndContextsProduceExplicitErrors()
	{
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.CreateSessionAsync(999));
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.GetSessionAsync(999));
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.GetSessionInitialInstructionsAsync(999));
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.SetSessionDialogAsync(999, []));
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.AddSessionDialogRecordAsync(999,
			new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "text")));
	}

	[Fact]
	public async Task CancelledOperationDoesNotModifyDatabaseAndSubsequentCallsWork()
	{
		var session = await Session();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _database.AddSessionDialogRecordAsync(session.Id,
			new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "cancelled"), cancellation.Token));
		Assert.Empty((await _database.GetSessionAsync(session.Id)).Dialog);
	}

	private async Task<SessionInfo> Session()
	{
		var context = await _database.CreateContextAsync(Template());
		return await _database.CreateSessionAsync(context.Id);
	}

	private string Template(string? invalidPart = null)
	{
		var path = Path.Combine(_directory, "template.zip");
		using var stream = File.Create(path);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
		void Add(string name, string text)
		{
			using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
			writer.Write(text);
		}
		if (invalidPart != "preview.txt")
		{
			Add("preview.txt", "Preview\nSecond paragraph");
		}
		if (invalidPart != "agent_initial_instructions.txt")
		{
			Add("agent_initial_instructions.txt", "Agent instructions");
		}
		Add("context/arbitrary-name.txt", invalidPart == "empty-title" ? "\nbody" : "\uFEFFBackground\r\n\r\nLine one\r\nLine two");
		Add("context/empty.txt", "Unknown topic\n");
		Add("context/nested/ignored.txt", "");
		Add("context/ignored.bin", "");
		Add("EMPTY_BLOCKS.txt", "Ignored root notes");
		return path;
	}

	private void Execute(string sql)
	{
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = _path, ForeignKeys = true, Pooling = false
		}.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	public void Dispose() => Directory.Delete(_directory, recursive: true);
}
