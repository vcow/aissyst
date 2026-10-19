using aissyst.console;
using aissyst.core.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aissyst.tests;

public class CaptureSessionTests
{
	private static readonly AudioInputDevice Microphone = new("test-microphone", "Test microphone", 1, true);
	private static readonly AudioApplication Application = new(123, "Test application", false, new[] { 123 });

	[Fact]
	public async Task DisposingScopeStopsSessionAndNewScopeAllowsRestartWithoutStoppingHost()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var host = CreateHost();
		await host.StartAsync(timeout.Token);
		try
		{
			CaptureSession? previousSession = null;
			CaptureSessionContext? previousContext = null;
			for (var index = 0; index < 2; index++)
			{
				CaptureSession session;
				await using (var scope = host.Services.CreateAsyncScope())
				{
					var context = scope.ServiceProvider.GetRequiredService<CaptureSessionContext>();
					context.Initialize(Microphone, Application);
					session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
					Assert.Same(context, session.Context);
					Assert.Same(session, scope.ServiceProvider.GetRequiredService<CaptureSession>());
					Assert.NotSame(previousSession, session);
					Assert.NotSame(previousContext, context);
					session.Start();
					Assert.False(session.Completion.IsCompleted);
					previousSession = session;
					previousContext = context;
				}

				await session.Completion.WaitAsync(timeout.Token);
				Assert.True(session.Completion.IsCompletedSuccessfully);
				Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
			}
		}
		finally
		{
			await host.StopAsync(timeout.Token);
		}
	}

	[Fact]
	public async Task SeparateScopesHaveIndependentSourcesAndSessionLifetimes()
	{
		using var host = CreateHost();
		await using var firstScope = host.Services.CreateAsyncScope();
		await using var secondScope = host.Services.CreateAsyncScope();
		var otherMicrophone = new AudioInputDevice("other-microphone", "Other microphone", 2, false);
		var otherApplication = new AudioApplication(456, "Other application", true, new[] { 456 });
		firstScope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
		secondScope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(otherMicrophone, otherApplication);
		var first = firstScope.ServiceProvider.GetRequiredService<CaptureSession>();
		var second = secondScope.ServiceProvider.GetRequiredService<CaptureSession>();
		first.Start();
		second.Start();

		Assert.Same(Microphone, first.Context.Microphone);
		Assert.Same(Application, first.Context.Application);
		Assert.Same(otherMicrophone, second.Context.Microphone);
		Assert.Same(otherApplication, second.Context.Application);
		await firstScope.DisposeAsync();
		Assert.True(first.Completion.IsCompletedSuccessfully);
		Assert.False(second.Completion.IsCompleted);
	}

	[Fact]
	public async Task HostShutdownStopsSessionBeforeItsScopeIsDisposed()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var host = CreateHost();
		await host.StartAsync(timeout.Token);
		try
		{
			await using var scope = host.Services.CreateAsyncScope();
			scope.ServiceProvider.GetRequiredService<CaptureSessionContext>().Initialize(Microphone, Application);
			var session = scope.ServiceProvider.GetRequiredService<CaptureSession>();
			session.Start();
			Assert.False(session.Completion.IsCompleted);

			await host.StopAsync(timeout.Token);
			await session.Completion.WaitAsync(timeout.Token);
			Assert.True(session.Completion.IsCompletedSuccessfully);
		}
		finally
		{
			await host.StopAsync(timeout.Token);
		}
	}

	private static IHost CreateHost()
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddScoped<CaptureSessionContext>();
		builder.Services.AddScoped<CaptureSession>();
		return builder.Build();
	}
}
