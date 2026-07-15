using System;
using System.Linq;
using ContainerExtension;
using ContainerExtension.Views;
using Xunit;

namespace ContainerExtension.UnitTests;

// Joins the telemetry collection: RendersEverySummaryKey constructs a DockerExecutionStrategy whose
// background initialization touches the process-global telemetry sink, so it must not run in parallel
// with the telemetry tests that reset that static state.
[Collection("TelemetryTests")]
public sealed class ActiveConfigLayoutTests
{
    // Every setting GetActiveSettingsSummary surfaces must be rendered by the Active Configuration panel;
    // a summary key missing from ActiveConfigLayout.Groups is silently hidden from the user (as the
    // privileged-mode toggle was before this became an invariant).
    [Fact]
    public void ActiveConfigPanel_RendersEverySummaryKey()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var summaryKeys = strategy.GetActiveSettingsSummary().Keys;
        var displayedKeys = ActiveConfigLayout.Groups.SelectMany(g => g.Keys).ToHashSet(StringComparer.Ordinal);

        Assert.All(summaryKeys, key =>
            Assert.True(displayedKeys.Contains(key),
                $"Setting '{key}' is emitted by GetActiveSettingsSummary but absent from ActiveConfigLayout.Groups, so it is never shown in the dashboard."));
    }

    [Fact]
    public void ActiveConfigPanel_ShowsPrivilegedModeToggle()
    {
        var displayedKeys = ActiveConfigLayout.Groups.SelectMany(g => g.Keys);

        Assert.Contains(ContainerExtensionModule.SettingsKeyAllowPrivileged, displayedKeys);
    }
}
