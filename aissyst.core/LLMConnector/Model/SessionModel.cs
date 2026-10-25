using aissyst.core.Database;
using aissyst.core.LLMConnector.DTO;
using aissyst.core.Processor;

namespace aissyst.core.LLMConnector.Model;

public class SessionModel
{
	private readonly IDatabase _database;
	private readonly ITextProcessor _textProcessor;
	private readonly ILLMConnector _llmConnector;

	private string _summary;
	private DateTime _summaryTimestamp;

	public SessionModel(IDatabase database, ITextProcessor textProcessor, ILLMConnector llmConnector)
	{
		_database = database;
		_textProcessor = textProcessor;
		_llmConnector = llmConnector;
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		var session = GetSession();
		var response = await _llmConnector.InitializeSession(session);
		Reset(response);
	}

	public void Reset(DialogResponse initialResponse)
	{
		throw new NotImplementedException();
	}

	public Session GetSession()
	{
		throw new NotImplementedException();
	}

	public DialogRequest GetRequest(RequestType requestType)
	{
		throw new NotImplementedException();
	}
}