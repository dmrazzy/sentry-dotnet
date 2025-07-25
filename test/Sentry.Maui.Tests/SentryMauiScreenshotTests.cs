using Microsoft.Extensions.Options;

namespace Sentry.Maui.Tests;

public class SentryMauiScreenshotTests
{
    private class Fixture
    {
        public MauiAppBuilder Builder { get; }
        public FakeTransport Transport { get; private set; } = new FakeTransport();
        public InMemoryDiagnosticLogger Logger { get; private set; } = new InMemoryDiagnosticLogger();

        public Fixture()
        {
            var builder = MauiApp.CreateBuilder();
            builder.Services.AddSingleton(Substitute.For<IApplication>());

            builder.Services.Configure<SentryMauiOptions>(options =>
            {
                options.Transport = Transport;
                options.Dsn = ValidDsn;
                options.AttachScreenshot = true;
                options.Debug = true;
                options.DiagnosticLogger = Logger;
                options.AutoSessionTracking = false; //Get rid of session envelope for easier Assert
                options.CacheDirectoryPath = null;   //Do not wrap our FakeTransport with a caching transport
                options.FlushTimeout = TimeSpan.FromSeconds(10);
            });

            Builder = builder;
        }
    }

    private readonly Fixture _fixture = new();

#if __MOBILE__
    // TODO: This test doesn't work on Android or iOS, so isn't much use. We should consider replacing it with some
    // more granular tests that test SentryMauiOptionsSetup, SentryMauiScreenshotProcessor and ScreenshotAttachment
    // in isolation. It's generally hard to test any of this functionality since ScreenshotImplementation relies of
    // various static members like ActivityStateManager.Default:
    // https://github.com/dotnet/maui/blob/3c7b65264d2f341a48db32263a271fd8718cfd23/src/Essentials/src/Screenshot/Screenshot.android.cs#L28
    [SkippableFact]
    public async Task CaptureException_WhenAttachScreenshots_ContainsScreenshotAttachmentAsync()
    {
#if __IOS__
        Skip.If(true, "Flaky on iOS");
#endif
#if ANDROID
        Skip.If(true, "Doesn't work on Android");
#endif

        // Arrange
        var builder = _fixture.Builder.UseSentry();

        // Act
        await using var app = builder.Build();
        var client = app.Services.GetRequiredService<ISentryClient>();
        var sentryId = client.CaptureException(new Exception());
        await client.FlushAsync();

        var options = app.Services.GetRequiredService<IOptions<SentryMauiOptions>>().Value;

        var envelope = _fixture.Transport.GetSentEnvelopes().FirstOrDefault(e => e.TryGetEventId() == sentryId);
        envelope.Should().NotBeNull("Envelope with sentryId {0} should be sent", sentryId);

        var envelopeItem = envelope!.Items.FirstOrDefault(item => item.TryGetType() == "attachment");

        // Assert
        // On Android this can fail due to MAUI not being able to detect current Activity, see issue https://github.com/dotnet/maui/issues/19450
        if (_fixture.Logger.Entries.Any(entry => entry.Level == SentryLevel.Error && entry.Exception is NullReferenceException))
        {
            envelopeItem.Should().BeNull();
        }
        else
        {
            envelopeItem.Should().NotBeNull();
            envelopeItem!.TryGetFileName().Should().Be("screenshot.jpg");
        }
    }

    [SkippableFact]
    public async Task CaptureException_RemoveScreenshot_NotContainsScreenshotAttachmentAsync()
    {
#if __IOS__
        Skip.If(true, "Flaky on iOS");
#endif

        // Arrange
        var builder = _fixture.Builder.UseSentry(options => options.SetBeforeSend((e, hint) =>
            {
                hint.Attachments.Clear();
                return e;
            }
        ));

        // Act
        await using var app = builder.Build();
        var client = app.Services.GetRequiredService<ISentryClient>();
        var sentryId = client.CaptureException(new Exception());
        await client.FlushAsync();

        var envelope = _fixture.Transport.GetSentEnvelopes().FirstOrDefault(e => e.TryGetEventId() == sentryId);
        envelope.Should().NotBeNull();

        var envelopeItem = envelope!.Items.FirstOrDefault(item => item.TryGetType() == "attachment");

        // Assert
        envelopeItem.Should().BeNull();
    }

    [SkippableFact]
    public async Task CaptureException_BeforeCaptureScreenshot_DisableCaptureAsync()
    {
#if __IOS__
        Skip.If(true, "Flaky on iOS");
#endif

        // Arrange
        var builder = _fixture.Builder.UseSentry(options => options.SetBeforeScreenshotCapture(
            (_, _) => false
        ));

        // Act
        await using var app = builder.Build();
        var client = app.Services.GetRequiredService<ISentryClient>();
        var sentryId = client.CaptureException(new Exception());
        await client.FlushAsync();

        var envelope = _fixture.Transport.GetSentEnvelopes().FirstOrDefault(e => e.TryGetEventId() == sentryId);
        envelope.Should().NotBeNull();

        var envelopeItem = envelope!.Items.FirstOrDefault(item => item.TryGetType() == "attachment");

        // Assert
        envelopeItem.Should().BeNull();
    }

    // TODO: This test doesn't work on Android or iOS, so isn't much use. We should consider replacing it with some
    // more granular tests that test SentryMauiOptionsSetup, SentryMauiScreenshotProcessor and ScreenshotAttachment
    // in isolation. It's generally hard to test any of this functionality since ScreenshotImplementation relies of
    // various static members like ActivityStateManager.Default:
    // https://github.com/dotnet/maui/blob/3c7b65264d2f341a48db32263a271fd8718cfd23/src/Essentials/src/Screenshot/Screenshot.android.cs#L28
    [SkippableFact]
    public async Task CaptureException_BeforeCaptureScreenshot_DefaultAsync()
    {
#if __IOS__
        Skip.If(true, "Flaky on iOS");
#endif
#if ANDROID
        Skip.If(true, "Doesn't work on Android");
#endif

        // Arrange
        var builder = _fixture.Builder.UseSentry(options => options.SetBeforeScreenshotCapture(
            (_, _) => true
        ));

        // Act
        using var app = builder.Build();
        var client = app.Services.GetRequiredService<ISentryClient>();
        var sentryId = client.CaptureException(new Exception());
        await client.FlushAsync();

        var options = app.Services.GetRequiredService<IOptions<SentryMauiOptions>>().Value;

        var envelope = _fixture.Transport.GetSentEnvelopes().FirstOrDefault(e => e.TryGetEventId() == sentryId);
        envelope.Should().NotBeNull("Envelope with sentryId {0} should be sent", sentryId);

        var envelopeItem = envelope!.Items.FirstOrDefault(item => item.TryGetType() == "attachment");

        // Assert
        // On Android this can fail due to MAUI not being able to detect current Activity, see issue https://github.com/dotnet/maui/issues/19450
        if (_fixture.Logger.Entries.Any(entry => entry.Level == SentryLevel.Error && entry.Exception is NullReferenceException))
        {
            envelopeItem.Should().BeNull();
        }
        else
        {
            envelopeItem.Should().NotBeNull();
            envelopeItem!.TryGetFileName().Should().Be("screenshot.jpg");
        }
    }

    [SkippableFact]
    public async Task CaptureException_AttachScreenshot_Threadsafe()
    {
#if ANDROID
         Skip.If(TestEnvironment.IsGitHubActions, "Flaky in CI on Android");
#endif
        // Arrange
        var builder = _fixture.Builder.UseSentry(options => options.AttachScreenshot = true);
        await using var app = builder.Build();
        var client = app.Services.GetRequiredService<ISentryClient>();
        var startSignal = new ManualResetEventSlim(false);

        var tasks = new List<Task<SentryId>>();
        for (var i = 0; i < 20; i++)
        {
            var j = i;
            tasks.Add(Task.Run(async () =>
            {
                startSignal.Wait(); // Make sure all the tasks start at the same time
                var exSample = new NotImplementedException("Sample Exception " + j);
                var sentryId = client.CaptureException(exSample);
                await client.FlushAsync(TimeSpan.FromSeconds(5));
                return sentryId;
            }));
        }

        // Act
        await Task.Delay(50); // Wait for all of the tasks to be ready
        startSignal.Set();
        await Task.WhenAll(tasks);

        // Assert
        foreach (var task in tasks)
        {
            task.Status.Should().Be(TaskStatus.RanToCompletion, $"Task should complete successfully. Status: {task.Status}");
            task.Exception.Should().BeNull("No unhandled exceptions should occur during concurrent capture.");
        }
    }
#endif
}
