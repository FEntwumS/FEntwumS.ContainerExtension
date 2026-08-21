using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using static ContainerExtension.Services.Docker.DockerToolConsole;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Owns the container run mechanics for a connected daemon: pulling the image per the configured pull
/// policy, creating/starting the container, streaming and capturing stdout/stderr, sampling resource
/// usage, and stopping/removing on completion or cancellation. Extracted from
/// <see cref="DockerExecutionStrategy"/>, which prepares the inputs and orchestrates fallback/telemetry
/// around each run.
/// </summary>
internal sealed class ContainerRunner
{
    private readonly DockerClient _client;
    private readonly ISettingsService _settings;
    private readonly DockerToolConsole _console;
    private readonly Uri _daemonUri;

    internal ContainerRunner(DockerClient client, ISettingsService settings, DockerToolConsole console, Uri daemonUri)
    {
        _client = client;
        _settings = settings;
        _console = console;
        _daemonUri = daemonUri;
    }

    internal async Task<string?> EnsureImageAsync(string image, ToolCommand command, CancellationToken ct)
    {
        string? imageDigest = null;
        var platform = _settings.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto")?.Trim();
        var pullPolicy = _settings.SafeGetSetting<string>(ContainerExtensionModule.PullPolicySetting, "if-not-present");

        bool imageExistsLocally = false;
        try
        {
            var inspectResponse = await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            imageDigest = inspectResponse.ID;
            imageExistsLocally = true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            imageExistsLocally = false;
        }

        bool shouldPull = pullPolicy switch
        {
            "always" => true,
            "never" => false,
            _ => !imageExistsLocally
        };

        if (!imageExistsLocally && string.Equals(pullPolicy, "never", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Image '{image}' not found locally and pull policy is 'never'.");
        }

        if (shouldPull)
        {
            _console.SdkLog(command, string.Equals(pullPolicy, "always", StringComparison.Ordinal) && imageExistsLocally
              ? $"[Docker SDK] Pull policy 'always' — refreshing '{image}'..."
              : $"[Docker SDK] Image '{image}' not found locally. Pulling...");

            var pullParams = new ImagesCreateParameters { FromImage = image };
            if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
            {
                pullParams.Platform = platform;
            }

            // The daemon reports registry pull failures as in-band JSON error frames over an HTTP 200
            // response. Capture the first one so the real reason survives to the post-pull check below;
            // CreateImageAsync itself returns successfully even when the pull failed.
            string? lastPullError = null;
            var progressHandler = new Progress<JSONMessage>(msg =>
            {
                if (msg == null)
                {
                    return;
                }
                try
                {
                    if (msg.Error != null || !string.IsNullOrEmpty(msg.ErrorMessage))
                    {
                        Volatile.Write(ref lastPullError, msg.ErrorMessage ?? msg.Error?.Message);
                    }

                    var progressText = string.IsNullOrWhiteSpace(msg.ProgressMessage)
                        ? msg.Status
                        : $"{msg.Status} {msg.ProgressMessage}";

                    if (!string.IsNullOrWhiteSpace(progressText))
                    {
                        _console.SdkLog(command, $"[Docker Pull] {progressText}");
                    }
                }
                catch (Exception)
                {
                    // Keep the image pull task running through status formatting errors
                }
            });

            try
            {
                try
                {
                    await _client.Images.CreateImageAsync(pullParams, null, progressHandler, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !string.IsNullOrWhiteSpace(pullParams.Platform))
                {
                    _console.SdkLog(command, $"[Docker Pull Warning] Pull failed with platform '{pullParams.Platform}': {ex.Message}. Falling back to default host architecture.");
                    pullParams.Platform = null;
                    await _client.Images.CreateImageAsync(pullParams, null, progressHandler, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (imageExistsLocally)
                {
                    _console.SdkLog(command, $"[Docker Pull Warning] Pull failed for '{image}': {ex.Message}. Falling back to cached local version.");
                }
                else
                {
                    throw;
                }
            }

            var capturedPullError = Volatile.Read(ref lastPullError);

            // Confirm the image materialized locally. A NotFound here means the pull failed despite
            // CreateImageAsync returning normally, unless a cached local copy is being relied upon.
            try
            {
                var postPull = await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
                imageDigest = postPull.ID;
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && !imageExistsLocally)
            {
                throw new DockerExecutionException(
                  $"Failed to pull image '{image}': {capturedPullError ?? "image not found on registry."}", ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Post-pull digest inspect failed for '{image}'", ex);
            }

            if (capturedPullError == null)
            {
                _console.SdkLog(command, $"[Docker SDK] Pull complete for '{image}'.");
            }
        }

        if (imageDigest != null)
        {
            var shortDigest = imageDigest.ShortId();
            _console.SdkLog(command, $"[Docker SDK] Resolved digest: {shortDigest}...");
        }

        return imageDigest;
    }

    internal record ResourceProfile(long PeakMemoryBytes, double MaxCpuPercent, int SampleCount, bool OomKilled);

    // Merge a late-arriving stats profile into whatever the run already captured. An earlier capture always
    // wins, so the OOM correction applied after container inspect is never overwritten by the stats sampler
    // (which reports OomKilled=false); the late profile is adopted only when nothing was captured yet.
    internal static ResourceProfile? MergeLateResourceProfile(ResourceProfile? captured, ResourceProfile? late)
        => captured ?? late;

    private async Task<ResourceProfile?> CollectResourceStatsAsync(
      string containerId, ToolCommand command, CancellationToken ct)
    {
        long peakMemory = 0;
        double maxCpu = 0;
        int sampleCount = 0;
        long prevCpuTotal = 0;
        long prevSystemTotal = 0;
        var statsLock = new System.Threading.Lock();

        try
        {
            var progress = new StatelessProgress<ContainerStatsResponse>(stats =>
            {
                if (stats.MemoryStats?.Usage > 0)
                {
                    var currentMem = (long)stats.MemoryStats.Usage;
                    long current;
                    do { current = Interlocked.Read(ref peakMemory); }
                    while (currentMem > current && Interlocked.CompareExchange(ref peakMemory, currentMem, current) != current);
                }

                if (stats.CPUStats?.CPUUsage?.TotalUsage > 0 && stats.CPUStats?.SystemUsage > 0)
                {
                    var cpuTotal = (long)stats.CPUStats.CPUUsage.TotalUsage;
                    var systemTotal = (long)stats.CPUStats.SystemUsage;
                    var onlineCpus = (int)(stats.CPUStats.OnlineCPUs > 0 ? stats.CPUStats.OnlineCPUs : 1);

                    lock (statsLock)
                    {
                        if (prevCpuTotal > 0 && prevSystemTotal > 0)
                        {
                            var cpuDelta = (double)(cpuTotal - prevCpuTotal);
                            var systemDelta = (double)(systemTotal - prevSystemTotal);
                            if (systemDelta > 0 && onlineCpus > 0)
                            {
                                var cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                                var currentMax = Volatile.Read(ref maxCpu);
                                while (cpuPercent > currentMax && Interlocked.CompareExchange(ref maxCpu, cpuPercent, currentMax) != currentMax)
                                {
                                    currentMax = Volatile.Read(ref maxCpu);
                                }
                            }
                        }
                        prevCpuTotal = cpuTotal;
                        prevSystemTotal = systemTotal;
                    }
                }
                Interlocked.Increment(ref sampleCount);
            });

            await _client.Containers.GetContainerStatsAsync(
              containerId, new ContainerStatsParameters { Stream = true }, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Ignore */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _console.SdkLog(command, $"[Docker SDK] Stats collection ended: {ex.Message}", RankInfo);
        }

        if (Interlocked.CompareExchange(ref sampleCount, 0, 0) == 0) return null;
        return new ResourceProfile(Interlocked.Read(ref peakMemory), Math.Round(Volatile.Read(ref maxCpu), 1), sampleCount, false);
    }

    private sealed class StatelessProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    internal async Task<(long exitCode, string output, bool wasCancelled, ResourceProfile? profile)> RunContainerAsync(
      CreateContainerParameters createParams, ToolCommand command, CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var executable = (command.Executable ?? command.ToolName ?? string.Empty).Replace("\r", "");
        long exitCode = -1;
        // Written from the cancellation-callback thread and read on the main path; accessed via
        // Volatile to establish the cross-thread happens-before the EOF-drain decision relies on.
        int wasCancelledFlag = 0;

        // Docker.DotNet's AttachContainerAsync only accepts a write-closable (socket) transport: it
        // rejects any hijacked stream whose CanCloseWrite is false with
        // NotSupportedException("Cannot shutdown write on this transport"). The Windows named-pipe
        // stream reports CanCloseWrite == false, so attach is unusable over npipe — the Docker Desktop
        // default endpoint. Since the strategy only ever reads stdout/stderr and never writes stdin, on
        // npipe it streams output through the non-hijacked logs-follow endpoint instead: same
        // multiplexed framing, no write-close requirement. That stream is opened after the container
        // starts, so auto-remove is disabled on this path to stop a fast-exiting container from being
        // reaped before its logs drain; it is force-removed explicitly in the finally.
        var useLogsStreaming = _daemonUri?.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase) == true;
        // The reaper's force-remove gate must reflect the user's actual Auto-Remove intent, not the
        // daemon-side npipe workaround. Capture it before the override below clobbers HostConfig.AutoRemove
        // to false: otherwise an npipe container still tracked at teardown would be stopped but never
        // removed, diverging from the socket path which inherits the user's setting verbatim.
        var autoRemove = createParams.HostConfig?.AutoRemove ?? true;
        if (useLogsStreaming && createParams.HostConfig is not null)
        {
            createParams.HostConfig.AutoRemove = false;
        }

        // Track the container by its unique name BEFORE creating it, closing the window in which the
        // container exists on the daemon but is not yet tracked by ID: if the process is torn down in that
        // window the exit reaper (CleanupContainers) can still stop/remove it, since Docker accepts a name
        // or an ID. Once the ID is tracked, drop the name entry so teardown and the reaper key off the ID.
        var containerName = createParams.Name;
        var trackByName = !string.IsNullOrEmpty(containerName);
        if (trackByName)
        {
            ContainerReaper.Track(containerName!, autoRemove);
        }

        string containerId;
        try
        {
            var container = await _client.Containers.CreateContainerAsync(createParams, ct).ConfigureAwait(false);
            containerId = container.ID;
        }
        catch
        {
            if (trackByName)
            {
                ContainerReaper.Untrack(containerName!);
            }
            throw;
        }
        ContainerReaper.Track(containerId, autoRemove);
        if (trackByName)
        {
            ContainerReaper.Untrack(containerName!);
        }

        var cancelRegistration = ct.CanBeCanceled
          ? ct.Register(() =>
          {
              Volatile.Write(ref wasCancelledFlag, 1);
              try
              {
                  var stopTask = _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
#pragma warning disable VSTHRD110
                  stopTask.ContinueWith(t =>
    {
        if (t.IsFaulted)
            ContainerTelemetry.TrackError("DockerExecutionStrategy",
          $"Async container stop failed for '{containerId.ShortId()}'", t.Exception?.InnerException);
    }, TaskScheduler.Default);
#pragma warning restore VSTHRD110
              }
              catch (Exception ex) when (ex is not OutOfMemoryException)
              {
                  ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Cancel-time container stop failed for '{containerId.ShortId()}'", ex);
              }
          })
          : (CancellationTokenRegistration?)null;

        ResourceProfile? profile = null;
        Task? readTask = null;
        Task<ResourceProfile?>? statsTask = null;
        CancellationTokenSource? statsCts = null;
        CancellationTokenSource? readCts = null;
        // Hoisted out of a using-declaration so it is disposed in the finally AFTER readTask drains; a using
        // here would dispose the stream while the read loop could still touch it on an early-throw path.
        MultiplexedStream? stream = null;
        bool ranToCompletion = false;

        try
        {
            _console.SdkLog(command, $"[Docker SDK] Spawning {executable} in {createParams.Image}...");
            _console.SdkLog(command, $"[Docker SDK] Command: {string.Join(" ", createParams.Cmd ?? [])}", RankInfo);

            readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readToken = readCts.Token;

            var containerStopwatch = Stopwatch.StartNew();
            if (useLogsStreaming)
            {
                // npipe: no hijacked attach. Start first, then follow the container's log stream. The
                // logging driver captures stdout/stderr from process start, so opening the stream after
                // the start call loses no output; the framing is identical to a non-TTY attach.
                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
                _console.SdkLog(command, $"[Docker SDK] Container {containerId.ShortId()} started.", RankInfo);
                stream = await _client.Containers.GetContainerLogsAsync(
                  containerId, false,
                  new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true, Timestamps = false }, ct).ConfigureAwait(false);
            }
            else
            {
                // Socket transports (unix/tcp): attach before start so no early output can be missed.
                stream = await _client.Containers.AttachContainerAsync(
                  containerId, false,
                  new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true }, ct).ConfigureAwait(false);
                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
                _console.SdkLog(command, $"[Docker SDK] Container {containerId.ShortId()} started.", RankInfo);
            }

            readTask = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();
                var stdoutDecoder = Encoding.UTF8.GetDecoder();
                var stderrDecoder = Encoding.UTF8.GetDecoder();
                var charBuf = System.Buffers.ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(buffer.Length));

                try
                {
                    while (!readToken.IsCancellationRequested)
                    {
                        readToken.ThrowIfCancellationRequested();
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, readToken).ConfigureAwait(false);
                        if (result.EOF) break;

                        int charCount;
                        if (result.Target == MultiplexedStream.TargetStream.StandardError)
                        {
                            charCount = stderrDecoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                            var textSpan = charBuf.AsSpan(0, charCount);
                            lock (outputBuilder)
                            {
                                AppendCapped(outputBuilder, textSpan);
                            }
                            DrainLines(stderrBuf, textSpan, command.ErrorHandler);
                        }
                        else
                        {
                            charCount = stdoutDecoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                            var textSpan = charBuf.AsSpan(0, charCount);
                            lock (outputBuilder)
                            {
                                AppendCapped(outputBuilder, textSpan);
                            }
                            DrainLines(stdoutBuf, textSpan, command.OutputHandler);
                        }
                    }
                }
                catch (OperationCanceledException) { /* Ignore */ }
                finally
                {
                    // Flush any bytes held back by the decoders (an incomplete trailing multibyte
                    // sequence at EOF) before returning the rented buffer, so no output is lost.
                    try
                    {
                        var tailOut = stdoutDecoder.GetChars(buffer, 0, 0, charBuf, 0, flush: true);
                        if (tailOut > 0)
                        {
                            var tailSpan = charBuf.AsSpan(0, tailOut);
                            lock (outputBuilder) { AppendCapped(outputBuilder, tailSpan); }
                            DrainLines(stdoutBuf, tailSpan, command.OutputHandler);
                        }
                        var tailErr = stderrDecoder.GetChars(buffer, 0, 0, charBuf, 0, flush: true);
                        if (tailErr > 0)
                        {
                            var tailSpan = charBuf.AsSpan(0, tailErr);
                            lock (outputBuilder) { AppendCapped(outputBuilder, tailSpan); }
                            DrainLines(stderrBuf, tailSpan, command.ErrorHandler);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Best-effort decoder flush; ignore decoding faults on the tail.
                    }

                    System.Buffers.ArrayPool<char>.Shared.Return(charBuf);
                    if (stdoutBuf.Length > 0)
                    {
                        var finalStdout = stdoutBuf.ToString();
                        SafeInvoke(() => command.OutputHandler?.Invoke(finalStdout));
                    }
                    if (stderrBuf.Length > 0)
                    {
                        var finalStderr = stderrBuf.ToString();
                        SafeInvoke(() => command.ErrorHandler?.Invoke(finalStderr));
                    }
                }
            }, CancellationToken.None);

            statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statsTask = CollectResourceStatsAsync(containerId, command, statsCts.Token);

            var logRank = _console.CurrentLevelRank;
            try
            {
                var wait = await _client.Containers.WaitContainerAsync(containerId, ct).ConfigureAwait(false);
                exitCode = wait.StatusCode;
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref wasCancelledFlag, 1);
                if (logRank >= RankErrors)
                    SafeInvoke(() => command.ErrorHandler?.Invoke("[Docker SDK] Container execution was cancelled."));
            }

            if (statsCts != null) await statsCts.CancelAsync().ConfigureAwait(false);

            // On a normal container exit, drain the attach stream to EOF instead of cancelling
            // the read loop immediately — output written just before the container stopped may
            // still be buffered in the stream and would otherwise be lost. Only force-cancel the
            // read loop on the genuine cancellation/timeout path, or if EOF does not arrive in
            // a bounded window.
            if (readTask != null)
            {
                if (Volatile.Read(ref wasCancelledFlag) != 0)
                {
                    if (readCts != null) await readCts.CancelAsync().ConfigureAwait(false);
                    await readTask.ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await readTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        if (readCts != null) await readCts.CancelAsync().ConfigureAwait(false);
                        await readTask.ConfigureAwait(false);
                    }
                }
            }

            try { profile = await statsTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Resource stats collection timed out", null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Resource stats collection failed", ex);
            }

            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
                if (inspect.State.OOMKilled)
                {
                    // Synthesize a minimal profile when OOM is detected but no stats sample was
                    // captured (very short-lived container), so the OOM condition is never dropped.
                    profile = profile is null ? new ResourceProfile(0, 0, 0, true) : profile with { OomKilled = true };
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (exitCode == 137)
                {
                    profile = profile is null ? new ResourceProfile(0, 0, 0, true) : profile with { OomKilled = true };
                }
            }

            containerStopwatch.Stop();

            var peakInfo = profile != null
              ? $", peak RAM: {profile.PeakMemoryBytes / (1024 * 1024)} MB, max CPU: {profile.MaxCpuPercent:F1}%"
               + (profile.OomKilled ? " (OOM-killed)" : "")
              : "";
            _console.SdkLog(command, $"[Docker SDK] Container {containerId.ShortId()} stopped — exit code {exitCode}, ran {containerStopwatch.Elapsed.TotalSeconds:F2}s{peakInfo}.", RankInfo);
            ranToCompletion = true;
        }
        finally
        {
            // DisposeAsync awaits an in-flight cancellation callback instead of blocking the thread on it.
            if (cancelRegistration is { } cancelReg)
            {
                await cancelReg.DisposeAsync().ConfigureAwait(false);
            }

            try { if (readCts != null) { await readCts.CancelAsync().ConfigureAwait(false); readCts.Dispose(); } } catch { /* Ignore */ }
            try { if (statsCts != null) { await statsCts.CancelAsync().ConfigureAwait(false); statsCts.Dispose(); } } catch { /* Ignore */ }

            if (readTask != null)
                try { await readTask.ConfigureAwait(false); } catch { /* Ignore */ }

            // Dispose the attach stream only after readTask has fully drained, so the read loop never
            // touches a disposed stream on the early-throw path (the using-declaration this replaced would
            // have disposed it as the try-scope unwound, before this finally awaited readTask).
            try { stream?.Dispose(); } catch { /* Ignore */ }

            // Observe statsTask so a late-completing collection cannot fault unobserved. MergeLateResourceProfile
            // keeps any earlier capture — in particular the OOM correction the inspect block applied above — and
            // adopts the late profile only when nothing was captured yet, so the OOM flag is never clobbered by
            // re-awaiting the (already-completed, OomKilled=false) stats task.
            if (statsTask != null)
            {
                try
                {
                    var lateProfile = await statsTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    profile = MergeLateResourceProfile(profile, lateProfile);
                }
                catch { /* Best-effort late capture; ignore. */ }
            }

            ContainerReaper.Untrack(containerId);

            // Defense in depth: if auto-remove was requested but the container never reached a
            // clean auto-removing exit, force-remove it so it does not linger. This covers two
            // paths: (1) start/attach/wait threw before completion; and (2) cancellation/timeout,
            // where the wait and inspect OperationCanceledExceptions are caught rather than
            // rethrown, so ranToCompletion is still set — without the wasCancelled clause the
            // container would be untracked here yet never force-removed, leaking it if the
            // fire-and-forget cancel-time stop also failed. Harmless 404 if Docker already reaped
            // it. Containers the user explicitly opted to keep (auto-remove off) are left untouched.
            // On the npipe logs-streaming path auto-remove was forced off (above) so the log stream
            // could drain, so that container is always force-removed here regardless of outcome.
            if (useLogsStreaming || (autoRemove && (!ranToCompletion || Volatile.Read(ref wasCancelledFlag) != 0)))
            {
                try
                {
                    await _client.Containers.RemoveContainerAsync(containerId,
                        new ContainerRemoveParameters { Force = true }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Already removed, or daemon unreachable — nothing more to do.
                }
            }
        }

        string finalOutput;
        lock (outputBuilder) { finalOutput = outputBuilder.ToString(); }

        return (exitCode, finalOutput, Volatile.Read(ref wasCancelledFlag) != 0, profile);
    }
}
