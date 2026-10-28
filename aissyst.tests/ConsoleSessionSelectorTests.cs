using System.IO.Compression;
using aissyst.console;
using aissyst.sqlite;

namespace aissyst.tests;

public sealed class ConsoleSessionSelectorTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "aissyst-selector-" + Guid.NewGuid().ToString("N"));
	private readonly SqliteDatabase _database;
	private readonly StringWriter _output = new();

	public ConsoleSessionSelectorTests()
	{
		Directory.CreateDirectory(_directory);
		_database = new SqliteDatabase(Path.Combine(_directory, "database.sqlite"));
	}

	[Fact]
	public async Task ExistingSessionRetriesInvalidInputAndInitializesOnlyOnce()
	{
		var context = await _database.CreateContextAsync(Template());
		var session = await _database.CreateSessionAsync(context.Id);
		var selector = Selector("wrong", "0", "3", "1", "c", "o");
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
		await selector.InitializeAsync();
		await selector.InitializeAsync();
		Assert.Equal(session.Id, selector.GetSelectedSessionId());
		Assert.Single(await _database.GetSessionsAsync());
		Assert.Contains("Invalid selection", _output.ToString());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CreatesSessionFromExistingContext(bool existingSession)
	{
		var context = await _database.CreateContextAsync(Template());
		if (existingSession)
		{
			await _database.CreateSessionAsync(context.Id);
		}
		var selector = existingSession ? Selector("2", "1", "c") : Selector("1", "c");
		await selector.InitializeAsync();
		var session = await _database.GetSessionAsync(selector.GetSelectedSessionId());
		Assert.StartsWith(context.Name, session.SessionInfo.Name);
		Assert.Equal(existingSession ? 2 : 1, (await _database.GetSessionsAsync()).Count);
	}

	[Theory]
	[InlineData(false, "\"")]
	[InlineData(true, "'")]
	public async Task ImportsTemplateAndImmediatelySelectsNewSession(bool existingContext, string quote)
	{
		if (existingContext)
		{
			await _database.CreateContextAsync(Template());
		}
		var path = quote + Template() + quote;
		var selector = existingContext ? Selector("2", path) : Selector(path);
		await selector.InitializeAsync();
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
		Assert.Equal(existingContext ? 2 : 1, (await _database.GetContextsAsync()).Count);
		Assert.Contains("No existing sessions", _output.ToString());
	}

	[Fact]
	public async Task InvalidTemplateFailsWithoutSelectingOrCreatingSession()
	{
		var selector = Selector(Path.Combine(_directory, "missing.zip"));
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => selector.InitializeAsync());
		Assert.Contains("Failed to load the context template", error.Message);
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
		Assert.Empty(await _database.GetSessionsAsync());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EndOfInputLeavesSessionUnselected(bool existingContext)
	{
		if (existingContext)
		{
			await _database.CreateContextAsync(Template());
		}
		var selector = Selector();
		await Assert.ThrowsAsync<EndOfStreamException>(() => selector.InitializeAsync());
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
		Assert.Empty(await _database.GetSessionsAsync());
	}

	[Fact]
	public async Task CancellationDuringInputLeavesSessionUnselected()
	{
		using var cancellation = new CancellationTokenSource();
		var selector = new ConsoleSessionSelector(_database, async token =>
		{
			await cancellation.CancelAsync();
			await Task.Delay(Timeout.Infinite, token);
			return null;
		}, _output);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.InitializeAsync(cancellation.Token));
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
		Assert.Empty(await _database.GetSessionsAsync());
	}

	[Theory]
	[InlineData("n", null)]
	[InlineData("d", "DELETE")]
	[InlineData("d", "")]
	[InlineData("d", " delete ")]
	public async Task ContextReturnAndUnconfirmedDeletionPreserveContext(string action, string? confirmation)
	{
		var context = await _database.CreateContextAsync(Template());
		var selector = confirmation == null
			? Selector("1", action, "1", "c")
			: Selector("1", action, confirmation, "1", "c");
		await selector.InitializeAsync();
		Assert.Equal(context.Id, Assert.Single(await _database.GetContextsAsync()).Id);
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
	}

	[Fact]
	public async Task DeletingLastContextReturnsToEmptyListAndTemplateImport()
	{
		var context = await _database.CreateContextAsync(Template());
		var selector = Selector("1", "d", "delete", Template());
		await selector.InitializeAsync();
		Assert.NotEqual(context.Id, Assert.Single(await _database.GetContextsAsync()).Id);
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
		Assert.Contains("No available contexts", _output.ToString());
	}

	[Fact]
	public async Task EndOfInputDuringDeleteConfirmationPreservesContext()
	{
		await _database.CreateContextAsync(Template());
		var selector = Selector("1", "d");
		await Assert.ThrowsAsync<EndOfStreamException>(() => selector.InitializeAsync());
		Assert.Single(await _database.GetContextsAsync());
		Assert.Empty(await _database.GetSessionsAsync());
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
	}

	[Theory]
	[InlineData("n", null)]
	[InlineData("d", "DELETE")]
	[InlineData("d", "")]
	[InlineData("d", " delete ")]
	public async Task SessionReturnAndUnconfirmedDeletionPreserveSession(string action, string? confirmation)
	{
		var context = await _database.CreateContextAsync(Template());
		var session = await _database.CreateSessionAsync(context.Id);
		var selector = confirmation == null
			? Selector("1", action, "1", "o")
			: Selector("1", action, confirmation, "1", "o");
		await selector.InitializeAsync();
		Assert.Equal(session.Id, selector.GetSelectedSessionId());
		Assert.Single(await _database.GetSessionsAsync());
	}

	[Fact]
	public async Task SessionDeletionRefreshesListBeforeOpeningRemainingSession()
	{
		var context = await _database.CreateContextAsync(Template());
		var deleted = await _database.CreateSessionAsync(context.Id);
		var remaining = await _database.CreateSessionAsync(context.Id);
		var selector = Selector("1", "d", "delete", "1", "o");
		await selector.InitializeAsync();
		Assert.Equal(remaining.Id, selector.GetSelectedSessionId());
		Assert.Equal(remaining.Id, Assert.Single(await _database.GetSessionsAsync()).Id);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.GetSessionAsync(deleted.Id));
	}

	[Fact]
	public async Task DeletingLastSessionAdvancesToContexts()
	{
		var context = await _database.CreateContextAsync(Template());
		var deleted = await _database.CreateSessionAsync(context.Id);
		var selector = Selector("1", "d", "delete", "1", "c");
		await selector.InitializeAsync();
		Assert.NotEqual(deleted.Id, selector.GetSelectedSessionId());
		Assert.Single(await _database.GetSessionsAsync());
		Assert.Contains("No existing sessions", _output.ToString());
	}

	[Fact]
	public async Task EndOfInputDuringSessionDeletionPreservesSession()
	{
		var context = await _database.CreateContextAsync(Template());
		await _database.CreateSessionAsync(context.Id);
		var selector = Selector("1", "d");
		await Assert.ThrowsAsync<EndOfStreamException>(() => selector.InitializeAsync());
		Assert.Single(await _database.GetSessionsAsync());
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
	}

	private ConsoleSessionSelector Selector(params string[] lines)
	{
		var input = new Queue<string>(lines);
		return new ConsoleSessionSelector(_database,
			_ => Task.FromResult(input.TryDequeue(out var line) ? line : null), _output);
	}

	private string Template()
	{
		var path = Path.Combine(_directory, "context template " + Guid.NewGuid().ToString("N") + ".zip");
		using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
		archive.CreateEntry("context/");
		using (var writer = new StreamWriter(archive.CreateEntry("preview.txt").Open()))
		{
			writer.Write("Preview");
		}
		using (var writer = new StreamWriter(archive.CreateEntry("agent_initial_instructions.txt").Open()))
		{
			writer.Write("Instructions");
		}
		return path;
	}

	public void Dispose()
	{
		_output.Dispose();
		Directory.Delete(_directory, recursive: true);
	}
}
