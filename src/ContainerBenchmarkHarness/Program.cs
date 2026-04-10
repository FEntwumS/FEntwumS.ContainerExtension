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
    /// <summary>Entry point — parses CLI args and runs a single container execution.</summary>
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <command> [args...]");
            return 1;
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
