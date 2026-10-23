using Microsoft.Extensions.Logging;

namespace aissyst.core.Controller;

internal static class BackgroundOperation
{
	internal static async Task RunAsync(Func<Task> operation, ILogger logger,
		string operationName, CancellationToken cancellationToken)
	{
		try
		{
			await operation();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Cancellation is expected while the application is stopping.
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Background operation failed: {Operation}", operationName);
		}
	}
}
