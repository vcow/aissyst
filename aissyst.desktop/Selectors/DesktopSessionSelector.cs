using System.Globalization;
using aissyst.core.Database;
using aissyst.core.LLMConnector.Model;
using aissyst.desktop.Wizard;
using Microsoft.Extensions.Logging;

namespace aissyst.desktop.Selectors;

internal sealed class DesktopSessionSelector(IDatabase database, IWizardService wizard,
	ILogger<DesktopSessionSelector> logger) : ISessionSelector
{
	private int? _selected;
	public int GetSelectedSessionId() => _selected ?? throw new InvalidOperationException("No session has been selected.");

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_selected.HasValue)
		{
			return;
		}
		var error = string.Empty;
		string? selectedKey = null;
		while (true)
		{
			var sessions = await database.GetSessionsAsync(cancellationToken);
			if (sessions.Count == 0)
			{
				break;
			}
			var options = sessions.Select(session => new WizardOption(session.Id.ToString(CultureInfo.InvariantCulture),
				session.Name, session.CreationTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
				{
					NextLabel = "Next (Open)",
					SecondaryAction = new WizardAction("delete:" + session.Id.ToString(CultureInfo.InvariantCulture), "Delete",
						$"Delete session '{session.Name}' and its entire dialog? This cannot be undone.") { ConfirmationLabel = "Confirm Delete" }
				})
				.Append(new WizardOption("create", "Create New", "Start a new dialog using a prepared context.")).ToArray();
			var selected = await wizard.ShowAsync(new WizardPage("Select a session",
				"Continue a saved dialog, delete it, or create a new session.", options)
			{
				SelectedKey = selectedKey, Error = error
			}, cancellationToken);
			if (selected == "create")
			{
				break;
			}
			var session = sessions.FirstOrDefault(item => item.Id.ToString(CultureInfo.InvariantCulture) == selected);
			if (session != null)
			{
				cancellationToken.ThrowIfCancellationRequested();
				_selected = session.Id;
				logger.LogInformation("Selected session: {Session}.", session.Name);
				return;
			}
			var deleted = sessions.First(item => "delete:" + item.Id.ToString(CultureInfo.InvariantCulture) == selected);
			try
			{
				await database.DeleteSessionAsync(deleted.Id, cancellationToken);
				logger.LogInformation("Deleted session: {Session}.", deleted.Name);
				error = string.Empty;
				selectedKey = null;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to delete session {SessionId}.", deleted.Id);
				error = exception.Message;
				selectedKey = deleted.Id.ToString(CultureInfo.InvariantCulture);
			}
		}

		var contextId = await SelectContextAsync(cancellationToken);
		var created = await database.CreateSessionAsync(contextId, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		_selected = created.Id;
		logger.LogInformation("Created session: {Session}.", created.Name);
	}

	private async Task<int> SelectContextAsync(CancellationToken cancellationToken)
	{
		var error = string.Empty;
		string? selectedKey = null;
		while (true)
		{
			var contexts = await database.GetContextsAsync(cancellationToken);
			if (contexts.Count == 0)
			{
				return await ImportTemplateAsync(cancellationToken);
			}
			var options = contexts.Select(context => new WizardOption(context.Id.ToString(CultureInfo.InvariantCulture), context.Name)
			{
				NextLabel = "Next (Create)",
				SecondaryAction = new WizardAction("delete:" + context.Id.ToString(CultureInfo.InvariantCulture), "Delete",
					$"Delete context '{context.Name}'? Existing sessions will be preserved. This cannot be undone.")
				{
					ConfirmationLabel = "Confirm Delete"
				}
			}).Append(new WizardOption("template", "Create from Template", "Import a prepared context ZIP archive.")).ToArray();
			var selected = await wizard.ShowAsync(new WizardPage("Select a context",
				"Choose a context for the new session, delete a saved context, or import a template.", options)
			{
				SelectedKey = selectedKey, Error = error
			}, cancellationToken);
			if (selected == "template")
			{
				return await ImportTemplateAsync(cancellationToken);
			}
			var context = contexts.FirstOrDefault(item => item.Id.ToString(CultureInfo.InvariantCulture) == selected);
			if (context != null)
			{
				return context.Id;
			}
			var deleted = contexts.First(item => "delete:" + item.Id.ToString(CultureInfo.InvariantCulture) == selected);
			try
			{
				await database.DeleteContextAsync(deleted.Id, cancellationToken);
				logger.LogInformation("Deleted context: {Context}.", deleted.Name);
				error = string.Empty;
				selectedKey = null;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to delete context {ContextId}.", deleted.Id);
				error = exception.Message;
				selectedKey = deleted.Id.ToString(CultureInfo.InvariantCulture);
			}
		}
	}

	private async Task<int> ImportTemplateAsync(CancellationToken cancellationToken)
	{
		var path = string.Empty;
		var error = string.Empty;
		while (true)
		{
			path = await wizard.ShowAsync(new WizardPage("Import a context template",
				"Select a ZIP archive containing preview.txt, agent_initial_instructions.txt, and a context folder.", [])
			{
				IsTemplatePath = true, Path = path, Error = error
			}, cancellationToken);
			path = path.Trim();
			if (path.Length >= 2 && ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
			{
				path = path[1..^1];
			}
			try
			{
				return (await database.CreateContextAsync(path, cancellationToken: cancellationToken)).Id;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to import the context template.");
				error = exception.Message;
			}
		}
	}
}
