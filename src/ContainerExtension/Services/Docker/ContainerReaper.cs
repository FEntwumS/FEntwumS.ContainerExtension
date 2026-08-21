using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Tracks live containers and force-reaps them on process exit, Ctrl-C, or strategy disposal, so an abrupt
/// shutdown cannot leave orphaned containers behind. Process-global by nature — a single set of exit
/// handlers and one tracked-container registry — hence static: the first client to <see cref="TryArm"/>
/// becomes the owner and drives teardown.
/// </summary>
internal static class ContainerReaper
{
    private static readonly ConcurrentDictionary<string, bool> Active = new(StringComparer.Ordinal);
    private static DockerClient? _clientForCleanup;
    private static int _cleanupExecuted;
    private static ConsoleCancelEventHandler? _cancelKeyPressHandler;

    /// <summary>Begin tracking a container (by ID or name) so it is stopped, and optionally removed, on shutdown.</summary>
    internal static void Track(string idOrName, bool autoRemove) => Active.TryAdd(idOrName, autoRemove);

    /// <summary>Stop tracking a container that has already been dealt with.</summary>
    internal static void Untrack(string idOrName) => Active.TryRemove(idOrName, out _);

    /// <summary>
    /// Installs process-exit and Ctrl-C reaping for <paramref name="client"/>, once per process. Returns
    /// true if this call became the owner (installed the handlers); false if another client already owns
    /// teardown.
    /// </summary>
    internal static bool TryArm(DockerClient client)
    {
        if (Interlocked.CompareExchange(ref _clientForCleanup, client, null) is null)
        {
            AppDomain.CurrentDomain.ProcessExit += OnShutdown;
            _cancelKeyPressHandler = (s, e) => OnShutdown(s, e);
            Console.CancelKeyPress += _cancelKeyPressHandler;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Disposal counterpart of <see cref="TryArm"/>: if <paramref name="client"/> is the current owner,
    /// unregisters the handlers and reaps its tracked containers with the still-valid client, then re-enables
    /// reaping so a later owner can still clean up on exit. A null or non-owning client is a no-op.
    /// </summary>
    internal static void Disarm(DockerClient? client)
    {
        if (client != null &&
            Interlocked.CompareExchange(ref _clientForCleanup, null, client) == client)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnShutdown;
            if (_cancelKeyPressHandler != null)
            {
                try
                {
                    Console.CancelKeyPress -= _cancelKeyPressHandler;
                }
                catch
                {
                    // Ignore Console unregistration errors on shutdown
                }
                _cancelKeyPressHandler = null;
            }

            // The CAS above already nulled the owner field, so the unregistered ProcessExit handler is now a
            // no-op and cannot perform this cleanup; run it here with the captured client. Guard against a
            // ProcessExit firing concurrently, then re-arm so a later strategy instance still cleans up on exit.
            if (Interlocked.Exchange(ref _cleanupExecuted, 1) == 0)
            {
                ReapAll(client);
            }
            Volatile.Write(ref _cleanupExecuted, 0);
        }
    }

    private static void OnShutdown(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cleanupExecuted, 1) != 0)
        {
            return;
        }
        var client = _clientForCleanup;
        if (client == null)
        {
            return;
        }
        ReapAll(client);
    }

    // Stops and force-removes every tracked container using the supplied client, concurrently, under a
    // single shared time budget, rather than blocking the caller (Dispose can run on the UI thread) for up
    // to 2 s per container in sequence.
    private static void ReapAll(DockerClient client)
    {
        var keys = Active.Keys;
        if (keys.Count == 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();
        foreach (var key in keys)
        {
            if (Active.TryRemove(key, out var shouldAutoRemove))
            {
                tasks.Add(StopAndRemoveContainerAsync(client, key, shouldAutoRemove, cts.Token));
            }
        }

        try
        {
            // Synchronous block is intentional: the reaper runs from Dispose and the ProcessExit handler,
            // neither of which has an async context to await into. Scope the suppression to this one site.
#pragma warning disable VSTHRD002
            Task.WhenAll(tasks).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
        catch (Exception)
        {
            // Best effort on exit
        }
    }

    private static async Task StopAndRemoveContainerAsync(DockerClient client, string key, bool shouldAutoRemove, CancellationToken ct)
    {
        try
        {
            await client.Containers.StopContainerAsync(key, new ContainerStopParameters { WaitBeforeKillSeconds = 1 }, ct).ConfigureAwait(false);
            if (shouldAutoRemove)
            {
                await client.Containers.RemoveContainerAsync(key, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best effort per container
        }
    }
}
