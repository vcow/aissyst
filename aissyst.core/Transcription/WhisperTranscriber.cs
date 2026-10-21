using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace aissyst.core.Transcription;

public sealed class WhisperTranscriber(
	IApplicationSettings settings, HttpClient httpClient, ILogger<WhisperTranscriber> logger) : ITranscriber
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
			logger.LogInformation("Loading transcription model: {ModelPath}", path);
			// Native model loading is synchronous and cannot be interrupted safely.
			await Task.Run(() =>
			{
				_factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false });
				_processor = _factory.CreateBuilder().WithLanguage(settings.TranscriptionLanguage).Build();
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
		else
		{
			logger.LogWarning("Transcription model is missing. Downloading it before startup: {ModelPath}", path);
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporaryPath = $"{path}.{Guid.NewGuid():N}.download";
		try
		{
			using var response = await httpClient.GetAsync(settings.TranscriptionModelUrl,
				HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			response.EnsureSuccessStatusCode();
			await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
				FileShare.None, 81920, FileOptions.Asynchronous))
			{
				await response.Content.CopyToAsync(target, cancellationToken);
			}
			if (!await HasExpectedHashAsync(temporaryPath, cancellationToken))
			{
				throw new InvalidDataException("The downloaded transcription model failed SHA-256 verification.");
			}
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, path, overwrite: true);
			logger.LogInformation("Transcription model downloaded and verified: {ModelPath}", path);
			return path;
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = File.OpenRead(path);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken);
		return Convert.ToHexString(hash).Equals(settings.TranscriptionModelSha256, StringComparison.OrdinalIgnoreCase);
	}

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
