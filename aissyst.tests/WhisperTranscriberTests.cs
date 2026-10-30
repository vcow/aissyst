using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using aissyst.core;
using aissyst.core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class WhisperTranscriberTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aissyst-model-tests-{Guid.NewGuid():N}");
	private static readonly byte[] ModelBytes = "Test model contents"u8.ToArray();
	private string ModelPath => Path.Combine(_directory, "model.bin");

	[Fact]
	public async Task DownloadVerifiesModelAndReusesCacheWithoutNetwork()
	{
		using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(ModelBytes)
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client);
		await transcriber.EnsureModelAsync(CancellationToken.None);
		Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(ModelPath));
		await transcriber.EnsureModelAsync(CancellationToken.None);
		Assert.Equal(1, handler.Requests);
		Assert.Single(Directory.GetFiles(_directory));
		Assert.False(transcriber.IsReady);
	}

	[Fact]
	public async Task CorruptCachedModelIsReplacedOnlyAfterSuccessfulDownload()
	{
		Directory.CreateDirectory(_directory);
		await File.WriteAllTextAsync(ModelPath, "Corrupt model");
		using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(ModelBytes)
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client);
		await transcriber.EnsureModelAsync(CancellationToken.None);
		Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(ModelPath));
	}

	[Theory]
	[InlineData(HttpStatusCode.OK)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	public async Task FailedDownloadLeavesNoModelOrTemporaryFile(HttpStatusCode status)
	{
		using var handler = new TestHandler(_ => new HttpResponseMessage(status)
		{
			Content = new StringContent("Invalid model response")
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client);
		await Assert.ThrowsAsync<InvalidOperationException>(() => transcriber.InitializeAsync());
		Assert.False(transcriber.IsReady);
		Assert.Empty(Directory.GetFiles(_directory));
	}

	[Fact]
	public async Task CancelledDownloadRemovesTemporaryFileAndCanRetry()
	{
		using var cancellation = new CancellationTokenSource();
		using var handler = new TestHandler(_ =>
		{
			cancellation.Cancel();
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ModelBytes) };
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcriber.InitializeAsync(cancellation.Token));
		Assert.False(transcriber.IsReady);
		Assert.Empty(Directory.GetFiles(_directory));
		await transcriber.EnsureModelAsync(CancellationToken.None);
		Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(ModelPath));
	}

	[Fact]
	public async Task DownloadReportsByteProgressAndKeepsOnlyStartAndCompletionInTheNormalLog()
	{
		var logger = new RecordingLogger();
		var progress = new TestInitializationProgress();
		using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(ModelBytes),
			RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.test/model.bin?signature=private-signature")
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client, logger, progress);
		await transcriber.EnsureModelAsync(CancellationToken.None);
		var messages = string.Join('\n', logger.Entries.Select(entry => entry.Message));
		Assert.Contains("Requesting transcription model from https://example.test/model.bin", messages);
		Assert.Contains("status 200", messages);
		Assert.Contains("final URL https://cdn.example.test/model.bin", messages);
		Assert.DoesNotContain("private-signature", messages);
		Assert.Contains("downloaded and verified in", messages);
		var normal = logger.Entries.Where(entry => entry.Level >= LogLevel.Information).ToArray();
		Assert.Equal(2, normal.Length);
		Assert.Contains(progress.Updates, update => update.CompletedBytes == ModelBytes.Length && update.TotalBytes == ModelBytes.Length);
		Assert.Contains(progress.Updates, update => update.Message.Contains("Verifying", StringComparison.Ordinal) && update.TotalBytes == null);
		Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(ModelPath));
	}

	[Theory]
	[InlineData(false, "waiting for HTTP headers")]
	[InlineData(true, "reading response body")]
	public async Task StalledNetworkReportsTheWaitingPhaseAndCancellationCleansUp(bool responseReceived, string expectedStage)
	{
		var logger = new RecordingLogger();
		var progress = new TestInitializationProgress();
		using var cancellation = new CancellationTokenSource();
		using var handler = new AsyncTestHandler(async (_, token) =>
		{
			if (!responseReceived)
			{
				await Task.Delay(Timeout.Infinite, token);
			}
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) };
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client, logger, progress);
		var download = transcriber.EnsureModelAsync(cancellation.Token);
		try
		{
			var update = await progress.WaitForAsync(update => responseReceived ? update.CompletedBytes == 0 : update.Message.Contains("Connecting", StringComparison.Ordinal));
			Assert.Null(update.TotalBytes);
			Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("download progress:", StringComparison.Ordinal));
			Assert.False(download.IsCompleted);
		}
		finally
		{
			await cancellation.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
		}
		Assert.Contains(logger.Entries, entry => entry.Message.Contains("cancelled during " + expectedStage, StringComparison.Ordinal));
		Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
		Assert.Empty(Directory.GetFiles(_directory));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ChunkedDownloadReportsActualBytesWithAndWithoutContentLength(bool knownLength)
	{
		var model = Enumerable.Repeat((byte)42, 5000).ToArray();
		var progress = new TestInitializationProgress();
		using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(new ChunkedStream(model, knownLength))
		});
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client, progress: progress, expectedModel: model);
		await transcriber.EnsureModelAsync(CancellationToken.None);
		var transfer = progress.Updates.Where(update => update.CompletedBytes.HasValue).ToArray();
		Assert.Equal(0, transfer[0].CompletedBytes);
		Assert.Equal(model.Length, transfer[^1].CompletedBytes);
		Assert.All(transfer, update => Assert.Equal(knownLength ? model.Length : (long?)null, update.TotalBytes));
		Assert.True(transfer.Length < model.Length);
		Assert.Equal(model, await File.ReadAllBytesAsync(ModelPath));
		var requests = handler.Requests;
		var beforeCache = progress.Updates.Count;
		await transcriber.EnsureModelAsync(CancellationToken.None);
		Assert.Equal(requests, handler.Requests);
		Assert.All(progress.Updates.Skip(beforeCache), update => Assert.Null(update.CompletedBytes));
	}

	[Fact]
	public async Task HttpTimeoutIsLoggedAsFailureRatherThanUserCancellation()
	{
		var logger = new RecordingLogger();
		using var handler = new AsyncTestHandler((_, _) => Task.FromException<HttpResponseMessage>(
			new TaskCanceledException("HTTP request timed out.", new TimeoutException("Test timeout."))));
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client, logger);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcriber.EnsureModelAsync(CancellationToken.None));
		var failure = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
		Assert.Contains("failed during waiting for HTTP headers", failure.Message);
		Assert.Contains("received 0 bytes", failure.Message);
		Assert.IsType<TaskCanceledException>(failure.Exception);
		Assert.IsType<TimeoutException>(failure.Exception.InnerException);
		Assert.Empty(Directory.GetFiles(_directory));
	}

	[Theory]
	[InlineData(HttpStatusCode.ServiceUnavailable, "checking HTTP response")]
	[InlineData(HttpStatusCode.OK, "verifying SHA-256")]
	public async Task FailuresNameTheirStageAndPreserveTheException(HttpStatusCode status, string stage)
	{
		var logger = new RecordingLogger();
		using var handler = new TestHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("Invalid model") });
		using var client = new HttpClient(handler);
		await using var transcriber = Create(client, logger);
		var exception = await Assert.ThrowsAnyAsync<Exception>(() => transcriber.EnsureModelAsync(CancellationToken.None));
		var failure = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
		Assert.Contains("failed during " + stage, failure.Message);
		Assert.Same(exception, failure.Exception);
		Assert.Empty(Directory.GetFiles(_directory));
	}

	[Fact]
	public async Task InitializationAfterDisposalIsRejectedBeforeNetworkAccess()
	{
		using var handler = new TestHandler(_ => throw new InvalidOperationException("Unexpected download."));
		using var client = new HttpClient(handler);
		var transcriber = Create(client);
		await transcriber.DisposeAsync();
		await transcriber.DisposeAsync();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => transcriber.InitializeAsync());
		Assert.Equal(0, handler.Requests);
	}

	private WhisperTranscriber Create(HttpClient client, ILogger<WhisperTranscriber>? logger = null,
		IProgress<InitializationProgress>? progress = null, byte[]? expectedModel = null)
	{
		var settings = new ApplicationSettings(transcriptionModelPath: ModelPath,
			transcriptionModelUrl: new Uri("https://example.test/model.bin"),
			transcriptionModelSha256: Convert.ToHexString(SHA256.HashData(expectedModel ?? ModelBytes)));
		return new WhisperTranscriber(settings, client, logger ?? NullLogger<WhisperTranscriber>.Instance, progress);
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		internal int Requests { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			return Task.FromResult(respond(request));
		}
	}

	private sealed class AsyncTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			respond(request, cancellationToken);
	}

	private sealed class WaitingStream : MemoryStream
	{
		public override bool CanSeek => false;
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return 0;
		}
	}

	private sealed class ChunkedStream(byte[] bytes, bool knownLength) : MemoryStream(bytes)
	{
		public override bool CanSeek => knownLength;
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
	}

	private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

	private sealed class RecordingLogger : ILogger<WhisperTranscriber>
	{
		internal ConcurrentQueue<LogEntry> Entries { get; } = new();
		public bool IsEnabled(LogLevel level) => true;
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			var entry = new LogEntry(level, formatter(state, exception), exception);
			Entries.Enqueue(entry);
		}
	}
}
