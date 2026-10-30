using System.Collections.Concurrent;
using System.Threading.Channels;
using aissyst.core;

namespace aissyst.tests;

internal sealed class TestInitializationProgress : IProgress<InitializationProgress>
{
	internal ConcurrentQueue<InitializationProgress> Updates { get; } = new();
	private readonly Channel<InitializationProgress> _updates = Channel.CreateUnbounded<InitializationProgress>();
	public void Report(InitializationProgress value)
	{
		Updates.Enqueue(value);
		_updates.Writer.TryWrite(value);
	}

	internal async Task<InitializationProgress> WaitForAsync(Func<InitializationProgress, bool> predicate)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true)
		{
			var update = await _updates.Reader.ReadAsync(timeout.Token);
			if (predicate(update))
			{
				return update;
			}
		}
	}
}
