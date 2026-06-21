using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Essentials.ToolEngine;
using OneWare.Essentials.Services;
using OneWare.Essentials.Models;
using ContainerExtension;
using System.Collections.Generic;

#pragma warning disable CA1303 // Do not pass literals as localized parameters
#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable CA1822 // Mark members as static

namespace ContainerBenchmarkHarness;

/// <summary>
/// The main entry point and execution coordinator for the container benchmark harness.
/// </summary>
sealed class Program
{
    /// <summary>
    /// The entry point for the benchmark harness application.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the harness.</param>
    /// <returns>An exit code where 0 indicates success and any other value indicates failure.</returns>
    static async Task<int> Main(string[] args)
    {
        // Force compilation of generic list/dictionary constructors for Native-AOT / Newtonsoft.Json
        _ = new List<string>();
        _ = new Dictionary<string, string>();

        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <command> [args...]");
            Console.WriteLine("   or: dotnet run -- stress-telemetry [--processes M] [--threads N] [--iterations K]");
            return 1;
        }

        if (args[0].Equals("stress-telemetry", StringComparison.OrdinalIgnoreCase))
        {
            return await RunStressTestAsync(args).ConfigureAwait(false);
        }

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService, MockSettingsService>();
        var sp = services.BuildServiceProvider();

        DockerExecutionStrategy? strategy = null;
        try
        {
            try
            {
                strategy = new DockerExecutionStrategy(sp);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Console.Error.WriteLineAsync($"Benchmark initialization failed: Unable to connect to local Docker socket. Details: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            var toolName = args[0];
            var toolArgs = new string[args.Length - 1];
            Array.Copy(args, 1, toolArgs, 0, args.Length - 1);

            var command = new ToolCommand
            {
                ToolName = toolName,
                Executable = toolName,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                CommandArguments = toolArgs.Select(arg => new TestCommandArgument(arg)).Cast<ICommandArgument>().ToList(),
                OutputHandler = msg => { Console.WriteLine(msg); return true; },
                ErrorHandler = msg => { Console.Error.WriteLine(msg); return true; },
                StatusMessage = "Running Container Benchmark",
                State = OneWare.Essentials.Enums.AppState.Loading,
                ShowTimer = false
            };

            var result = await strategy.ExecuteAsync(command).ConfigureAwait(false);
            return result.success ? 0 : 1;
        }
        finally
        {
            strategy?.Dispose();
        }
    }

    /// <summary>
    /// Runs a concurrent telemetry stress test to measure system stability under load.
    /// </summary>
    /// <param name="args">The command-line arguments specifying test parameters like processes, threads, and iterations.</param>
    /// <returns>An exit code where 0 indicates success and any other value indicates failure.</returns>
    private static async Task<int> RunStressTestAsync(string[] args)
    {
        int processes = 1;
        int threads = 10;
        int iterations = 100;
        bool isChild = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--processes", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                processes = Math.Clamp(int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture), 1, 16);
            }
            else if (args[i].Equals("--threads", StringComparison.Ordinal) && i + 1 < args.Length) threads = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i].Equals("--iterations", StringComparison.Ordinal) && i + 1 < args.Length) iterations = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i].Equals("--child", StringComparison.Ordinal)) isChild = true;
        }

        if (threads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Threads must be a positive integer.");
        }
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Iterations must be a positive integer.");
        }

        var pid = Environment.ProcessId;
        var prefix = isChild ? $"[Child {pid}]" : $"[Parent {pid}]";
        var childProcs = new List<System.Diagnostics.Process>(processes > 1 ? processes - 1 : 0);
        var childTasks = new List<Task>();
        bool success = false;
        int errorCount = 0;

        EventHandler processExitHandler = (s, e) => KillChildProcesses(childProcs);
        ConsoleCancelEventHandler cancelKeyHandler = (s, e) => KillChildProcesses(childProcs);

        try
        {
            if (!isChild && processes > 1)
            {
                AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                Console.CancelKeyPress += cancelKeyHandler;

                await Console.Out.WriteLineAsync($"{prefix} Spawning {processes - 1} child processes...").ConfigureAwait(false);
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    await Console.Error.WriteLineAsync($"{prefix} Error: Unable to determine executable path.").ConfigureAwait(false);
                    return 1;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"stress-telemetry --processes {processes} --threads {threads} --iterations {iterations} --child",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                for (int i = 1; i < processes; i++)
                {
                    var p = System.Diagnostics.Process.Start(psi);
                    if (p != null)
                    {
                        lock (childProcs)
                        {
                            childProcs.Add(p);
                        }
                        var localProcess = p;
                        var processTask = Task.Run(async () =>
                        {
                            try
                            {
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                var stdoutTask = localProcess.StandardOutput.ReadToEndAsync(cts.Token);
                                var stderrTask = localProcess.StandardError.ReadToEndAsync(cts.Token);
                                var exitTask = localProcess.WaitForExitAsync(cts.Token);

                                await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

                                if (localProcess.ExitCode != 0)
                                {
                                    await Console.Error.WriteLineAsync($"{prefix} Child {localProcess.Id} exited with code {localProcess.ExitCode}").ConfigureAwait(false);
                                    Interlocked.Increment(ref errorCount);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                await Console.Error.WriteLineAsync($"{prefix} Child {localProcess.Id} hang detected (timed out after 10 seconds).").ConfigureAwait(false);
                                Interlocked.Increment(ref errorCount);
                                try { localProcess.Kill(entireProcessTree: true); } catch { }
                            }
                            catch (Exception ex)
                            {
                                await Console.Error.WriteLineAsync($"{prefix} Child {localProcess.Id} task exception: {ex.Message}").ConfigureAwait(false);
                            }
                        });
                        childTasks.Add(processTask);
                    }
                }
            }

            await Console.Out.WriteLineAsync($"{prefix} Starting telemetry stress test: {threads} threads, {iterations} iterations per thread.").ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            int successCount = 0;

            var tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                int threadId = t;
                tasks[t] = Task.Run(async () =>
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        try
                        {
                            ContainerTelemetry.LogExecution(
                                image: "stress-test-image",
                                tool: $"stress-test-{pid}-{threadId}",
                                durationSeconds: 0.042 + (i % 10),
                                exitCode: 0,
                                imageDigest: null,
                                wasCancelled: false,
                                dockerRunCommand: $"--iter {i}",
                                peakMemoryBytes: 1024 * 1024,
                                maxCpuPercent: 5.5,
                                oomKilled: false,
                                maxEntries: 10000,
                                errorMessage: null
                            );
                            Interlocked.Increment(ref successCount);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errorCount);
                            await Console.Error.WriteLineAsync($"{prefix} Error on T{threadId} I{i}: {ex.Message}").ConfigureAwait(false);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            sw.Stop();

            await Console.Out.WriteLineAsync($"{prefix} Done in {sw.ElapsedMilliseconds}ms. Success: {successCount}, Errors: {errorCount}").ConfigureAwait(false);

            if (!isChild && childTasks.Count > 0)
            {
                await Console.Out.WriteLineAsync($"{prefix} Waiting for {childTasks.Count} child processes to finish...").ConfigureAwait(false);
                await Task.WhenAll(childTasks).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"{prefix} All child processes finished.").ConfigureAwait(false);
            }
            success = true;
        }
        finally
        {
            if (!isChild && processes > 1)
            {
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                Console.CancelKeyPress -= cancelKeyHandler;
            }

            if (!success && !isChild && childProcs.Count > 0)
            {
                await Console.Out.WriteLineAsync($"{prefix} Failure encountered. Cleaning up child processes...").ConfigureAwait(false);
                KillChildProcesses(childProcs);
            }

            if (!isChild && childTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(childTasks).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }

            List<System.Diagnostics.Process> childProcsCopy;
            lock (childProcs)
            {
                childProcsCopy = new List<System.Diagnostics.Process>(childProcs);
            }
            foreach (var cp in childProcsCopy)
            {
                try
                {
                    cp.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }

        return errorCount == 0 ? 0 : 1;
    }

    private static void KillChildProcesses(List<System.Diagnostics.Process> childProcs)
    {
        List<System.Diagnostics.Process> childProcsCopy;
        lock (childProcs)
        {
            childProcsCopy = new List<System.Diagnostics.Process>(childProcs);
        }
        foreach (var cp in childProcsCopy)
        {
            try
            {
                if (!cp.HasExited)
                {
                    cp.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore
            }
        }
    }
}

/// <summary>
/// A mocked implementation of the OneWare settings service used by the benchmark harness.
/// Allows setting and querying mock configuration preferences without the full OneWare Studio runtime.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated via DI")]
sealed class MockSettingsService : ISettingsService
{
    private static readonly Dictionary<string, object> DefaultSettings = new(StringComparer.Ordinal)
    {
        [ContainerExtensionModule.AutoRemoveSetting] = true,
        [ContainerExtensionModule.DefaultImageSetting] = ContainerExtensionModule.FallbackImage,
        [ContainerExtensionModule.DockerRuntimePathSetting] = "",
        [ContainerExtensionModule.MemoryLimitSetting] = 0.0,
        [ContainerExtensionModule.CpuLimitSetting] = 0.0,
        [ContainerExtensionModule.DaemonSocketSetting] = "",
        [ContainerExtensionModule.PlatformSetting] = "auto",
        [ContainerExtensionModule.TimeoutSetting] = 0.0,
        [ContainerExtensionModule.NetworkModeSetting] = "bridge",
        [ContainerExtensionModule.LogLevelSetting] = "Verbose",
        [ContainerExtensionModule.ShowTimestampsSetting] = true,
        [ContainerExtensionModule.PullPolicySetting] = "if-not-present",
        [ContainerExtensionModule.ExtraFlagsSetting] = "",
        [ContainerExtensionModule.DashboardRefreshSetting] = "Manual",
        [ContainerExtensionModule.ContainerNamePrefixSetting] = "containerextension-",
        [ContainerExtensionModule.TelemetryRetentionSetting] = "100",
        [ContainerExtensionModule.BypassNamedPipeCheckSetting] = false,
        [ContainerExtensionModule.AllowNativeFallbackSetting] = false
    };

    private readonly Dictionary<string, object> _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="MockSettingsService"/> class populated with default settings.
    /// </summary>
    public MockSettingsService()
    {
        _settings = new Dictionary<string, object>(DefaultSettings, StringComparer.Ordinal);
    }

    /// <summary>
    /// Fired when a setting is saved. Stubbed for compatibility.
    /// </summary>
    public event EventHandler<SaveEventArgs>? Saved = delegate { };

    /// <summary>
    /// Checks if a setting is present in the configuration dictionary.
    /// </summary>
    /// <param name="key">The configuration key name.</param>
    /// <returns>True if the key exists; otherwise, false.</returns>
    public bool HasSetting(string key) => _settings.ContainsKey(key);

    /// <summary>
    /// Retrieves a typed setting value from the configuration.
    /// </summary>
    /// <typeparam name="T">The type of the setting.</typeparam>
    /// <param name="key">The configuration key name.</param>
    /// <returns>The typed value if present; otherwise, default.</returns>
    public T GetSettingValue<T>(string key)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            if (value is T typed)
            {
                return typed;
            }
            try
            {
                return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return default(T)!;
            }
        }
        return default(T)!;
    }

    /// <summary>Stubs for categories registration.</summary>
    public void RegisterSettingCategory(string category, int order, string? icon) { }
    /// <summary>Stubs for subcategories registration.</summary>
    public void RegisterSettingSubCategory(string category, string subCategory, int order, string? icon) { }
    /// <summary>Stubs for subcategories registration.</summary>
    public void RegisterSettingSubCategory(string category, string subCategory) { }
    /// <summary>Stubs for registry registration.</summary>
    public void Register<T>(string key, T setting) { }
    /// <summary>Stubs for setting binding.</summary>
    public IObservable<T> Bind<T>(string key, IObservable<T> observable) => observable;
    /// <summary>Stubs for setting registration.</summary>
    public void RegisterTitled<T>(string category, string subCategory, string key, string title, string description, T defaultValue) { }
    /// <summary>Stubs for folder path setting registration.</summary>
    public void RegisterTitledFolderPath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator) { }
    /// <summary>Stubs for file path setting registration.</summary>
    public void RegisterTitledFilePath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator, params Avalonia.Platform.Storage.FilePickerFileType[] fileTypes) { }
    /// <summary>Stubs for slider setting registration.</summary>
    public void RegisterTitledSlider(string category, string subCategory, string key, string title, string description, double defaultValue, double min, double max, double tick) { }
    /// <summary>Stubs for combo setting registration.</summary>
    public void RegisterTitledCombo<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    /// <summary>Stubs for search combo setting registration.</summary>
    public void RegisterTitledComboSearch<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    /// <summary>Stubs for list box setting registration.</summary>
    public void RegisterTitledListBox(string category, string subCategory, string key, string title, string description, params string[] options) { }
    /// <summary>Stubs for setting registration.</summary>
    public void RegisterSetting(string category, string subCategory, string key, OneWare.Essentials.Models.TitledSetting setting) { }
    /// <summary>Stubs for setting registration.</summary>
    public void RegisterSetting(string category, string subCategory, string key, object settingModule) { }
    /// <summary>Stubs for setting updates.</summary>
    public void UpdateSetting(string key, OneWare.Essentials.Models.TitledSetting setting) { }
    /// <summary>Stubs for custom setting registration.</summary>
    public void RegisterCustom(string category, string subCategory, string key, OneWare.Essentials.Models.CustomSetting setting) { }

    /// <summary>
    /// Resolves and builds a mocked OneWare Setting wrapper object for a configuration key.
    /// </summary>
    /// <param name="key">The configuration key name.</param>
    /// <returns>A typed OneWare setting descriptor.</returns>
    public OneWare.Essentials.Models.Setting GetSetting(string key)
    {
        if (_settings.TryGetValue(key, out var defaultValue))
        {
            if (defaultValue is bool b)
            {
                return new CheckBoxSetting(key, b);
            }
            if (defaultValue is string s)
            {
                return new TextBoxSetting(key, s, null);
            }
            if (defaultValue is double d)
            {
                return new SliderSetting(key, d, 0, d * 2, 1);
            }
        }
        return new TextBoxSetting(key, "", null);
    }

    /// <summary>Stubs for combo options retrieval.</summary>
    public T[] GetComboOptions<T>(string key) => Array.Empty<T>();

    /// <summary>
    /// Modifies a setting value in the configuration dynamically.
    /// </summary>
    /// <param name="key">The configuration key name.</param>
    /// <param name="value">The new setting value.</param>
    public void SetSettingValue(string key, object value)
    {
        _settings[key] = value;
    }

    /// <summary>Stubs for settings observable binding.</summary>
    public IObservable<T> GetSettingObservable<T>(string key) => System.Reactive.Linq.Observable.Empty<T>();
    /// <summary>Stubs for settings loading.</summary>
    public void Load(string path) { }
    /// <summary>Stubs for settings saving.</summary>
    public void Save(string path, bool overrideExisting) { }
    /// <summary>Stubs for initialization hooks.</summary>
    public void WhenLoaded(Action action) { }
    /// <summary>Stubs for settings resetting.</summary>
    public void Reset(string key) { }
    /// <summary>Stubs for settings resetting.</summary>
    public void ResetAll() { }
}
#pragma warning restore CA1822

/// <summary>
/// A mock implementation of <see cref="ICommandArgument"/> used for testing and benchmarking commands.
/// </summary>
internal sealed class TestCommandArgument : ICommandArgument
{
    private string _argument;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestCommandArgument"/> class with the specified argument.
    /// </summary>
    /// <param name="argument">The string argument representation.</param>
    public TestCommandArgument(string argument)
    {
        _argument = argument;
    }

    /// <summary>
    /// Prepares the command argument for execution on the target operating system, applying path mapping if necessary.
    /// </summary>
    /// <param name="osPlatform">The target operating system platform.</param>
    /// <param name="pathMapper">An optional function to map file paths from host to container.</param>
    public void Prepare(System.Runtime.InteropServices.OSPlatform osPlatform, Func<string, string>? pathMapper = null)
    {
        if (pathMapper != null && ContainerExtension.Services.Docker.DockerCommandBuilder.ShouldMapArgument(_argument))
        {
            var mapped = pathMapper(_argument);
            if (mapped != null)
            {
                _argument = mapped;
            }
        }
    }

    /// <summary>
    /// Gets the string representation of the prepared command argument.
    /// </summary>
    /// <returns>The command argument string.</returns>
    public string GetArgument() => _argument;
}
