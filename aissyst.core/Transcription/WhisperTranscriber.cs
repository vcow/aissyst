using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using aissyst.core.Audio;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace aissyst.core.Transcription;

public sealed class WhisperTranscriber(
	IApplicationSettings settings, HttpClient httpClient, ILogger<WhisperTranscriber> logger,
	IProgress<InitializationProgress>? initializationProgress = null) : ITranscriber
{
	private readonly SemaphoreSlim _lifecycle = new(1, 1);
	private WhisperFactory? _factory;
	private WhisperProcessor? _processor;
	private bool _disposed;
	private volatile bool _isReady;

	public bool IsReady => _isReady;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_isReady)
			{
				return;
			}

			var path = await EnsureModelAsync(cancellationToken);
			initializationProgress?.Report(new InitializationProgress("Loading transcription model..."));
			logger.LogInformation("Loading transcription model: {ModelPath}", path);
			// Native model loading is synchronous and cannot be interrupted safely.
			await Task.Run(() =>
			{
				_factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false });
				_processor = CreateProcessor();
			}, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			_isReady = true;
			logger.LogInformation("Transcriber ready. Language: {Language}", settings.TranscriptionLanguage);
		}
		catch (Exception exception)
		{
			ReleaseModel();
			if (exception is ObjectDisposedException
				|| exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			throw new InvalidOperationException($"Failed to initialize the transcriber: {exception.Message}", exception);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public async Task<string> TranscribeAsync(IReadOnlyList<AudioFrame> frames, CancellationToken cancellationToken = default)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!_isReady || _processor == null)
			{
				throw new InvalidOperationException("The transcriber has not been initialized.");
			}
			var samples = await Task.Run(() => WhisperAudioConverter.Convert(frames, cancellationToken), cancellationToken);
			if (samples.Length == 0)
			{
				return string.Empty;
			}
			var text = new StringBuilder();
			try
			{
				await foreach (var segment in _processor.ProcessAsync(samples.AsMemory(), cancellationToken))
				{
					text.Append(segment.Text);
				}
			}
			catch
			{
				// ProcessAsync may observe cancellation before the native inference task exits.
				await _processor.DisposeAsync();
				_processor = CreateProcessor();
				throw;
			}
			return text.ToString().Trim();
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	private WhisperProcessor CreateProcessor() => _factory!.CreateBuilder()
		.WithLanguage(settings.TranscriptionLanguage)
		.WithNoContext()
		.Build();

	internal async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
	{
		var path = Path.GetFullPath(settings.TranscriptionModelPath);
		if (File.Exists(path))
		{
			if (await HasExpectedHashAsync(path, cancellationToken))
			{
				return path;
			}
			logger.LogWarning("Transcription model checksum mismatch. Downloading a replacement: {ModelPath}", path);
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporaryPath = $"{path}.{Guid.NewGuid():N}.download";
		var elapsed = Stopwatch.StartNew();
		var stage = "waiting for HTTP headers";
		long receivedBytes = 0;
		logger.LogInformation("Downloading transcription model: {ModelPath}.", path);
		logger.LogDebug("Requesting transcription model from {ModelUrl}. HTTP header timeout: {HeaderTimeout}; temporary file: {TemporaryPath}.",
			DescribeUri(settings.TranscriptionModelUrl), httpClient.Timeout, temporaryPath);
		initializationProgress?.Report(new InitializationProgress("Connecting to the model download server..."));
		try
		{
			using var response = await httpClient.GetAsync(settings.TranscriptionModelUrl,
				HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			stage = "checking HTTP response";
			var totalBytes = response.Content.Headers.ContentLength;
			logger.LogDebug("Transcription model HTTP response: status {StatusCode}, HTTP/{HttpVersion}, content length {ContentLength}, final URL {FinalUrl}.",
				(int)response.StatusCode, response.Version, totalBytes?.ToString() ?? "unknown",
				DescribeUri(response.RequestMessage?.RequestUri ?? settings.TranscriptionModelUrl));
			response.EnsureSuccessStatusCode();
			initializationProgress?.Report(new InitializationProgress("Downloading transcription model", 0, totalBytes));
			stage = "opening response stream";
			await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
			stage = "creating temporary file";
			await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
				FileShare.None, 81920, FileOptions.Asynchronous))
			{
				var buffer = new byte[81920];
				var reporting = Stopwatch.StartNew();
				while (true)
				{
					stage = "reading response body";
					var count = await source.ReadAsync(buffer, cancellationToken);
					if (count == 0)
					{
						break;
					}
					receivedBytes += count;
					stage = "writing temporary file";
					await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
					if (receivedBytes == count || reporting.Elapsed >= TimeSpan.FromMilliseconds(200))
					{
						initializationProgress?.Report(new InitializationProgress("Downloading transcription model", receivedBytes, totalBytes));
						reporting.Restart();
					}
				}
				stage = "flushing temporary file";
			}
			initializationProgress?.Report(new InitializationProgress("Downloading transcription model", receivedBytes, totalBytes));
			stage = "verifying SHA-256";
			if (!await HasExpectedHashAsync(temporaryPath, cancellationToken))
			{
				throw new InvalidDataException("The downloaded transcription model failed SHA-256 verification.");
			}
			cancellationToken.ThrowIfCancellationRequested();
			stage = "installing verified model";
			File.Move(temporaryPath, path, overwrite: true);
			logger.LogInformation("Transcription model downloaded and verified in {ElapsedSeconds:F1}s: {ModelPath}", elapsed.Elapsed.TotalSeconds, path);
			return path;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			logger.LogInformation("Transcription model download cancelled during {Stage} after {ElapsedSeconds:F1}s; received {ReceivedBytes} bytes.",
				stage, elapsed.Elapsed.TotalSeconds, receivedBytes);
			throw;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Transcription model download failed during {Stage} after {ElapsedSeconds:F1}s; received {ReceivedBytes} bytes. HTTP header timeout: {HeaderTimeout}.",
				stage, elapsed.Elapsed.TotalSeconds, receivedBytes, httpClient.Timeout);
			throw;
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				logger.LogDebug("Removing incomplete transcription model: {TemporaryPath}.", temporaryPath);
				File.Delete(temporaryPath);
			}
		}
	}

	private async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
	{
		initializationProgress?.Report(new InitializationProgress("Verifying transcription model SHA-256..."));
		await using var stream = File.OpenRead(path);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken);
		var actual = Convert.ToHexString(hash);
		var matches = actual.Equals(settings.TranscriptionModelSha256, StringComparison.OrdinalIgnoreCase);
		if (!matches)
		{
			logger.LogWarning("Transcription model SHA-256 mismatch: {ModelPath}; expected {ExpectedHash}, actual {ActualHash}.",
				path, settings.TranscriptionModelSha256, actual);
		}
		return matches;
	}

	private static string DescribeUri(Uri uri) =>
		uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

	public async ValueTask DisposeAsync()
	{
		await _lifecycle.WaitAsync();
		try
		{
			_disposed = true;
			ReleaseModel();
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	private void ReleaseModel()
	{
		_isReady = false;
		_processor?.Dispose();
		_processor = null;
		_factory?.Dispose();
		_factory = null;
	}
}
