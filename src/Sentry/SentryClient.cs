using Sentry.Extensibility;
using Sentry.Internal;
using Sentry.Protocol.Envelopes;

namespace Sentry;

/// <summary>
/// Sentry client used to send events to Sentry.
/// </summary>
/// <remarks>
/// This client captures events by queueing those to its
/// internal background worker which sends events to Sentry.
/// </remarks>
/// <inheritdoc cref="ISentryClient" />
/// <inheritdoc cref="IDisposable" />
public class SentryClient : ISentryClient, IDisposable
{
    private readonly SentryOptions _options;
    private readonly ISessionManager _sessionManager;
    private readonly RandomValuesFactory _randomValuesFactory;
    private readonly Enricher _enricher;

    internal IBackgroundWorker Worker { get; }

    internal SentryOptions Options => _options;

    /// <summary>
    /// Whether the client is enabled.
    /// </summary>
    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <summary>
    /// Creates a new instance of <see cref="SentryClient"/>.
    /// </summary>
    /// <param name="options">The configuration for this client.</param>
    public SentryClient(SentryOptions options)
        : this(options, null, null, null) { }

    internal SentryClient(
        SentryOptions options,
        IBackgroundWorker? worker = null,
        RandomValuesFactory? randomValuesFactory = null,
        ISessionManager? sessionManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _randomValuesFactory = randomValuesFactory ?? new SynchronizedRandomValuesFactory();
        _sessionManager = sessionManager ?? new GlobalSessionManager(options);
        _enricher = new Enricher(options);

        options.SetupLogging(); // Only relevant if this client wasn't created as a result of calling Init

        if (worker == null)
        {
            var composer = new SdkComposer(options);
            Worker = composer.CreateBackgroundWorker();
        }
        else
        {
            options.LogDebug("Worker of type {0} was provided via Options.", worker.GetType().Name);
            Worker = worker;
        }
    }

    /// <inheritdoc />
    public SentryId CaptureEvent(SentryEvent? @event, Scope? scope = null, SentryHint? hint = null)
    {
        if (@event == null)
        {
            return SentryId.Empty;
        }

        try
        {
            return DoSendEvent(@event, hint, scope);
        }
        catch (Exception e)
        {
            _options.LogError(e, "An error occurred when capturing the event {0}.", @event.EventId);
            return SentryId.Empty;
        }
    }

    /// <inheritdoc />
    public void CaptureFeedback(SentryFeedback feedback, Scope? scope = null, SentryHint? hint = null)
    {
        if (string.IsNullOrEmpty(feedback.Message))
        {
            _options.LogWarning("Feedback dropped due to empty message.");
            return;
        }

        scope ??= new Scope(_options);
        hint ??= new SentryHint();
        hint.AddAttachmentsFromScope(scope);

        _options.LogInfo("Capturing feedback: '{0}'.", feedback.Message);

        var evt = new SentryEvent { Level = SentryLevel.Info };
        evt.Contexts.Feedback = feedback;
        // type: 'feedback',

        // Evaluate and copy before invoking the callback
        scope.Evaluate();
        scope.Apply(evt);

        if (scope.Level != null && scope.Level != SentryLevel.Info)
        {
            // Level on scope takes precedence over the one on event
            _options.LogInfo("Overriding level set on feedback event '{0}' with level set on scope '{1}'.", evt.Level, scope.Level);
            evt.Level = scope.Level;
        }

        var attachments = hint.Attachments.ToList();
        var envelope = Envelope.FromFeedback(evt, _options.DiagnosticLogger, attachments, scope.SessionUpdate);
        CaptureEnvelope(envelope);
    }

    /// <inheritdoc />
    [Obsolete("Use CaptureFeedback instead.")]
    public void CaptureUserFeedback(UserFeedback userFeedback)
    {
        if (userFeedback.EventId.Equals(SentryId.Empty))
        {
            // Ignore the user feedback if EventId is empty
            _options.LogWarning("User feedback dropped due to empty id.");
            return;
        }

        CaptureEnvelope(Envelope.FromUserFeedback(userFeedback));
    }

    /// <inheritdoc />
    public void CaptureTransaction(SentryTransaction transaction) => CaptureTransaction(transaction, null, null);

    /// <inheritdoc />
    public void CaptureTransaction(SentryTransaction transaction, Scope? scope, SentryHint? hint)
    {
        if (transaction.SpanId.Equals(SpanId.Empty))
        {
            _options.LogWarning("Transaction dropped due to empty id.");
            return;
        }

        if (string.IsNullOrWhiteSpace(transaction.Name) ||
            string.IsNullOrWhiteSpace(transaction.Operation))
        {
            _options.LogWarning("Transaction discarded due to one or more required fields missing.");
            return;
        }

        // Unfinished transaction can only happen if the user calls this method instead of
        // transaction.Finish().
        // We still send these transactions over, but warn the user not to do it.
        if (!transaction.IsFinished)
        {
            _options.LogWarning("Capturing a transaction which has not been finished. " +
                                "Please call transaction.Finish() instead of hub.CaptureTransaction(transaction) " +
                                "to properly finalize the transaction and send it to Sentry.");
        }

        // Sampling decision MUST have been made at this point
        Debug.Assert(transaction.IsSampled is not null, "Attempt to capture transaction without sampling decision.");

        var spanCount = transaction.Spans.Count + 1; // 1 for each span + 1 for the transaction itself
        if (transaction.IsSampled is false)
        {
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.SampleRate, DataCategory.Transaction);
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.SampleRate, DataCategory.Span, spanCount);
            _options.LogDebug("Transaction dropped by sampling.");
            return;
        }

        scope ??= new Scope(_options);
        hint ??= new SentryHint();
        hint.AddAttachmentsFromScope(scope);

        _options.LogInfo("Capturing transaction.");

        scope.Evaluate();
        scope.Apply(transaction);

        _enricher.Apply(transaction);

        var processedTransaction = transaction;
        foreach (var processor in scope.GetAllTransactionProcessors())
        {
            processedTransaction = processor.DoProcessTransaction(transaction, hint);
            if (processedTransaction == null) // Rejected transaction
            {
                _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.EventProcessor, DataCategory.Transaction);
                _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.EventProcessor, DataCategory.Span, spanCount);
                _options.LogInfo("Event dropped by processor {0}", processor.GetType().Name);
                return;
            }
        }

        processedTransaction = BeforeSendTransaction(processedTransaction, hint);
        if (processedTransaction is null) // Rejected transaction
        {
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.BeforeSend, DataCategory.Transaction);
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.BeforeSend, DataCategory.Span, spanCount);
            _options.LogInfo("Transaction dropped by BeforeSendTransaction callback.");
            return;
        }

        if (!_options.SendDefaultPii)
        {
            processedTransaction.Redact();
        }

        CaptureEnvelope(Envelope.FromTransaction(processedTransaction));
    }

#if NET6_0_OR_GREATER
    [UnconditionalSuppressMessage("Trimming", "IL2026: RequiresUnreferencedCode", Justification = AotHelper.AvoidAtRuntime)]
#endif
    private SentryTransaction? BeforeSendTransaction(SentryTransaction transaction, SentryHint hint)
    {
        if (_options.BeforeSendTransactionInternal is null)
        {
            return transaction;
        }

        _options.LogDebug("Calling the BeforeSendTransaction callback");

        try
        {
            return _options.BeforeSendTransactionInternal?.Invoke(transaction, hint);
        }
        catch (Exception e)
        {
            if (!AotHelper.IsTrimmed)
            {
                // Attempt to demystify exceptions before adding them as breadcrumbs.
                e.Demystify();
            }

            _options.LogError(e, "The BeforeSendTransaction callback threw an exception. It will be added as breadcrumb and continue.");

            var data = new Dictionary<string, string>
            {
                {"message", e.Message}
            };

            if (e.StackTrace is not null)
            {
                data.Add("stackTrace", e.StackTrace);
            }

            transaction.AddBreadcrumb(
                message: "BeforeSendTransaction callback failed.",
                category: "SentryClient",
                data: data,
                level: BreadcrumbLevel.Error);
        }

        return transaction;
    }

    /// <inheritdoc />
    public void CaptureSession(SessionUpdate sessionUpdate)
        => CaptureEnvelope(Envelope.FromSession(sessionUpdate));

    /// <inheritdoc />
    public SentryId CaptureCheckIn(
        string monitorSlug,
        CheckInStatus status,
        SentryId? sentryId = null,
        TimeSpan? duration = null,
        Scope? scope = null,
        Action<SentryMonitorOptions>? configureMonitorOptions = null)
    {
        scope ??= new Scope(_options);

        var traceId = scope.PropagationContext.TraceId;
        if (scope.Span is not null)
        {
            // Overwrite the traceId if there is a span active
            traceId = scope.Span.TraceId;
        }

        var checkIn = new SentryCheckIn(monitorSlug, status, sentryId)
        {
            Duration = duration,
            TraceId = traceId,
        };

        if (configureMonitorOptions is not null)
        {
            var monitorOptions = new SentryMonitorOptions();
            configureMonitorOptions.Invoke(monitorOptions);
            checkIn.MonitorOptions = monitorOptions;
        }

        _enricher.Apply(checkIn);

        return CaptureEnvelope(Envelope.FromCheckIn(checkIn))
            ? checkIn.Id
            : SentryId.Empty;
    }

    /// <summary>
    /// Flushes events asynchronously.
    /// </summary>
    /// <param name="timeout">How long to wait for flush to finish.</param>
    /// <returns>A task to await for the flush operation.</returns>
    public Task FlushAsync(TimeSpan timeout) => Worker.FlushAsync(timeout);

    // TODO: this method needs to be refactored, it's really hard to analyze nullability
    private SentryId DoSendEvent(SentryEvent @event, SentryHint? hint, Scope? scope)
    {
        var filteredExceptions = ApplyExceptionFilters(@event.Exception);
        if (filteredExceptions?.Count > 0)
        {
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.EventProcessor, DataCategory.Error);
            _options.LogInfo("Event was dropped by one or more exception filters for exception(s): {0}",
                string.Join(", ", filteredExceptions.Select(e => e.GetType()).Distinct()));
            return SentryId.Empty;
        }

        scope ??= new Scope(_options);
        hint ??= new SentryHint();
        hint.AddAttachmentsFromScope(scope);

        _options.LogInfo("Capturing event.");

        // Evaluate and copy before invoking the callback
        scope.Evaluate();
        scope.Apply(@event);

        if (scope.Level != null)
        {
            // Level on scope takes precedence over the one on event
            _options.LogInfo("Overriding level set on event '{0}' with level set on scope '{1}'.", @event.Level, scope.Level);
            @event.Level = scope.Level;
        }

        if (@event.Exception != null)
        {
            // Depends on Options instead of the processors to allow application adding new processors
            // after the SDK is initialized. Useful for example once a DI container is up
            foreach (var processor in scope.GetAllExceptionProcessors())
            {
                processor.Process(@event.Exception, @event);

                // NOTE: Exception processors can't drop events, but exception filters (above) can.
            }
        }

        if (SentryEventHelper.ProcessEvent(@event, scope.GetAllEventProcessors(), hint, _options) is not { } processedEvent)
        {
            return SentryId.Empty;  // Dropped by an event processor
        }

        processedEvent = SentryEventHelper.DoBeforeSend(processedEvent, hint, _options);
        if (processedEvent == null)
        {
            return SentryId.Empty; // Dropped by BeforeSend callback
        }

        var hasTerminalException = processedEvent.HasTerminalException();
        if (hasTerminalException)
        {
            // Event contains a terminal exception -> end session as crashed
            _options.LogDebug("Ending session as Crashed, due to unhandled exception.");
            scope.SessionUpdate = _sessionManager.EndSession(SessionEndStatus.Crashed);
        }
        else if (processedEvent.HasException())
        {
            // Event contains a non-terminal exception -> report error
            // (this might return null if the session has already reported errors before)
            scope.SessionUpdate = _sessionManager.ReportError();
        }

        if (_options.SampleRate != null)
        {
            if (!_randomValuesFactory.NextBool(_options.SampleRate.Value))
            {
                _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.SampleRate, DataCategory.Error);
                _options.LogDebug("Event sampled.");
                return SentryId.Empty;
            }
        }
        else
        {
            _options.LogDebug("Event not sampled.");
        }

        if (!_options.SendDefaultPii)
        {
            processedEvent.Redact();
        }

        var attachments = hint.Attachments.ToList();
        var envelope = Envelope.FromEvent(processedEvent, _options.DiagnosticLogger, attachments, scope.SessionUpdate);
        return CaptureEnvelope(envelope) ? processedEvent.EventId : SentryId.Empty;
    }

    private IReadOnlyCollection<Exception>? ApplyExceptionFilters(Exception? exception)
    {
        var filters = _options.ExceptionFilters;
        if (exception == null || filters == null || filters.Count == 0)
        {
            // There was nothing to filter.
            return null;
        }

        if (filters.Any(f => f.Filter(exception)))
        {
            // The event should be filtered based on the given exception
            return new[] { exception };
        }

        if (exception is AggregateException aggregate)
        {
            // Flatten the tree of aggregates such that all the inner exceptions are non-aggregates.
            var innerExceptions = aggregate.Flatten().InnerExceptions;
            if (innerExceptions.All(e => ApplyExceptionFilters(e) != null))
            {
                // All inner exceptions matched a filter, so the event should be filtered.
                return innerExceptions;
            }
        }

        // The event should not be filtered.
        return null;
    }

    /// <inheritdoc cref="ISentryClient.CaptureEnvelope"/>
    public bool CaptureEnvelope(Envelope envelope)
    {
        if (Worker.EnqueueEnvelope(envelope))
        {
            _options.LogInfo("Envelope queued up: '{0}'", envelope.TryGetEventId(_options.DiagnosticLogger));
            return true;
        }

        _options.LogWarning(
            "The attempt to queue the event failed. Items in queue: {0}",
            Worker.QueuedItems);

        return false;
    }

    /// <summary>
    /// Disposes this client
    /// </summary>
    public void Dispose()
    {
        _options.LogDebug("Flushing SentryClient.");

        try
        {
            // Worker should empty its queue until SentryOptions.ShutdownTimeout
            Worker.FlushAsync(_options.ShutdownTimeout).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch
        {
            _options.LogDebug("Failed to wait on worker to flush");
        }
    }
}
