using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ContainerExtension;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

public sealed class ToolRegistrationTests
{
    [Fact]
    public void WatchToolRegistrations_WhenAToolRegistersLater_ReportsIt()
    {
        var tools = new ObservableCollection<ToolContext> { Tool("yosys") };
        var reported = new List<string>();
        using var subscription = ContainerExtensionModule.WatchToolRegistrations(tools, added => reported.AddRange(added.Select(t => t.Key)));

        tools.Add(Tool("ghdl"));

        Assert.Equal(["ghdl"], reported);
    }

    [Fact]
    public void WatchToolRegistrations_WhenAToolTakesThePlaceOfARemovedOne_ReportsIt()
    {
        // The number of tools stays the same, which is why counting them missed the new one.
        var tools = new ObservableCollection<ToolContext> { Tool("yosys") };
        var reported = new List<string>();
        using var subscription = ContainerExtensionModule.WatchToolRegistrations(tools, added => reported.AddRange(added.Select(t => t.Key)));

        tools.RemoveAt(0);
        tools.Add(Tool("ghdl"));

        Assert.Single(tools);
        Assert.Equal(["ghdl"], reported);
    }

    [Fact]
    public void WatchToolRegistrations_WhenToolsAreMovedRemovedOrCleared_ReportsNothing()
    {
        var tools = new ObservableCollection<ToolContext> { Tool("yosys"), Tool("ghdl") };
        var reported = new List<string>();
        using var subscription = ContainerExtensionModule.WatchToolRegistrations(tools, added => reported.AddRange(added.Select(t => t.Key)));

        tools.Move(0, 1);
        tools.RemoveAt(0);
        tools.Clear();

        Assert.Empty(reported);
    }

    [Fact]
    public void WatchToolRegistrations_AfterDispose_ReportsNothing()
    {
        var tools = new ObservableCollection<ToolContext>();
        var reported = new List<string>();
        var subscription = ContainerExtensionModule.WatchToolRegistrations(tools, added => reported.AddRange(added.Select(t => t.Key)));

        subscription.Dispose();
        tools.Add(Tool("ghdl"));

        Assert.Empty(reported);
    }

    [Fact]
    public void EnsurePerToolImageSettings_RegistersEachMissingSettingOnce()
    {
        var settings = new RecordingSettingsService();
        var toolService = new StrategyConfigurationToolService()
            .WithConfiguration("ghdl", ContainerExtensionModule.StrategyConfigurationImageKey, "hdlc/ghdl:yosys");
        ToolContext[] tools = [Tool("ghdl")];

        ContainerExtensionModule.EnsurePerToolImageSettings(tools, toolService, settings);
        ContainerExtensionModule.EnsurePerToolImageSettings(tools, toolService, settings);

        Assert.Equal(1, settings.RegistrationCount);
        var setting = Assert.IsType<TextBoxSetting>(settings.Registered[$"{ContainerExtensionModule.PerToolImagePrefix}ghdl"]);
        Assert.Equal("Container Image for ghdl", setting.Title);
        Assert.Equal("hdlc/ghdl:yosys", setting.Watermark);
    }

    private static ToolContext Tool(string key) => new(key, "Synth Tool", key);
}

// ISettingsService fake that records the titled settings registered with it and, like OneWare's settings
// service, refuses a key registered twice.
internal sealed class RecordingSettingsService : ISettingsService
{
    private readonly Dictionary<string, TitledSetting> _registered = new(StringComparer.Ordinal);

    public event EventHandler<SaveEventArgs>? Saved = delegate { };

    public IReadOnlyDictionary<string, TitledSetting> Registered => _registered;

    public int RegistrationCount { get; private set; }

    public bool HasSetting(string key) => _registered.ContainsKey(key);
    public T GetSettingValue<T>(string key) => default!;
    public void SetSettingValue(string key, object value) { }

    public void RegisterSetting(string category, string subCategory, string key, TitledSetting setting)
    {
        RegistrationCount++;
        _registered.Add(key, setting);
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
    public void RegisterSetting(string category, string subCategory, string key, object settingModule) { }
    public void UpdateSetting(string key, TitledSetting setting) { }
    public void RegisterCustom(string category, string subCategory, string key, CustomSetting setting) { }
    public Setting GetSetting(string key) => null!;
    public T[] GetComboOptions<T>(string key) => Array.Empty<T>();
    public IObservable<T> GetSettingObservable<T>(string key) => System.Reactive.Linq.Observable.Empty<T>();
    public void Load(string path) { }
    public void Save(string path, bool overrideExisting) { }
    public void WhenLoaded(Action action) { }
    public void Reset(string key) { }
    public void ResetAll() { }
}
