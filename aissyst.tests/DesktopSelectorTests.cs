using System.IO.Compression;
using aissyst.core;
using aissyst.core.LLMConnector;
using aissyst.desktop.Selectors;
using aissyst.desktop.Wizard;
using aissyst.sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace aissyst.tests;

public sealed class DesktopSelectorTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "aissyst-desktop-" + Guid.NewGuid().ToString("N"));
	private readonly SqliteDatabase _database;
	public DesktopSelectorTests()
	{
		Directory.CreateDirectory(_directory);
		_database = new SqliteDatabase(Path.Combine(_directory, "test.sqlite"));
	}
	public void Dispose() => Directory.Delete(_directory, true);

	[Fact]
	public async Task ExistingSessionIsSelectedOnceWithoutCreatingNewRecords()
	{
		var context = await _database.CreateContextAsync(Template());
		var session = await _database.CreateSessionAsync(context.Id);
		var wizard = new ScriptedWizard(session.Id.ToString());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedSessionId());
		await selector.InitializeAsync();
		await selector.InitializeAsync();
		Assert.Equal(session.Id, selector.GetSelectedSessionId());
		Assert.Single(wizard.Pages);
		Assert.Single(await _database.GetSessionsAsync());
	}

	[Fact]
	public async Task NewSessionUsesExistingContext()
	{
		var context = await _database.CreateContextAsync(Template());
		await _database.CreateSessionAsync(context.Id);
		var wizard = new ScriptedWizard("create", context.Id.ToString());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		var session = await _database.GetSessionAsync(selector.GetSelectedSessionId());
		Assert.StartsWith(context.Name, session.SessionInfo.Name);
		Assert.Equal(2, (await _database.GetSessionsAsync()).Count);
	}

	[Fact]
	public async Task DeletingSessionRefreshesChoicesAndPreservesOtherSessionsAndContext()
	{
		var context = await _database.CreateContextAsync(Template());
		var removed = await _database.CreateSessionAsync(context.Id);
		var retained = await _database.CreateSessionAsync(context.Id);
		await _database.AddSessionDialogRecordAsync(removed.Id,
			new(DateTime.UtcNow, AudioSourceType.Application, "Deleted dialog"));
		await _database.AddSessionDialogRecordAsync(retained.Id,
			new(DateTime.UtcNow, AudioSourceType.Application, "Retained dialog"));
		var wizard = new ScriptedWizard("delete:" + removed.Id, retained.Id.ToString());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		Assert.Equal(retained.Id, selector.GetSelectedSessionId());
		Assert.Equal(retained.Id, Assert.Single(await _database.GetSessionsAsync()).Id);
		Assert.Single(await _database.GetContextsAsync());
		Assert.Equal("Retained dialog", Assert.Single((await _database.GetSessionAsync(retained.Id)).Dialog).Message);
		await Assert.ThrowsAsync<KeyNotFoundException>(() => _database.GetSessionAsync(removed.Id));
		Assert.DoesNotContain(wizard.Pages[1].Options, item => item.Key == removed.Id.ToString());
		var sessionOption = wizard.Pages[0].Options.First(item => item.Key == retained.Id.ToString());
		Assert.Equal("Next (Open)", sessionOption.NextLabel);
		Assert.Equal("Delete", sessionOption.SecondaryAction?.Label);
		var createOption = wizard.Pages[0].Options.First(item => item.Key == "create");
		Assert.Equal("Next", createOption.NextLabel);
		Assert.Null(createOption.SecondaryAction);
	}

	[Fact]
	public async Task DeletingLastSessionAdvancesToContextSelectionAndCanCreateAnotherSession()
	{
		var context = await _database.CreateContextAsync(Template());
		var removed = await _database.CreateSessionAsync(context.Id);
		var wizard = new ScriptedWizard("delete:" + removed.Id, context.Id.ToString());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		Assert.Equal("Select a context", wizard.Pages[1].Title);
		Assert.Single(await _database.GetContextsAsync());
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
		Assert.NotEqual(removed.Id, selector.GetSelectedSessionId());
	}

	[Fact]
	public async Task DeletingContextRefreshesChoicesWithoutChangingExistingSessions()
	{
		var removed = await _database.CreateContextAsync(Template(), "Removed context");
		var retained = await _database.CreateContextAsync(Path.Combine(_directory, "context.zip"), "Retained context");
		var existing = await _database.CreateSessionAsync(removed.Id);
		var wizard = new ScriptedWizard("create", "delete:" + removed.Id, retained.Id.ToString());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		Assert.Equal(retained.Id, Assert.Single(await _database.GetContextsAsync()).Id);
		Assert.Equal(2, (await _database.GetSessionsAsync()).Count);
		Assert.Equal(existing.Id, (await _database.GetSessionAsync(existing.Id)).SessionInfo.Id);
		Assert.Equal("Test preview", (await _database.GetSessionAsync(existing.Id)).Preview);
		Assert.DoesNotContain(wizard.Pages[2].Options, item => item.Key == removed.Id.ToString());
		Assert.Equal("Next (Create)", wizard.Pages[1].Options.First(item => item.Key == removed.Id.ToString()).NextLabel);
		Assert.NotNull(wizard.Pages[1].Options.First(item => item.Key == removed.Id.ToString()).SecondaryAction);
		Assert.Null(wizard.Pages[1].Options.First(item => item.Key == "template").SecondaryAction);
	}

	[Fact]
	public async Task DeletingLastSessionAndContextAdvancesToTemplateImport()
	{
		var context = await _database.CreateContextAsync(Template());
		var session = await _database.CreateSessionAsync(context.Id);
		var wizard = new ScriptedWizard("delete:" + session.Id, "delete:" + context.Id, Path.Combine(_directory, "context.zip"));
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		Assert.True(wizard.Pages[2].IsTemplatePath);
		Assert.NotEqual(context.Id, Assert.Single(await _database.GetContextsAsync()).Id);
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
	}

	[Fact]
	public async Task InvalidTemplateCanBeCorrectedWithoutLeavingSetup()
	{
		var wizard = new ScriptedWizard(Path.Combine(_directory, "missing.zip"), Template());
		var selector = new DesktopSessionSelector(_database, wizard, NullLogger<DesktopSessionSelector>.Instance);
		await selector.InitializeAsync();
		Assert.Equal(2, wizard.Pages.Count);
		Assert.All(wizard.Pages, page => Assert.True(page.IsTemplatePath));
		Assert.NotEmpty(wizard.Pages[1].Error);
		Assert.Equal(Assert.Single(await _database.GetSessionsAsync()).Id, selector.GetSelectedSessionId());
		Assert.Single(await _database.GetContextsAsync());
	}

	[Theory]
	[InlineData("low", "low")]
	[InlineData("server", null)]
	public async Task ModelAndEffortUseTheCatalog(string selectedEffort, string? expected)
	{
		var model = new LLMModel("test-model", "Test model")
		{
			ReasoningLevels = [new("low", "Low effort"), new("high", "High effort")], DefaultReasoningEffort = "low"
		};
		var wizard = new ScriptedWizard(model.Id, selectedEffort);
		var selector = new DesktopModelSelector(wizard, NullLogger<DesktopModelSelector>.Instance);
		await selector.InitializeAsync([model]);
		Assert.Equal(model, selector.GetSelectedModel());
		Assert.Equal(expected, selector.GetSelectedReasoningEffort());
		Assert.Equal("low", wizard.Pages[1].SelectedKey);
	}

	[Fact]
	public async Task ModelWithoutLevelsUsesServerDefaultWithoutAnExtraPage()
	{
		var wizard = new ScriptedWizard("model");
		var selector = new DesktopModelSelector(wizard, NullLogger<DesktopModelSelector>.Instance);
		await selector.InitializeAsync([new("model", "Model")]);
		Assert.Null(selector.GetSelectedReasoningEffort());
		Assert.Single(wizard.Pages);
	}

	[Fact]
	public async Task CancellationDuringEffortSelectionDoesNotCommitTheModel()
	{
		using var cancellation = new CancellationTokenSource();
		var wizard = new ScriptedWizard("model", "low") { OnPage = page =>
		{
			if (page.Title == "Select reasoning effort")
			{
				cancellation.Cancel();
			}
		} };
		var selector = new DesktopModelSelector(wizard, NullLogger<DesktopModelSelector>.Instance);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.InitializeAsync(
			[new("model", "Model") { ReasoningLevels = [new("low", "Low")] }], cancellation.Token));
		Assert.Throws<InvalidOperationException>(() => selector.GetSelectedModel());
	}

	[Fact]
	public async Task EmptyAudioSelectionIsRejectedAndCanBeCorrectedWithoutStartingCapture()
	{
		var sources = new TestAudioCapture();
		var wizard = new ScriptedWizard("none", "none", sources.InputDevices[0].Id, "none");
		using var selector = new DesktopCaptureSession(sources, new ApplicationSettings(), wizard,
			NullLogger<DesktopCaptureSession>.Instance);
		await selector.InitializeAsync();
		await selector.InitializeAsync();
		Assert.Equal(4, wizard.Pages.Count);
		Assert.NotEmpty(wizard.Pages[2].Error);
		Assert.Equal(sources.InputDevices[0], selector.SelectedMicrophone);
		Assert.Null(selector.SelectedApplication);
		Assert.Equal(0, sources.MicrophoneCaptures);
		Assert.Equal(0, sources.ApplicationCaptures);
	}

	private string Template()
	{
		var path = Path.Combine(_directory, "context.zip");
		using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
		foreach (var (name, text) in new[] { ("preview.txt", "Test preview"),
			("agent_initial_instructions.txt", "Test instructions"), ("context/background.txt", "Background\nTest facts") })
		{
			using var writer = new StreamWriter(archive.CreateEntry(name).Open());
			writer.Write(text);
		}
		return path;
	}

	private sealed class ScriptedWizard(params string[] answers) : IWizardService
	{
		private readonly Queue<string> _answers = new(answers);
		internal List<WizardPage> Pages { get; } = [];
		internal Action<WizardPage>? OnPage { get; init; }
		public Task<string> ShowAsync(WizardPage page, CancellationToken cancellationToken = default)
		{
			Pages.Add(page);
			OnPage?.Invoke(page);
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(_answers.Dequeue());
		}
	}
}
