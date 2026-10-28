using aissyst.console;
using aissyst.core;
using aissyst.core.Processor;

namespace aissyst.tests;

public sealed class ConsoleOutputTests
{
	[Fact]
	public async Task BatchDoesNotInterleaveWithParallelSingleMessageOrAnswer()
	{
		using var writer = new BlockingWriter();
		var output = new ConsoleOutput(writer, () => { });
		DialogMessage Message(string text) => new(DateTime.UtcNow, AudioSourceType.Application, text);
		var batch = Task.Run(() => output.PrintMultipleDialogMessages([Message("First"), Message("Second")]));
		await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		using var started = new CountdownEvent(2);
		var single = Task.Run(() => { started.Signal(); output.PrintDialogMessage(Message("Single")); });
		var answer = Task.Run(() => { started.Signal(); output.PrintLLMAnswer("Answer"); });
		try
		{
			Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
			Assert.False(single.IsCompleted);
			Assert.False(answer.IsCompleted);
		}
		finally
		{
			writer.Release.Set();
		}
		await Task.WhenAll(batch, single, answer).WaitAsync(TimeSpan.FromSeconds(5));
		var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.EndsWith(": First", lines[0]);
		Assert.EndsWith(": Second", lines[1]);
		Assert.Equal(4, lines.Length);
	}

	[Fact]
	public void ClearDelegatesToOutputAndEmptyBatchWritesNothing()
	{
		using var writer = new StringWriter();
		var clears = 0;
		var output = new ConsoleOutput(writer, () => { clears++; writer.GetStringBuilder().Clear(); });
		output.PrintLLMAnswer("Old");
		output.Clear();
		output.PrintMultipleDialogMessages([]);
		Assert.Equal(1, clears);
		Assert.Equal("", writer.ToString());
	}

	private sealed class BlockingWriter : StringWriter
	{
		internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly ManualResetEventSlim Release = new();
		private bool _first = true;
		public override void WriteLine(string? value)
		{
			base.WriteLine(value);
			if (_first)
			{
				_first = false;
				Entered.SetResult();
				if (!Release.Wait(TimeSpan.FromSeconds(10)))
				{
					throw new TimeoutException("The batch test was not released.");
				}
			}
		}
		protected override void Dispose(bool disposing)
		{
			Release.Dispose();
			base.Dispose(disposing);
		}
	}
}
