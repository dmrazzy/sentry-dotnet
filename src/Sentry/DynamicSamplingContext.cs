using Sentry.Internal;
using Sentry.Internal.Extensions;

namespace Sentry;

/// <summary>
/// Provides the Dynamic Sampling Context for a transaction.
/// </summary>
/// <seealso href="https://develop.sentry.dev/sdk/performance/dynamic-sampling-context"/>
internal class DynamicSamplingContext
{
    public IReadOnlyDictionary<string, string> Items { get; }

    public bool IsEmpty => Items.Count == 0;

    private DynamicSamplingContext(IReadOnlyDictionary<string, string> items) => Items = items;

    /// <summary>
    /// Gets an empty <see cref="DynamicSamplingContext"/> that can be used to "freeze" the DSC on a transaction.
    /// </summary>
    public static readonly DynamicSamplingContext Empty = new(new Dictionary<string, string>().AsReadOnly());

    private DynamicSamplingContext(SentryId traceId,
        string publicKey,
        bool? sampled,
        double? sampleRate = null,
        double? sampleRand = null,
        string? release = null,
        string? environment = null,
        string? transactionName = null,
        IReplaySession? replaySession = null)
    {
        // Validate and set required values
        if (traceId == SentryId.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(traceId), "cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("cannot be empty", nameof(publicKey));
        }

        if (sampleRate is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Arg invalid if < 0.0 or > 1.0");
        }

        if (sampleRand is < 0.0 or >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRand), "Arg invalid if < 0.0 or >= 1.0");
        }

        var items = new Dictionary<string, string>(capacity: 9)
        {
            ["trace_id"] = traceId.ToString(),
            ["public_key"] = publicKey,
        };

        // Set optional values
        if (sampled.HasValue)
        {
            items.Add("sampled", sampled.Value ? "true" : "false");
        }

        if (sampleRate is not null)
        {
            items.Add("sample_rate", sampleRate.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (sampleRand is not null)
        {
            items.Add("sample_rand", sampleRand.Value.ToString("N4", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(release))
        {
            items.Add("release", release);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            items.Add("environment", environment);
        }

        if (!string.IsNullOrWhiteSpace(transactionName))
        {
            items.Add("transaction", transactionName);
        }

        if (replaySession?.ActiveReplayId is { } replayId && replayId != SentryId.Empty)
        {
            items.Add("replay_id", replayId.ToString());
        }

        Items = items;
    }

    public BaggageHeader ToBaggageHeader() => BaggageHeader.Create(Items, useSentryPrefix: true);

    public DynamicSamplingContext WithSampleRate(double sampleRate)
    {
        if (Items.TryGetValue("sample_rate", out var dscSampleRate))
        {
            if (double.TryParse(dscSampleRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            {
                if (Math.Abs(rate - sampleRate) > double.Epsilon)
                {
                    var items = Items.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    items["sample_rate"] = sampleRate.ToString(CultureInfo.InvariantCulture);
                    return new DynamicSamplingContext(items);
                }
            }
        }

        return this;
    }

    public DynamicSamplingContext WithReplayId(IReplaySession? replaySession)
    {
        if (replaySession?.ActiveReplayId is not { } replayId || replayId == SentryId.Empty)
        {
            return this;
        }

        var items = Items.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        items["replay_id"] = replayId.ToString();
        return new DynamicSamplingContext(items);
    }

    public static DynamicSamplingContext? CreateFromBaggageHeader(BaggageHeader baggage, IReplaySession? replaySession)
    {
        var items = baggage.GetSentryMembers();

        // The required items must exist and be valid to create the DSC from baggage.
        // Return null if they are not, so we know we should create it from the transaction instead.

        if (!items.TryGetValue("trace_id", out var traceId) ||
            !Guid.TryParse(traceId, out var id) || id == Guid.Empty)
        {
            return null;
        }

        if (!items.TryGetValue("public_key", out var publicKey) || string.IsNullOrWhiteSpace(publicKey))
        {
            return null;
        }

        if (items.TryGetValue("sampled", out var sampledString) && !bool.TryParse(sampledString, out var sampled))
        {
            return null;
        }

        if (!items.TryGetValue("sample_rate", out var sampleRate) ||
            !double.TryParse(sampleRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            rate is < 0.0 or > 1.0)
        {
            return null;
        }

        // See https://develop.sentry.dev/sdk/telemetry/traces/#propagated-random-value
        if (items.TryGetValue("sample_rand", out var sampleRand))
        {
            if (!double.TryParse(sampleRand, NumberStyles.Float, CultureInfo.InvariantCulture, out var rand) ||
                 rand is < 0.0 or >= 1.0)
            {
                return null;
            }
        }
        else
        {
            var rand = SampleRandHelper.GenerateSampleRand(traceId);
            if (!string.IsNullOrEmpty(sampledString))
            {
                // Ensure sample_rand is consistent with the sampling decision that has already been made
                rand = bool.Parse(sampledString)
                    ? rand * rate // 0 <= sampleRand < rate
                    : rate + (1 - rate) * rand; // rate < sampleRand < 1
            }
            items.Add("sample_rand", rand.ToString("N4", CultureInfo.InvariantCulture));
        }

        if (replaySession?.ActiveReplayId is { } replayId)
        {
            // Any upstream replay_id will be propagated only if the current process hasn't started it's own replay session.
            // Otherwise we have to overwrite this as it's the only way to communicate the replayId to Sentry Relay.
            // In Mobile apps this should never be a problem.
            items["replay_id"] = replayId.ToString();
        }

        return new DynamicSamplingContext(items);
    }

    public static DynamicSamplingContext CreateFromTransaction(TransactionTracer transaction, SentryOptions options, IReplaySession? replaySession)
    {
        // These should already be set on the transaction.
        var publicKey = options.ParsedDsn.PublicKey;
        var traceId = transaction.TraceId;
        var sampled = transaction.IsSampled;
        var sampleRate = transaction.SampleRate!.Value;
        var sampleRand = transaction.SampleRand;
        var transactionName = transaction.NameSource.IsHighQuality() ? transaction.Name : null;

        // These two may not have been set yet on the transaction, but we can get them directly.
        var release = options.SettingLocator.GetRelease();
        var environment = options.SettingLocator.GetEnvironment();

        return new DynamicSamplingContext(traceId,
            publicKey,
            sampled,
            sampleRate,
            sampleRand,
            release,
            environment,
            transactionName,
            replaySession);
    }

    public static DynamicSamplingContext CreateFromUnsampledTransaction(UnsampledTransaction transaction, SentryOptions options, IReplaySession? replaySession)
    {
        // These should already be set on the transaction.
        var publicKey = options.ParsedDsn.PublicKey;
        var traceId = transaction.TraceId;
        var sampled = transaction.IsSampled;
        var sampleRate = transaction.SampleRate!.Value;
        var sampleRand = transaction.SampleRand;
        var transactionName = transaction.NameSource.IsHighQuality() ? transaction.Name : null;

        // These two may not have been set yet on the transaction, but we can get them directly.
        var release = options.SettingLocator.GetRelease();
        var environment = options.SettingLocator.GetEnvironment();

        return new DynamicSamplingContext(traceId,
            publicKey,
            sampled,
            sampleRate,
            sampleRand,
            release,
            environment,
            transactionName,
            replaySession);
    }

    public static DynamicSamplingContext CreateFromPropagationContext(SentryPropagationContext propagationContext, SentryOptions options, IReplaySession? replaySession)
    {
        var traceId = propagationContext.TraceId;
        var publicKey = options.ParsedDsn.PublicKey;
        var release = options.SettingLocator.GetRelease();
        var environment = options.SettingLocator.GetEnvironment();

        return new DynamicSamplingContext(
            traceId,
            publicKey,
            null,
            release: release,
            environment: environment,
            replaySession: replaySession
            );
    }
}

internal static class DynamicSamplingContextExtensions
{
    public static DynamicSamplingContext? CreateDynamicSamplingContext(this BaggageHeader baggage, IReplaySession? replaySession = null)
        => DynamicSamplingContext.CreateFromBaggageHeader(baggage, replaySession);

    public static DynamicSamplingContext CreateDynamicSamplingContext(this TransactionTracer transaction, SentryOptions options, IReplaySession? replaySession)
        => DynamicSamplingContext.CreateFromTransaction(transaction, options, replaySession);

    public static DynamicSamplingContext CreateDynamicSamplingContext(this UnsampledTransaction transaction, SentryOptions options, IReplaySession? replaySession)
        => DynamicSamplingContext.CreateFromUnsampledTransaction(transaction, options, replaySession);

    public static DynamicSamplingContext CreateDynamicSamplingContext(this SentryPropagationContext propagationContext, SentryOptions options, IReplaySession? replaySession)
        => DynamicSamplingContext.CreateFromPropagationContext(propagationContext, options, replaySession);
}
