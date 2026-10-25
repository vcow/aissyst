namespace aissyst.core.Database;

public sealed record SessionContext(
	SessionInfo SessionInfo,
	string Preview,
	IReadOnlyList<ContextRecord> Context,
	IReadOnlyList<DialogRecord> Dialog);