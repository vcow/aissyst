using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using aissyst.core.Audio;

namespace aissyst.macos.Interop;

internal static class NativeAudioCapture
{
	internal static async IAsyncEnumerable<AudioFrame> ReadAsync(string source, string? argument,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var helper = Path.Combine(AppContext.BaseDirectory, "aissyst-audio-helper");
		if (!File.Exists(helper))
		{
			throw new InvalidOperationException("The macOS audio helper is missing. Build the project on macOS with Xcode Command Line Tools installed.");
		}
		var startInfo = new ProcessStartInfo(helper)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(source);
		if (argument != null)
		{
			startInfo.ArgumentList.Add(argument);
		}
		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start the macOS audio helper.");
		var errors = process.StandardError.ReadToEndAsync();
		try
		{
			var header = new byte[24];
			while (true)
			{
				var count = await process.StandardOutput.BaseStream.ReadAtLeastAsync(header, header.Length, false, cancellationToken);
				if (count == 0)
				{
					await process.WaitForExitAsync(cancellationToken);
					var message = (await errors).Trim();
					throw new InvalidOperationException($"{source} capture ended (exit code {process.ExitCode}). {message}");
				}
				if (count != header.Length || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x31445541)
				{
					throw new InvalidOperationException("Invalid audio helper packet header.");
				}
				var frames = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
				var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
				var channels = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
				var timestamp = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
				if (frames <= 0 || frames > 16384 || rate <= 0 || rate > 384000 || channels <= 0 || channels > 32)
				{
					throw new InvalidOperationException("Invalid PCM format from the audio helper.");
				}
				var payload = new byte[checked(frames * channels * sizeof(float))];
				await process.StandardOutput.BaseStream.ReadExactlyAsync(payload, cancellationToken);
				var samples = MemoryMarshal.Cast<byte, float>(payload).ToArray();
				var chunkFrames = Math.Max(1, rate / 50); // Approximately 20 ms per managed frame.
				for (var offset = 0; offset < frames; offset += chunkFrames)
				{
					var length = Math.Min(chunkFrames, frames - offset);
					yield return new AudioFrame(samples.AsMemory(offset * channels, length * channels),
						new AudioFormat(rate, channels), TimeSpan.FromTicks(timestamp / 100) + TimeSpan.FromSeconds((double)offset / rate));
				}
			}
		}
		finally
		{
			await StopAsync(process);
			await errors;
		}
	}

	private static async Task StopAsync(Process process)
	{
		if (!process.HasExited)
		{
			try
			{
				await process.StandardInput.WriteLineAsync("stop");
				process.StandardInput.Close();
			}
			catch (IOException)
			{
				// The helper may already have exited because a device or permission became unavailable.
			}
		}
		// Drain buffered packets while the helper stops, so it cannot block on a full stdout pipe.
		var drain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
		try
		{
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch (TimeoutException)
		{
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
		}
		await drain;
	}
}
