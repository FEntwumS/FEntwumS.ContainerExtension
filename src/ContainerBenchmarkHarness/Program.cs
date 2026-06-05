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

namespace ContainerBenchmarkHarness;

sealed class Program
{
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
        if (!isChild && processes > 1)
        {
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
                    _ = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();
                    childProcs.Add(p);
                }
            }
        }

        await Console.Out.WriteLineAsync($"{prefix} Starting telemetry stress test: {threads} threads, {iterations} iterations per thread.").ConfigureAwait(false);
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
                        await Console.Error.WriteLineAsync($"{prefix} Error on T{threadId} I{i}: {ex.Message}").ConfigureAwait(false);
                    }
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        await Console.Out.WriteLineAsync($"{prefix} Done in {sw.ElapsedMilliseconds}ms. Success: {successCount}, Errors: {errorCount}").ConfigureAwait(false);

        if (!isChild && childProcs.Count > 0)
        {
            await Console.Out.WriteLineAsync($"{prefix} Waiting for {childProcs.Count} child processes to finish...").ConfigureAwait(false);

            // Execute parallelized awaits with guaranteed unmanaged OS handle cleanup
            await Task.WhenAll(childProcs.Select(async cp =>
            {
                using (cp) // CRITICAL: Free OS process handle
                {
                    await cp.WaitForExitAsync().ConfigureAwait(false);
                    if (cp.ExitCode != 0)
                    {
                        await Console.Error.WriteLineAsync($"{prefix} Child {cp.Id} exited with code {cp.ExitCode}").ConfigureAwait(false);
                        Interlocked.Increment(ref errorCount);
                    }
                }
            })).ConfigureAwait(false);

            await Console.Out.WriteLineAsync($"{prefix} All child processes finished.").ConfigureAwait(false);
        }

        return errorCount == 0 ? 0 : 1;
    }
}

#pragma warning disable CA1822 // Interface stubs cannot be static
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated via DI")]
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

    // Must check dictionary to ensure SafeGetSetting resolves the defaults accurately
    public bool HasSetting(string key) => Defaults.ContainsKey(key);

    public T GetSettingValue<T>(string key)
    {
        if (Defaults.TryGetValue(key, out var value))
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
    public OneWare.Essentials.Models.Setting GetSetting(string key)
    {
        if (Defaults.TryGetValue(key, out var defaultValue))
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

internal sealed class TestCommandArgument : ICommandArgument
{
    private string _argument;
    public TestCommandArgument(string argument)
    {
        _argument = argument;
    }
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
    public string GetArgument() => _argument;
}
