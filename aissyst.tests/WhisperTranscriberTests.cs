using System.Net;
using System.Security.Cryptography;
using aissyst.console;
using aissyst.core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

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

	private WhisperTranscriber Create(HttpClient client)
	{
		var settings = new ApplicationSettings(transcriptionModelPath: ModelPath,
			transcriptionModelUrl: new Uri("https://example.test/model.bin"),
			transcriptionModelSha256: Convert.ToHexString(SHA256.HashData(ModelBytes)));
		return new WhisperTranscriber(settings, client, NullLogger<WhisperTranscriber>.Instance);
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
}
