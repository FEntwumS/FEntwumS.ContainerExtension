using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Essentials.ToolEngine;
using OneWare.Essentials.Services;
using OneWare.Essentials.Models;
using ContainerExtension;

namespace ContainerBenchmarkHarness;

/// <summary>
/// Standalone CLI harness that exercises the <see cref="DockerExecutionStrategy"/>
/// outside of the OneWare Studio IDE, enabling the Python benchmark script
/// (<c>benchmark.py --backend dotnet</c>) to measure Docker.DotNet SDK overhead
/// in isolation.
/// <para>
/// <b>Usage:</b> <c>dotnet run -- &lt;tool&gt; [args...]</c><br/>
/// <b>Example:</b> <c>dotnet run -- ghdl --version</c>
/// </para>
/// </summary>
sealed class Program
{
    /// <summary>Entry point — parses CLI args and runs a single container execution or stress tests telemetry.</summary>
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <command> [args...]");
            Console.WriteLine("   or: dotnet run -- stress-telemetry [--processes M] [--threads N] [--iterations K]");
            return 1;
        }

        if (args[0].Equals("stress-telemetry", StringComparison.OrdinalIgnoreCase))
        {
            return await RunStressTestAsync(args);
        }

        // Bootstrap a minimal DI container with a mock settings service
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService, MockSettingsService>();
        var sp = services.BuildServiceProvider();

        var strategy = new DockerExecutionStrategy(sp);

        // Parse the tool name and its arguments
        var toolName = args[0];
        var toolArgs = new string[args.Length - 1];
        Array.Copy(args, 1, toolArgs, 0, args.Length - 1);

        var command = new ToolCommand
        {
            ToolName = toolName,
            Executable = toolName,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Arguments = toolArgs,
            OutputHandler = msg => { Console.WriteLine(msg); return true; },
            ErrorHandler = msg => { Console.Error.WriteLine(msg); return true; },
            StatusMessage = "Running Container Benchmark",
            State = OneWare.Essentials.Enums.AppState.Loading,
            ShowTimer = false
        };

        var result = await strategy.ExecuteAsync(command);
        return result.success ? 0 : 1;
    }

    private static async Task<int> RunStressTestAsync(string[] args)
    {
        int processes = 1;
        int threads = 10;
        int iterations = 100;
        bool isChild = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--processes" && i + 1 < args.Length) processes = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--iterations" && i + 1 < args.Length) iterations = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (args[i] == "--child") isChild = true;
        }

        var pid = Environment.ProcessId;
        var prefix = isChild ? $"[Child {pid}]" : $"[Parent {pid}]";

        // If parent and processes > 1, spawn children
        var childProcs = new System.Collections.Generic.List<System.Diagnostics.Process>();
        if (!isChild && processes > 1)
        {
            await Console.Out.WriteLineAsync($"{prefix} Spawning {processes - 1} child processes...");
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                await Console.Error.WriteLineAsync($"{prefix} Error: Unable to determine executable path.");
                return 1;
            }

            for (int i = 1; i < processes; i++)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    // If running via dotnet, args are passed differently, but since it's an exe (or `dotnet run` delegates), we pass the args directly
                    // It's safer to reconstruct the exact command line args
                    Arguments = $"stress-telemetry --processes {processes} --threads {threads} --iterations {iterations} --child",
                    UseShellExecute = false
                };
                var p = System.Diagnostics.Process.Start(psi);
                if (p != null) childProcs.Add(p);
            }
        }

        await Console.Out.WriteLineAsync($"{prefix} Starting telemetry stress test: {threads} threads, {iterations} iterations per thread.");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int successCount = 0;
        int errorCount = 0;

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
                        await Console.Error.WriteLineAsync($"{prefix} Error on T{threadId} I{i}: {ex.Message}");
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        await Console.Out.WriteLineAsync($"{prefix} Done in {sw.ElapsedMilliseconds}ms. Success: {successCount}, Errors: {errorCount}");

        if (!isChild && childProcs.Count > 0)
        {
            await Console.Out.WriteLineAsync($"{prefix} Waiting for {childProcs.Count} child processes to finish...");
            foreach (var cp in childProcs)
            {
                cp.WaitForExit();
                if (cp.ExitCode != 0)
                {
                    await Console.Error.WriteLineAsync($"{prefix} Child {cp.Id} exited with code {cp.ExitCode}");
                    errorCount++;
                }
            }
            await Console.Out.WriteLineAsync($"{prefix} All child processes finished.");
        }

        return errorCount == 0 ? 0 : 1;
    }
}

/// <summary>
/// Minimal mock of <see cref="ISettingsService"/> for standalone use outside the IDE.
/// Returns sensible defaults matching the plugin's registered settings to ensure
/// behavioral parity between IDE and harness execution.
/// </summary>
#pragma warning disable CA1822 // Interface stubs cannot be static
sealed class MockSettingsService : ISettingsService
{
    private static readonly Dictionary<string, object> Defaults = new()
    {
        [ContainerExtensionModule.AutoRemoveSetting] = true,
        [ContainerExtensionModule.DefaultImageSetting] = ContainerExtensionModule.FallbackImage,
        [ContainerExtensionModule.DockerRuntimePathSetting] = "",
        [ContainerExtensionModule.MemoryLimitSetting] = 0.0,       // SliderSetting (0 = no limit)
        [ContainerExtensionModule.CpuLimitSetting] = 0.0,          // SliderSetting (0 = no limit)
        [ContainerExtensionModule.DaemonSocketSetting] = "",
        [ContainerExtensionModule.PlatformSetting] = "auto",            // ComboBoxSetting
        [ContainerExtensionModule.TimeoutSetting] = 0.0,            // SliderSetting (0 = no timeout)
        [ContainerExtensionModule.NetworkModeSetting] = "bridge",   // ComboBoxSetting
        [ContainerExtensionModule.LogLevelSetting] = "Verbose",      // ComboBoxSetting
        [ContainerExtensionModule.ShowTimestampsSetting] = true,     // CheckBoxSetting
        [ContainerExtensionModule.PullPolicySetting] = "if-not-present", // ComboBoxSetting
        [ContainerExtensionModule.ExtraFlagsSetting] = "",           // TextBoxSetting (container labels)
        [ContainerExtensionModule.DashboardRefreshSetting] = "Manual", // ComboBoxSetting
        [ContainerExtensionModule.ContainerNamePrefixSetting] = "containerextension-", // TextBoxSetting
        [ContainerExtensionModule.TelemetryRetentionSetting] = "100" // ComboBoxSetting
    };

    public event EventHandler<SaveEventArgs>? Saved = delegate { };

    public T GetSettingValue<T>(string key)
    {
        if (Defaults.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default(T)!;
    }

    // ── Stub implementations (not exercised in benchmark mode) ──────────
    public void RegisterSettingCategory(string category, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory) { }
    public void Register<T>(string key, T setting) { }
    public IObservable<T> Bind<T>(string key, IObservable<T> observable) => observable;
    public void RegisterTitled<T>(string category, string subCategory, string key, string title, string description, T defaultValue) { }
    public void RegisterTitledFolderPath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator) { }
    public void RegisterTitledFilePath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator, params Avalonia.Platform.Storage.FilePickerFileType[] fileTypes) { }
    public void RegisterTitledSlider(string category, string subCategory, string key, string title, string description, double defaultValue, double min, double max, double tick) { }
    public void RegisterTitledCombo<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledComboSearch<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledListBox(string category, string subCategory, string key, string title, string description, params string[] options) { }
    public void RegisterSetting(string category, string subCategory, string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterSetting(string category, string subCategory, string key, object settingModule) { }
    public void UpdateSetting(string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterCustom(string category, string subCategory, string key, OneWare.Essentials.Models.CustomSetting setting) { }
    public OneWare.Essentials.Models.Setting GetSetting(string key) => null!;
    public bool HasSetting(string key) => false;
    public T[] GetComboOptions<T>(string key) => Array.Empty<T>();
    public void SetSettingValue(string key, object value) { }
    public IObservable<T> GetSettingObservable<T>(string key) => System.Reactive.Linq.Observable.Empty<T>();
    public void Load(string path) { }
    public void Save(string path, bool overrideExisting) { }
    public void WhenLoaded(Action action) { }
    public void Reset(string key) { }
    public void ResetAll() { }
}
#pragma warning restore CA1822
