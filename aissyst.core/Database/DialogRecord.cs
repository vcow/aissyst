namespace aissyst.core.Database;

public record DialogRecord(
	DateTime Timestamp,
	string Role,
	string Message);