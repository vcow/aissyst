using aissyst.core;
using aissyst.desktop.Settings;

namespace aissyst.tests;

public sealed class DesktopSettingsStoreTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-settings-tests-{Guid.NewGuid():N}");
	private string FilePath => Path.Combine(_directory, "settings.json");

	[Fact]
	public async Task EverySettingRoundTripsWithoutChangingTheActiveSnapshotOrConsoleDefaults()
	{
		var store = new DesktopSettingsStore(FilePath);
		var defaults = store.Active;
		var console = new ApplicationSettings();
		Assert.Null(store.LoadError);
		Assert.False(File.Exists(FilePath));
		Assert.Equal(AudioSourceType.Both, defaults.AvailableSources);
		Assert.Equal(TimeSpan.FromSeconds(15), defaults.MaxRecordingDuration);
		Assert.Equal(TimeSpan.FromSeconds(30), console.MaxRecordingDuration);
		Assert.Equal(console.PauseUntilRecordingStops, defaults.PauseUntilRecordingStops);
		Assert.Equal(console.SignalThreshold, defaults.SignalThreshold);
		Assert.Equal(console.TranscriptionModelPath, defaults.TranscriptionModelPath);
		Assert.Equal(console.TranscriptionModelUrl, defaults.TranscriptionModelUrl);
		Assert.Equal(console.TranscriptionModelSha256, defaults.TranscriptionModelSha256);
		Assert.Equal(console.TranscriptionLanguage, defaults.TranscriptionLanguage);
		Assert.Equal(console.TranscriptionQueueCapacity, defaults.TranscriptionQueueCapacity);
		Assert.Equal(console.MaxTextProcessingCount, defaults.MaxTextProcessingCount);
		Assert.Equal(console.MaxTranscriptionProcessingCount, defaults.MaxTranscriptionProcessingCount);
		Assert.Equal(console.UseMarkdownFormat, defaults.UseMarkdownFormat);
		var changed = defaults with
		{
			AvailableSources = AudioSourceType.Application, PauseUntilRecordingStops = TimeSpan.FromSeconds(2.5),
			MaxRecordingDuration = TimeSpan.FromSeconds(20), SignalThreshold = 0.02f,
			TranscriptionModelPath = Path.Combine(_directory, "модели", "custom.bin"),
			TranscriptionModelUrl = new Uri("https://example.invalid/custom.bin"),
			TranscriptionModelSha256 = new string('a', 64), TranscriptionLanguage = "de",
			TranscriptionQueueCapacity = 25, MaxTranscriptionProcessingCount = 3,
			MaxTextProcessingCount = 5, UseMarkdownFormat = false
		};
		await store.SaveAsync(changed);
		Assert.Same(defaults, store.Active);
		Assert.Equal(changed, store.Saved);
		Assert.Contains("модели", await File.ReadAllTextAsync(FilePath));
		var restarted = new DesktopSettingsStore(FilePath);
		Assert.Null(restarted.LoadError);
		Assert.Equal(changed, restarted.Active);
		Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
	}

	[Theory]
	[InlineData("invalid JSON")]
	[InlineData("{\"MaxTranscriptionProcessingCount\":0}")]
	[InlineData("{\"TranscriptionModelUrl\":\"file:///tmp/model.bin\"}")]
	public void CorruptOrInvalidFileUsesDefaultsAndPreservesTheFileForCorrection(string content)
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(FilePath, content);
		var store = new DesktopSettingsStore(FilePath);
		Assert.NotNull(store.LoadError);
		Assert.Equal(new DesktopApplicationSettings(), store.Active);
		Assert.Equal(content, File.ReadAllText(FilePath));
	}

	[Fact]
	public async Task FailedSaveLeavesSavedAndActiveSettingsIntactAndRemovesTemporaryFile()
	{
		Directory.CreateDirectory(FilePath);
		var store = new DesktopSettingsStore(FilePath);
		var original = store.Saved;
		await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(original with { UseMarkdownFormat = false }));
		Assert.Same(original, store.Saved);
		Assert.Same(original, store.Active);
		Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}
}
