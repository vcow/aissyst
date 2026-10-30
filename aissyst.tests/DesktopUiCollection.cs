namespace aissyst.tests;

// Avalonia sessions share process-wide services and must not run concurrently.
[CollectionDefinition("Desktop UI")]
public sealed class DesktopUiCollection;
