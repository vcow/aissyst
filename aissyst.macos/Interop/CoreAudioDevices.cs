using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using aissyst.core.Audio;
using aissyst.macos.Audio;

namespace aissyst.macos.Interop;

[SupportedOSPlatform("macos")]
internal static class CoreAudioDevices
{
	private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const uint SystemObject = 1;
	private const uint GlobalScope = 0x676C6F62; // 'glob'
	private const uint InputScope = 0x696E7074; // 'inpt'
	private const uint DevicesProperty = 0x64657623; // 'dev#'
	private const uint DefaultInputProperty = 0x64496E20; // 'dIn '
	private const uint StreamConfigurationProperty = 0x736C6179; // 'slay'
	private const uint NameProperty = 0x6C6E616D; // 'lnam'
	private const uint UidProperty = 0x75696420; // 'uid '
	private const uint ProcessesProperty = 0x70727323; // 'prs#'
	private const uint ProcessIdProperty = 0x70706964; // 'ppid'
	private const uint ProcessBundleIdProperty = 0x70626964; // 'pbid'
	private const uint ProcessOutputActiveProperty = 0x7069726F; // 'piro'
	private const uint Utf8Encoding = 0x08000100;
	private const int BadPropertySize = 0x2173697A; // '!siz'
	private const int BadObject = 0x216F626A; // '!obj'
	private const int BadDevice = 0x21646576; // '!dev'

	internal static IReadOnlyList<AudioInputDevice> GetInputDevices(CancellationToken cancellationToken)
	{
		var defaultDevice = BitConverter.ToUInt32(ReadProperty(SystemObject, DefaultInputProperty));
		var deviceIds = ReadProperty(SystemObject, DevicesProperty);
		var devices = new List<AudioInputDevice>();

		for (var offset = 0; offset < deviceIds.Length; offset += sizeof(uint))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var deviceId = BitConverter.ToUInt32(deviceIds, offset);
			try
			{
				var channels = GetInputChannels(deviceId);
				if (channels == 0)
					continue;

				devices.Add(new AudioInputDevice(
					ReadString(deviceId, UidProperty),
					ReadString(deviceId, NameProperty),
					channels,
					deviceId == defaultDevice));
			}
			catch (CoreAudioException exception) when (exception.Status is BadObject or BadDevice)
			{
				// The device may have disconnected after its ID was enumerated.
			}
		}

		return devices.OrderByDescending(device => device.isDefault)
			.ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	[SupportedOSPlatform("macos14.2")]
	internal static IReadOnlyList<AudioProcessInfo> GetAudioProcesses(CancellationToken cancellationToken)
	{
		var processObjects = ReadProperty(SystemObject, ProcessesProperty);
		var processes = new List<AudioProcessInfo>();
		for (var offset = 0; offset < processObjects.Length; offset += sizeof(uint))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var objectId = BitConverter.ToUInt32(processObjects, offset);
			try
			{
				var pid = BitConverter.ToInt32(ReadProperty(objectId, ProcessIdProperty));
				if (pid <= 0 || pid == Environment.ProcessId)
					continue;

				processes.Add(new AudioProcessInfo(
					pid,
					ReadString(objectId, ProcessBundleIdProperty, allowNull: true),
					BitConverter.ToUInt32(ReadProperty(objectId, ProcessOutputActiveProperty)) != 0));
			}
			catch (CoreAudioException exception) when (exception.Status is BadObject or BadDevice)
			{
				// The process may have exited after its audio object was enumerated.
			}
		}

		return processes;
	}

	private static int GetInputChannels(uint deviceId)
	{
		var data = ReadProperty(deviceId, StreamConfigurationProperty, InputScope);
		var bufferCount = BitConverter.ToUInt32(data);
		// AudioBufferList includes padding before the first AudioBuffer on 64-bit platforms.
		var firstBufferOffset = Marshal.OffsetOf<AudioBufferList>(nameof(AudioBufferList.FirstBuffer)).ToInt32();
		var bufferSize = Marshal.SizeOf<AudioBuffer>();
		if ((long)firstBufferOffset + (long)bufferCount * bufferSize > data.Length)
			throw new InvalidOperationException("Core Audio returned an invalid input channel buffer list.");

		var channels = 0;
		for (var index = 0; index < bufferCount; index++)
			channels = checked(channels + (int)BitConverter.ToUInt32(data, firstBufferOffset + index * bufferSize));
		return channels;
	}

	private static string ReadString(uint deviceId, uint selector, bool allowNull = false)
	{
		var data = ReadProperty(deviceId, selector);
		var value = (nint)BitConverter.ToInt64(data);
		if (value == 0)
		{
			if (allowNull)
				return string.Empty;
			throw new InvalidOperationException("Core Audio returned a null object string reference.");
		}

		try
		{
			var capacity = checked((int)CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), Utf8Encoding) + 1);
			var buffer = new byte[capacity];
			if (!CFStringGetCString(value, buffer, buffer.Length, Utf8Encoding))
				throw new InvalidOperationException("Failed to read a Core Audio object string.");
			return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
		}
		finally
		{
			// The caller owns the CFString returned by the Name, DeviceUID, and ProcessBundleID properties.
			CFRelease(value);
		}
	}

	private static byte[] ReadProperty(uint objectId, uint selector, uint scope = GlobalScope)
	{
		var address = new AudioObjectPropertyAddress { Selector = selector, Scope = scope };
		for (var attempt = 0; attempt < 3; attempt++)
		{
			CheckStatus(AudioObjectGetPropertyDataSize(objectId, ref address, 0, 0, out var size), objectId, selector);
			if (size == 0)
				return [];

			var data = new byte[checked((int)size)];
			var status = AudioObjectGetPropertyData(objectId, ref address, 0, 0, ref size, data);
			if (status == BadPropertySize && attempt < 2)
				continue; // The device or channel list may have changed between the two calls.
			CheckStatus(status, objectId, selector);
			Array.Resize(ref data, checked((int)size));
			return data;
		}

		throw new InvalidOperationException("Failed to read a stable Core Audio property value.");
	}

	private static void CheckStatus(int status, uint objectId, uint selector)
	{
		if (status != 0)
			throw new CoreAudioException(status, objectId, selector);
	}

	private sealed class CoreAudioException(int status, uint objectId, uint selector)
		: InvalidOperationException($"Core Audio error: OSStatus={status} (0x{status:X8}), object={objectId}, property=0x{selector:X8}.")
	{
		internal int Status { get; } = status;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AudioObjectPropertyAddress
	{
		public uint Selector;
		public uint Scope;
		public uint Element;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AudioBuffer
	{
		public uint Channels;
		public uint DataByteSize;
		public nint Data;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AudioBufferList
	{
		public uint Count;
		public AudioBuffer FirstBuffer;
	}

	[DllImport(CoreAudio)]
	private static extern int AudioObjectGetPropertyDataSize(uint objectId, ref AudioObjectPropertyAddress address,
		uint qualifierDataSize, nint qualifierData, out uint dataSize);

	[DllImport(CoreAudio)]
	private static extern int AudioObjectGetPropertyData(uint objectId, ref AudioObjectPropertyAddress address,
		uint qualifierDataSize, nint qualifierData, ref uint dataSize, [Out] byte[] data);

	[DllImport(CoreFoundation)]
	private static extern nint CFStringGetLength(nint value);

	[DllImport(CoreFoundation)]
	private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

	[DllImport(CoreFoundation)]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint bufferSize, uint encoding);

	[DllImport(CoreFoundation)]
	private static extern void CFRelease(nint value);
}
