namespace aissyst.core.Database;

public record SessionInfo(
	int Id,
	int contextId,
	DateTime creationTime,
	string Notes);