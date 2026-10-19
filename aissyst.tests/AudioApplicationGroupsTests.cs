using aissyst.macos.Audio;

namespace aissyst.tests;

public class AudioApplicationGroupsTests
{
	[Fact]
	public void NestedBrowserHelpersBecomeOneSourceWithAllAudioProcesses()
	{
		var applications = AudioApplicationGroups.Create(
			[
				new(20, "org.mozilla.plugincontainer", false, "/Applications/Firefox.app/Contents/MacOS/plugin-container.app/Contents/MacOS/plugin-container"),
				new(21, "org.mozilla.plugincontainer", true, "/Applications/Firefox.app/Contents/MacOS/plugin-container.app/Contents/MacOS/plugin-container")
			],
			[new(10, "Firefox", "org.mozilla.firefox", "/Applications/Firefox.app")]);

		var application = Assert.Single(applications);
		Assert.Equal("Firefox", application.Name);
		Assert.Equal(10, application.Id);
		Assert.True(application.IsOutputActive);
		Assert.Equal(new[] { 20, 21 }, application.ProcessIds);
	}

	[Fact]
	public void IdleAudioClientsAreRetainedButUnrelatedRunningAppsAreExcluded()
	{
		var applications = AudioApplicationGroups.Create(
			[new(10, "org.telegram", false)],
			[
				new(10, "Telegram", "org.telegram", "/Applications/Telegram.app"),
				new(11, "Sourcetree", "com.sourcetree", "/Applications/Sourcetree.app")
			]);

		var application = Assert.Single(applications);
		Assert.Equal("Telegram", application.Name);
		Assert.False(application.IsOutputActive);
	}

	[Fact]
	public void IdenticalDisplayNamesDoNotMergeDifferentApplications()
	{
		var applications = AudioApplicationGroups.Create(
			[new(10, "example.one", false), new(11, "example.two", false)],
			[
				new(10, "Player", "example.one", "/Applications/One.app"),
				new(11, "Player", "example.two", "/Applications/Two.app")
			]);

		Assert.Equal(2, applications.Count);
	}

	[Fact]
	public void SharedXpcBundleIdDoesNotMergeServicesFromDifferentApplications()
	{
		const string helperPath = "/System/Library/Frameworks/WebKit.framework/XPCServices/com.apple.WebKit.GPU.xpc";
		var applications = AudioApplicationGroups.Create(
			[new(20, "com.apple.WebKit.GPU", true, helperPath), new(21, "com.apple.WebKit.GPU", false, helperPath)],
			[
				new(20, "App One Graphics and Media", "com.apple.WebKit.GPU", helperPath),
				new(21, "App Two Graphics and Media", "com.apple.WebKit.GPU", helperPath)
			]);

		Assert.Equal(2, applications.Count);
		Assert.Contains(applications, application => application.Name == "App One Graphics and Media" && application.IsOutputActive);
		Assert.Contains(applications, application => application.Name == "App Two Graphics and Media" && !application.IsOutputActive);
	}

	[Fact]
	public void UniqueApplicationBundleIdResolvesAnAudioProcessWithoutAPath()
	{
		var application = Assert.Single(AudioApplicationGroups.Create(
			[new(20, "example.player", false)],
			[new(10, "Player", "example.player", "/Applications/Player.app")]));

		Assert.Equal(10, application.Id);
		Assert.Equal("Player", application.Name);
		Assert.Equal(new[] { 20 }, application.ProcessIds);
	}

	[Fact]
	public void UnknownProcessesRemainSeparateWithoutBundleIdentifiers()
	{
		var applications = AudioApplicationGroups.Create(
			[new(20, "", false), new(21, "", false)], []);

		Assert.Equal(2, applications.Count);
		Assert.Contains(applications, application => application.Name == "Process 20");
		Assert.Contains(applications, application => application.Name == "Process 21");
	}
}
