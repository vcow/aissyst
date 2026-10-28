using System.IO.Compression;
using aissyst.core;
using aissyst.core.Database;
using aissyst.core.Processor;
using aissyst.sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class TextProcessorPersistenceTests
{
	[Fact]
	public async Task SqlitePersistsOriginalFragmentsAndRestartsWithoutDuplicates()
	{
		var directory = Path.Combine(Path.GetTempPath(), "aissyst-persistence-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var zip = Path.Combine(directory, "template.zip");
			using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
			{
				archive.CreateEntry("context/");
				using (var writer = new StreamWriter(archive.CreateEntry("preview.txt").Open()))
				{
					writer.Write("Preview");
				}
				using (var writer = new StreamWriter(archive.CreateEntry("agent_initial_instructions.txt").Open()))
				{
					writer.Write("Rules");
				}
			}
			var database = new SqliteDatabase(Path.Combine(directory, "data.sqlite"));
			var context = await database.CreateContextAsync(zip);
			var session = await database.CreateSessionAsync(context.Id);
			var selector = new TestSessionSelector { SessionId = session.Id };
			await selector.InitializeAsync();
			var timestamp = DateTime.UtcNow;
			DialogMessage[] fragments = [new(timestamp, AudioSourceType.Application, "  First\nline  "),
				new(timestamp, AudioSourceType.Application, " Second "),
				new(timestamp.AddSeconds(1), AudioSourceType.Microphone, "   "),
				new(timestamp.AddSeconds(2), AudioSourceType.Microphone, " Answer ")];
			await using (var processor = new TextProcessor(selector, database, NullLogger<TextProcessor>.Instance))
			{
				await processor.InitializeAsync();
				foreach (var fragment in fragments)
				{
					await processor.ProcessAsync(fragment);
				}
				await processor.ReloadAsync();
				await processor.ReloadAsync();
				Assert.Equal("First\nline Second", processor.GetDialog(DateTime.MinValue)[0].Text);
				Assert.Equal(2, processor.GetDialog(DateTime.MinValue).Count);
			}
			var reopened = new SqliteDatabase(Path.Combine(directory, "data.sqlite"));
			var records = (await reopened.GetSessionAsync(session.Id)).Dialog;
			Assert.Equal(fragments.Select(fragment => fragment.Text), records.Select(record => record.Message));
			Assert.Equal(fragments.Select(fragment => fragment.Timestamp), records.Select(record => record.Timestamp));
			await using var restored = new TextProcessor(selector, reopened, NullLogger<TextProcessor>.Instance);
			await restored.InitializeAsync();
			Assert.Equal(2, restored.GetDialog(DateTime.MinValue).Count);
			Assert.Null(await restored.ProcessAsync(new DialogMessage(timestamp.AddSeconds(3), AudioSourceType.Application, "Next")));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task ReloadReplacesHistoryAndPendingTurnInsteadOfAppending()
	{
		var database = new TestSessionDatabase();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(new TestSessionSelector(), database);
		await processor.InitializeAsync();
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Old"));
		database.Session = database.Session with { Dialog = [new DialogRecord(DateTime.UtcNow, "interviewer", "Replacement")] };
		await processor.ReloadAsync();
		await processor.ReloadAsync();
		Assert.Equal("Replacement", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
		Assert.Null(await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "New")));
		Assert.Equal("Replacement New", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
	}

	[Fact]
	public async Task FragmentArrivingDuringReloadWaitsAndIsSavedExactlyOnce()
	{
		var database = new TestSessionDatabase();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(new TestSessionSelector(), database);
		await processor.InitializeAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		database.Load = async (_, token) =>
		{
			var snapshot = database.Session;
			entered.SetResult();
			await release.Task.WaitAsync(token);
			return snapshot;
		};
		var reload = processor.ReloadAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var save = processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "During reload"));
		Assert.False(save.IsCompleted);
		Assert.Empty(database.Session.Dialog);
		release.SetResult();
		await Task.WhenAll(reload, save).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("During reload", Assert.Single(database.Session.Dialog).Message);
		Assert.Equal("During reload", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
	}

	[Fact]
	public async Task ReloadWaitsForPendingCommitAndFailedSaveRemainsInMemory()
	{
		var database = new TestSessionDatabase();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(new TestSessionSelector(), database);
		await processor.InitializeAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		database.BeforeAppend = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
		var save = processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Application, "Committed"));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var reload = processor.ReloadAsync();
		Assert.False(reload.IsCompleted);
		release.SetResult();
		await Task.WhenAll(save, reload).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("Committed", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
		database.BeforeAppend = (_, _) => Task.FromException(new IOException("Write failed"));
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Retained"));
		Assert.Equal(new[] { "Committed", "Retained" }, processor.GetDialog(DateTime.MinValue).Select(message => message.Text));
		await processor.ReloadAsync();
		await processor.ReloadAsync();
		Assert.Equal(new[] { "Committed", "Retained" }, processor.GetDialog(DateTime.MinValue).Select(message => message.Text));
		database.BeforeAppend = null;
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Next"));
		Assert.Equal(new[] { "Committed", "Retained", "Next" }, database.Session.Dialog.Select(record => record.Message));
		await processor.ReloadAsync();
		Assert.Equal("Retained Next", processor.GetDialog(DateTime.MinValue)[1].Text);
	}

	[Fact]
	public async Task DisposalMakesFinalAttemptToFlushBufferedFragments()
	{
		var database = new TestSessionDatabase { BeforeAppend = (_, _) => Task.FromException(new IOException("Offline")) };
		var processor = await TextProcessorInitializationTests.CreateAsync(new TestSessionSelector(), database);
		await processor.InitializeAsync();
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Buffered"));
		Assert.Empty(database.Session.Dialog);
		database.BeforeAppend = null;
		await processor.DisposeAsync();
		Assert.Equal("Buffered", Assert.Single(database.Session.Dialog).Message);
	}

	[Fact]
	public async Task FailedOrCancelledReloadKeepsThePreviousSnapshot()
	{
		var database = new TestSessionDatabase();
		await using var processor = await TextProcessorInitializationTests.CreateAsync(new TestSessionSelector(), database);
		await processor.InitializeAsync();
		await processor.ProcessAsync(new DialogMessage(DateTime.UtcNow, AudioSourceType.Microphone, "Retained"));
		database.Load = (_, _) => Task.FromException<SessionContext>(new IOException("Read failed"));
		await Assert.ThrowsAsync<IOException>(() => processor.ReloadAsync());
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ReloadAsync(cancellation.Token));
		Assert.True(processor.IsReady);
		Assert.Equal("Retained", Assert.Single(processor.GetDialog(DateTime.MinValue)).Text);
	}
}
