using aissyst.core.Audio;

namespace aissyst.macos.Audio;

internal sealed record AudioProcessInfo(
	int ProcessId,
	string BundleId,
	bool IsOutputActive,
	string? ExecutablePath = null);

internal sealed record RunningApplicationInfo(
	int ProcessId,
	string Name,
	string? BundleId,
	string? BundlePath);

internal static class AudioApplicationGroups
{
	internal static IReadOnlyList<AudioApplication> Create(
		IReadOnlyList<AudioProcessInfo> audioProcesses,
		IReadOnlyList<RunningApplicationInfo> runningApplications)
	{
		var applicationsByPid = runningApplications.ToDictionary(application => application.ProcessId);
		var ownersByPath = runningApplications
			.Where(application => application.BundlePath != null
			                      && GetRootApplicationPath(application.BundlePath) == application.BundlePath.TrimEnd('/'))
			.GroupBy(application => application.BundlePath!.TrimEnd('/'), StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.OrderBy(application => application.ProcessId).First(), StringComparer.Ordinal);
		var ownersByBundle = ownersByPath.Values
			.Where(application => !string.IsNullOrEmpty(application.BundleId))
			.GroupBy(application => application.BundleId!, StringComparer.Ordinal)
			.Where(group => group.Count() == 1)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

		var entries = audioProcesses.Select(process =>
		{
			applicationsByPid.TryGetValue(process.ProcessId, out var application);
			var rootPath = GetRootApplicationPath(application?.BundlePath)
			               ?? GetRootApplicationPath(process.ExecutablePath);
			RunningApplicationInfo? owner = null;
			if (rootPath != null)
				ownersByPath.TryGetValue(rootPath, out owner);
			else if (ownersByBundle.TryGetValue(process.BundleId, out owner))
				rootPath = owner.BundlePath!.TrimEnd('/');

			// Nested helper bundles belong to the outermost .app, not to a separate selectable source.
			// Shared XPC services can have the same bundle ID while serving different applications.
			// Without an identifiable owning .app, keep those processes separate.
			var key = rootPath != null ? $"path:{rootPath}" : $"pid:{process.ProcessId}";
			var name = owner?.Name
			           ?? (rootPath != null ? Path.GetFileNameWithoutExtension(rootPath) : application?.Name)
			           ?? (!string.IsNullOrEmpty(process.BundleId) ? process.BundleId : $"Process {process.ProcessId}");
			return new { Key = key, Name = name, OwnerId = owner?.ProcessId, Process = process };
		});

		return entries.GroupBy(entry => entry.Key, StringComparer.Ordinal)
			.Select(group => new AudioApplication(
				group.First().OwnerId ?? group.Min(entry => entry.Process.ProcessId),
				group.First().Name,
				group.Any(entry => entry.Process.IsOutputActive),
				group.Select(entry => entry.Process.ProcessId).Distinct().Order().ToArray()))
			.OrderBy(application => application.Name, new AudioApplicationGroupsComparer())
			.ThenBy(application => application.Id)
			.ToArray();
	}

	private static string? GetRootApplicationPath(string? path)
	{
		if (string.IsNullOrEmpty(path))
			return null;

		var index = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
		if (index >= 0)
			return path[..(index + 4)];

		return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? path : null;
	}

	private class AudioApplicationGroupsComparer : IComparer<string>
	{
		public int Compare(string? x, string? y)
		{
			if (!string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y))
			{
				const string process = "Process ";
				var xStartsWithProcess = x.StartsWith(process);
				var yStartsWithProcess = y.StartsWith(process);
				if (xStartsWithProcess && !yStartsWithProcess) return 1;
				if (!xStartsWithProcess && yStartsWithProcess) return -1;

				const string comApple = "com.apple.";
				var xStartsWithComApple = x.StartsWith(comApple);
				var yStartsWithComApple = y.StartsWith(comApple);
				if (xStartsWithComApple && !yStartsWithComApple) return 1;
				if (!xStartsWithComApple && yStartsWithComApple) return -1;
			}

			return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
		}
	}
}