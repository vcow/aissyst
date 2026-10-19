using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using aissyst.core.Audio;
using aissyst.macos.Audio;

namespace aissyst.macos.Interop;

[SupportedOSPlatform("macos")]
internal static class MacApplications
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static readonly nint SharedWorkspace = sel_registerName("sharedWorkspace");
	private static readonly nint RunningApplications = sel_registerName("runningApplications");
	private static readonly nint Count = sel_registerName("count");
	private static readonly nint ObjectAtIndex = sel_registerName("objectAtIndex:");
	private static readonly nint ProcessIdentifier = sel_registerName("processIdentifier");
	private static readonly nint LocalizedName = sel_registerName("localizedName");
	private static readonly nint BundleIdentifier = sel_registerName("bundleIdentifier");
	private static readonly nint BundleUrl = sel_registerName("bundleURL");
	private static readonly nint PathSelector = sel_registerName("path");
	private static readonly nint Utf8String = sel_registerName("UTF8String");

	static MacApplications()
	{
		// Keep AppKit loaded for the lifetime of the process so its Objective-C classes remain available.
		NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
	}

	[SupportedOSPlatform("macos14.2")]
	internal static IReadOnlyList<AudioApplication> GetApplications(CancellationToken cancellationToken)
	{
		var audioProcesses = CoreAudioDevices.GetAudioProcesses(cancellationToken);
		var runningApplications = GetRunningApplications(cancellationToken);
		var processesWithPaths = new List<AudioProcessInfo>();
		foreach (var process in audioProcesses)
		{
			cancellationToken.ThrowIfCancellationRequested();
			processesWithPaths.Add(process with { ExecutablePath = GetExecutablePath(process.ProcessId) });
		}

		return AudioApplicationGroups.Create(processesWithPaths, runningApplications);
	}

	private static IReadOnlyList<RunningApplicationInfo> GetRunningApplications(CancellationToken cancellationToken)
	{
		var pool = objc_autoreleasePoolPush();
		try
		{
			var workspaceClass = objc_getClass("NSWorkspace");
			if (workspaceClass == 0)
				throw new InvalidOperationException("The NSWorkspace class is not available.");

			var workspace = SendPointer(workspaceClass, SharedWorkspace);
			var runningApplications = SendPointer(workspace, RunningApplications);
			if (workspace == 0 || runningApplications == 0)
				throw new InvalidOperationException("Failed to obtain the running application list from NSWorkspace.");

			var count = SendUnsigned(runningApplications, Count);
			var applications = new List<RunningApplicationInfo>();
			for (nuint index = 0; index < count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var application = SendPointerAtIndex(runningApplications, ObjectAtIndex, index);
				var pid = SendInt32(application, ProcessIdentifier);
				if (pid <= 0)
					continue; // Applications without a live process cannot be selected for audio capture.

				var name = ReadString(SendPointer(application, LocalizedName))
					?? ReadString(SendPointer(application, BundleIdentifier))
					?? $"Process {pid}";
				var bundleId = ReadString(SendPointer(application, BundleIdentifier));
				var bundleUrl = SendPointer(application, BundleUrl);
				var bundlePath = ReadString(SendPointer(bundleUrl, PathSelector));
				applications.Add(new RunningApplicationInfo(pid, name, bundleId, bundlePath));
			}

			return applications;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	private static string? ReadString(nint value)
	{
		return value == 0 ? null : Marshal.PtrToStringUTF8(SendPointer(value, Utf8String));
	}

	private static string? GetExecutablePath(int pid)
	{
		var buffer = new byte[4096]; // PROC_PIDPATHINFO_MAXSIZE
		if (proc_pidpath(pid, buffer, (uint)buffer.Length) <= 0)
			return null; // A process may exit or deny access to its executable path.

		var length = Array.IndexOf(buffer, (byte)0);
		return length >= 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
	}

	[DllImport("/usr/lib/libproc.dylib")]
	private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);

	[DllImport(ObjectiveC)]
	private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(ObjectiveC)]
	private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(ObjectiveC)]
	private static extern nint objc_autoreleasePoolPush();

	[DllImport(ObjectiveC)]
	private static extern void objc_autoreleasePoolPop(nint pool);

	[DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static extern nint SendPointer(nint receiver, nint selector);

	[DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static extern nuint SendUnsigned(nint receiver, nint selector);

	[DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static extern int SendInt32(nint receiver, nint selector);

	[DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static extern nint SendPointerAtIndex(nint receiver, nint selector, nuint index);
}
