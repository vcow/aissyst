using System.Buffers.Binary;
using System.Text;
using aissyst.core.Audio;

namespace aissyst.console;

internal static class WaveFile
{
	internal static async Task WriteAsync(string path, AudioRecording recording)
	{
		var format = recording.Format;
		var dataLength = checked(recording.Samples.Length * sizeof(short));
		var header = new byte[44];
		Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), checked(36 + dataLength));
		Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
		BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); // PCM16
		BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), checked((short)format.Channels));
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), format.SampleRate);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), checked(format.SampleRate * format.Channels * sizeof(short)));
		BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), checked((short)(format.Channels * sizeof(short))));
		BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
		Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
		BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataLength);

		var temporaryPath = path + ".tmp";
		try
		{
			await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
				8192, FileOptions.Asynchronous))
			{
				await file.WriteAsync(header);
				var buffer = new byte[8192];
				for (var offset = 0; offset < recording.Samples.Length;)
				{
					var count = Math.Min(buffer.Length / sizeof(short), recording.Samples.Length - offset);
					for (var index = 0; index < count; index++)
					{
						var sample = Math.Clamp(recording.Samples.Span[offset + index], -1f, 1f);
						var value = (short)Math.Round(sample < 0 ? sample * 32768 : sample * 32767);
						BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index * sizeof(short)), value);
					}
					await file.WriteAsync(buffer.AsMemory(0, count * sizeof(short)));
					offset += count;
				}
			}
			File.Move(temporaryPath, path);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}
}
