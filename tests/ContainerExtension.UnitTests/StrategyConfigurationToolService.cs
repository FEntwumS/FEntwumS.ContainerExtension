using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;

namespace ContainerExtension.UnitTests;

/// <summary>
/// A tool service that knows nothing but the strategy configuration each tool's plugin declares, which is
/// all the Docker strategy asks it for when it picks an image.
/// </summary>
internal sealed class StrategyConfigurationToolService : IToolService
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _configurations = new(StringComparer.Ordinal);

    public StrategyConfigurationToolService WithConfiguration(string toolKey, string key, string value)
    {
        _configurations[toolKey] = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
        return this;
    }

    public IReadOnlyDictionary<string, string> GetStrategyConfiguration(string toolKey)
        => _configurations.TryGetValue(toolKey, out var configuration) ? configuration : new Dictionary<string, string>(StringComparer.Ordinal);

    public void Register(ToolContext description) => throw new NotSupportedException();

    public void Unregister(ToolContext description) => throw new NotSupportedException();

    public void Unregister(string toolKey) => throw new NotSupportedException();

    public ObservableCollection<ToolContext> GetAllTools() => throw new NotSupportedException();

    public ToolConfiguration GetGlobalToolConfiguration() => throw new NotSupportedException();

    public void RegisterStrategy(IToolExecutionStrategy strategy) => throw new NotSupportedException();

    public void RegisterStrategy(IToolExecutionStrategy strategy, IReadOnlyCollection<string> supportedToolKeys) => throw new NotSupportedException();

    public void RegisterStrategy(IToolExecutionStrategy strategy, Func<ToolContext, bool> supportsTool) => throw new NotSupportedException();

    public void RegisterUniversalStrategy(IToolExecutionStrategy strategy) => throw new NotSupportedException();

    public void UnregisterStrategy(string strategyKey) => throw new NotSupportedException();

    public IReadOnlyList<IToolExecutionStrategy> GetStrategies(string toolKey) => throw new NotSupportedException();

    public string[] GetStrategyKeys(string toolKey) => throw new NotSupportedException();

    public IToolExecutionStrategy GetStrategy(string toolKey) => throw new NotSupportedException();

    public IToolExecutionStrategy? TryGetStrategy(string toolKey, string strategyKey) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, string> GetStrategyConfiguration(string toolKey, string prefix) => throw new NotSupportedException();

    public void SetStrategyConfigurationValue(string toolKey, string configKey, string value) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, string> GetEffectiveStrategyConfiguration(ToolCommand command) => throw new NotSupportedException();
}
