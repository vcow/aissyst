using aissyst.core.Database;
using aissyst.core.LLMConnector.Model;

namespace aissyst.console;

public sealed class ConsoleSessionSelector : ISessionSelector
{
	private readonly IDatabase _database;
	private readonly Func<CancellationToken, Task<string?>> _readLine;
	private readonly TextWriter _output;
	private int? _selectedSessionId;

	public ConsoleSessionSelector(IDatabase database)
		: this(database, ConsoleInput.ReadLineAsync, Console.Out)
	{
	}

	internal ConsoleSessionSelector(IDatabase database, Func<CancellationToken, Task<string?>> readLine, TextWriter output)
	{
		_database = database;
		_readLine = readLine;
		_output = output;
	}

	public int GetSelectedSessionId() => _selectedSessionId
		?? throw new InvalidOperationException("No session has been selected.");

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_selectedSessionId.HasValue)
		{
			return;
		}

		while (true)
		{
			var sessions = await _database.GetSessionsAsync(cancellationToken);
			if (sessions.Count == 0)
			{
				_output.WriteLine("No existing sessions found.");
				await SelectContextAsync(cancellationToken);
				return;
			}

			_output.WriteLine("Available sessions:");
			for (var index = 0; index < sessions.Count; index++)
			{
				var session = sessions[index];
				_output.WriteLine($"{index + 1}. {session.Name} ({session.CreationTime:u})");
			}
			_output.WriteLine($"{sessions.Count + 1}. Create New");
			var selection = await ReadSelectionAsync(sessions.Count + 1, cancellationToken);
			if (selection > sessions.Count)
			{
				await SelectContextAsync(cancellationToken);
				return;
			}

			var selected = sessions[selection - 1];
			var action = await ReadActionAsync($"Session: {selected.Name}", "o", "open", cancellationToken);
			if (action == "o")
			{
				cancellationToken.ThrowIfCancellationRequested();
				_selectedSessionId = selected.Id;
				return;
			}
			if (action == "d" && await ConfirmDeleteAsync(cancellationToken))
			{
				await _database.DeleteSessionAsync(selected.Id, cancellationToken);
			}
		}
	}

	private async Task SelectContextAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			var contexts = await _database.GetContextsAsync(cancellationToken);
			int contextId;
			if (contexts.Count > 0)
			{
				_output.WriteLine("Available contexts:");
				for (var index = 0; index < contexts.Count; index++)
				{
					_output.WriteLine($"{index + 1}. {contexts[index].Name}");
				}
				_output.WriteLine($"{contexts.Count + 1}. Create from Template");
				var selection = await ReadSelectionAsync(contexts.Count + 1, cancellationToken);
				if (selection <= contexts.Count)
				{
					var context = contexts[selection - 1];
					var action = await ReadActionAsync($"Context: {context.Name}", "c", "create new session", cancellationToken);
					if (action == "d")
					{
						if (await ConfirmDeleteAsync(cancellationToken))
						{
							await _database.DeleteContextAsync(context.Id, cancellationToken);
						}
						continue;
					}
					if (action == "n")
					{
						continue;
					}
					contextId = context.Id;
				}
				else
				{
					contextId = await LoadTemplateAsync(cancellationToken);
				}
			}
			else
			{
				_output.WriteLine("No available contexts found.");
				contextId = await LoadTemplateAsync(cancellationToken);
			}

			var created = await _database.CreateSessionAsync(contextId, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			_selectedSessionId = created.Id;
			_output.WriteLine($"Created session: {created.Name}");
			return;
		}
	}

	private async Task<string> ReadActionAsync(string selected, string proceedKey, string proceed, CancellationToken cancellationToken)
	{
		_output.WriteLine(selected);
		while (true)
		{
			_output.Write($"({proceedKey}){proceed}, (n)return to list, (d)delete: ");
			var action = (await ReadRequiredLineAsync(cancellationToken)).Trim().ToLowerInvariant();
			if (action == proceedKey || action is "n" or "d")
			{
				return action;
			}
			_output.WriteLine($"Invalid action. Enter {proceedKey}, n, or d.");
		}
	}

	private async Task<bool> ConfirmDeleteAsync(CancellationToken cancellationToken)
	{
		_output.Write("Type delete to confirm deletion, or anything else to return to the list: ");
		return await ReadRequiredLineAsync(cancellationToken) == "delete";
	}

	private async Task<int> LoadTemplateAsync(CancellationToken cancellationToken)
	{
		_output.Write("Enter the context template ZIP path: ");
		var path = (await ReadRequiredLineAsync(cancellationToken)).Trim();
		if (path.Length >= 2 && ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
		{
			path = path[1..^1];
		}
		try
		{
			return (await _database.CreateContextAsync(path, cancellationToken: cancellationToken)).Id;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new InvalidOperationException($"Failed to load the context template: {exception.Message}", exception);
		}
	}

	private async Task<int> ReadSelectionAsync(int count, CancellationToken cancellationToken)
	{
		while (true)
		{
			_output.Write($"Select by number (1-{count}): ");
			var input = await ReadRequiredLineAsync(cancellationToken);
			if (int.TryParse(input, out var number) && number >= 1 && number <= count)
			{
				return number;
			}
			_output.WriteLine($"Invalid selection. Enter a number from 1 to {count}.");
		}
	}

	private async Task<string> ReadRequiredLineAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var line = await _readLine(cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		return line ?? throw new EndOfStreamException("Standard input was closed before selecting a session.");
	}
}
