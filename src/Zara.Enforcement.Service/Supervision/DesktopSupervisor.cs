namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Owns one exact desktop launch generation at a time and recreates it indefinitely while the
/// latest accepted directive requires presence. Product schedules and settings are deliberately
/// absent from this state machine.
/// </summary>
internal sealed class DesktopSupervisor
{
    internal static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMaximumRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IDesktopProcessLauncher _launcher;
    private readonly ISupervisionCommandSource _commandSource;
    private readonly ISupervisionDelay _delay;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;

    public DesktopSupervisor(
        IDesktopProcessLauncher launcher,
        ISupervisionCommandSource commandSource,
        ISupervisionDelay delay,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(commandSource);
        ArgumentNullException.ThrowIfNull(delay);

        TimeSpan selectedInitial = initialRetryDelay ?? DefaultInitialRetryDelay;
        TimeSpan selectedMaximum = maximumRetryDelay ?? DefaultMaximumRetryDelay;
        if (selectedInitial <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialRetryDelay),
                "The initial retry delay must be positive.");
        }

        if (selectedMaximum < selectedInitial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetryDelay),
                "The maximum retry delay must not be shorter than the initial delay.");
        }

        _launcher = launcher;
        _commandSource = commandSource;
        _delay = delay;
        _initialRetryDelay = selectedInitial;
        _maximumRetryDelay = selectedMaximum;
    }

    /// <summary>
    /// Runs until the Service is cancelled. A release never kills a live desktop; it only prevents
    /// the next launch and leaves the supervisor idle for a future authenticated command, allowing
    /// the tray exit path to finish gracefully without disabling later manual registrations.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        SupervisionDirective directive = _commandSource.Current;
        long observedRevision = directive.Revision;
        TimeSpan retryDelay = _initialRetryDelay;
        ISupervisedProcess? process = null;
        bool processReady = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshDirective(ref directive, ref observedRevision);

                if (process is not null)
                {
                    ProcessWaitOutcome waitOutcome = await WaitForProcessOrDirectiveAsync(
                        process,
                        processReady,
                        directive,
                        observedRevision,
                        cancellationToken).ConfigureAwait(false);
                    directive = waitOutcome.Directive;
                    observedRevision = directive.Revision;

                    if (waitOutcome.Kind == ProcessWaitKind.DirectiveChanged)
                    {
                        continue;
                    }

                    if (waitOutcome.Kind == ProcessWaitKind.Ready)
                    {
                        processReady = true;
                        retryDelay = _initialRetryDelay;
                        continue;
                    }

                    await process.DisposeAsync().ConfigureAwait(false);
                    process = null;
                    processReady = false;
                    RefreshDirective(ref directive, ref observedRevision);

                    if (!directive.RestartRequired)
                    {
                        continue;
                    }

                    RetryWaitOutcome exitRetry = await WaitForRetryOrReleaseAsync(
                        retryDelay,
                        directive,
                        cancellationToken).ConfigureAwait(false);
                    directive = exitRetry.Directive;
                    observedRevision = directive.Revision;
                    if (!exitRetry.ShouldRetry)
                    {
                        continue;
                    }

                    retryDelay = NextRetryDelay(retryDelay);
                }

                RefreshDirective(ref directive, ref observedRevision);
                if (!directive.RestartRequired)
                {
                    directive = await _commandSource.WaitForChangeAsync(
                        observedRevision,
                        cancellationToken).ConfigureAwait(false);
                    observedRevision = directive.Revision;
                    continue;
                }

                DesktopLaunchResult launchResult;
                try
                {
                    launchResult = await _launcher.LaunchAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    launchResult = DesktopLaunchResult.RetryableFailure(
                        exception.HResult,
                        "LauncherException");
                }

                RefreshDirective(ref directive, ref observedRevision);
                if (launchResult.Succeeded)
                {
                    process = launchResult.Process ??
                        throw new InvalidOperationException(
                            "A successful launch result did not contain a process lifetime.");
                    processReady = false;
                    continue;
                }

                if (!directive.RestartRequired)
                {
                    continue;
                }

                RetryWaitOutcome failureRetry = await WaitForRetryOrReleaseAsync(
                    retryDelay,
                    directive,
                    cancellationToken).ConfigureAwait(false);
                directive = failureRetry.Directive;
                observedRevision = directive.Revision;
                if (!failureRetry.ShouldRetry)
                {
                    continue;
                }

                retryDelay = NextRetryDelay(retryDelay);
            }
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ProcessWaitOutcome> WaitForProcessOrDirectiveAsync(
        ISupervisedProcess process,
        bool processReady,
        SupervisionDirective directive,
        long observedRevision,
        CancellationToken cancellationToken)
    {
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task processExit = process.WaitForExitAsync(cancellationToken);
        Task<SupervisionDirective> command = _commandSource.WaitForChangeAsync(
            observedRevision,
            commandCancellation.Token).AsTask();
        Task? readiness = processReady
            ? null
            : process.WaitForReadyAsync(cancellationToken);

        Task completed = readiness is null
            ? await Task.WhenAny(processExit, command).ConfigureAwait(false)
            : await Task.WhenAny(processExit, command, readiness).ConfigureAwait(false);

        // If health and exit become observable together, accept health first so that a generation
        // which did reach READY resets the retry backoff exactly once before its exit is handled.
        if (readiness is not null && readiness.IsCompletedSuccessfully)
        {
            commandCancellation.Cancel();
            await ObserveExpectedCancellationAsync(command).ConfigureAwait(false);
            await readiness.ConfigureAwait(false);
            return new(ProcessWaitKind.Ready, _commandSource.Current);
        }

        if (completed == command)
        {
            SupervisionDirective changedDirective = await command.ConfigureAwait(false);
            return new(ProcessWaitKind.DirectiveChanged, changedDirective);
        }

        commandCancellation.Cancel();
        await ObserveExpectedCancellationAsync(command).ConfigureAwait(false);

        if (readiness is not null && completed == readiness)
        {
            try
            {
                await readiness.ConfigureAwait(false);
                return new(ProcessWaitKind.Ready, _commandSource.Current);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new(ProcessWaitKind.ReadinessFailed, _commandSource.Current);
            }
        }

        try
        {
            await processExit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failed exact-handle wait leaves process ownership uncertain. Disposing the lifetime
            // closes its kill-on-close job before a new generation is attempted.
        }

        SupervisionDirective current = _commandSource.Current;
        return new(
            ProcessWaitKind.Exited,
            current.Revision > directive.Revision ? current : directive);
    }

    private async Task<RetryWaitOutcome> WaitForRetryOrReleaseAsync(
        TimeSpan retryDelay,
        SupervisionDirective directive,
        CancellationToken cancellationToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task delay = _delay.DelayAsync(retryDelay, delayCancellation.Token);

        while (true)
        {
            SupervisionDirective current = _commandSource.Current;
            if (current.Revision > directive.Revision)
            {
                directive = current;
            }

            if (!directive.RestartRequired)
            {
                delayCancellation.Cancel();
                await ObserveExpectedCancellationAsync(delay).ConfigureAwait(false);
                return new(false, directive);
            }

            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            Task<SupervisionDirective> command = _commandSource.WaitForChangeAsync(
                directive.Revision,
                commandCancellation.Token).AsTask();
            Task completed = await Task.WhenAny(delay, command).ConfigureAwait(false);
            if (completed == delay)
            {
                commandCancellation.Cancel();
                await ObserveExpectedCancellationAsync(command).ConfigureAwait(false);
                await delay.ConfigureAwait(false);

                current = _commandSource.Current;
                if (current.Revision > directive.Revision)
                {
                    directive = current;
                }

                return new(directive.RestartRequired, directive);
            }

            directive = await command.ConfigureAwait(false);
            // An equivalent RequireRestart update must not cancel or restart the existing backoff.
            // The same timer remains in flight until it expires or a release arrives.
        }
    }

    private void RefreshDirective(
        ref SupervisionDirective directive,
        ref long observedRevision)
    {
        SupervisionDirective current = _commandSource.Current;
        if (current.Revision > observedRevision)
        {
            directive = current;
            observedRevision = current.Revision;
        }
    }

    private TimeSpan NextRetryDelay(TimeSpan current)
    {
        if (current >= _maximumRetryDelay)
        {
            return _maximumRetryDelay;
        }

        long doubledTicks = checked(current.Ticks * 2);
        return TimeSpan.FromTicks(Math.Min(doubledTicks, _maximumRetryDelay.Ticks));
    }

    private static async Task ObserveExpectedCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum ProcessWaitKind
    {
        DirectiveChanged,
        Ready,
        Exited,
        ReadinessFailed,
    }

    private readonly record struct ProcessWaitOutcome(
        ProcessWaitKind Kind,
        SupervisionDirective Directive);

    private readonly record struct RetryWaitOutcome(
        bool ShouldRetry,
        SupervisionDirective Directive);
}
