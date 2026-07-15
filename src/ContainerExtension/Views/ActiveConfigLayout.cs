namespace ContainerExtension.Views;

/// <summary>
/// Declarative layout for the dashboard's Active Configuration panel: the settings shown, grouped by
/// section. Kept out of <see cref="DockerDiagnosticsView"/> so the display-coverage invariant — that every
/// key <c>DockerExecutionStrategy.GetActiveSettingsSummary</c> emits is rendered in some group — can be
/// asserted without constructing an Avalonia control. A setting present in the summary but absent here is
/// silently hidden from the user (as the privileged-mode toggle was).
/// </summary>
internal static class ActiveConfigLayout
{
    internal static readonly (string Title, string[] Keys)[] Groups =
    [
        ("IMAGE & EXECUTION", [ ContainerExtensionModule.SettingsKeyImage, ContainerExtensionModule.SettingsKeyPullPolicy, ContainerExtensionModule.SettingsKeyPlatform, ContainerExtensionModule.SettingsKeyNetwork ]),
        ("RESOURCE LIMITS",  [ ContainerExtensionModule.SettingsKeyMemory, ContainerExtensionModule.SettingsKeyCpu, ContainerExtensionModule.SettingsKeyTimeout ]),
        ("CONTAINER INFO",   [ ContainerExtensionModule.SettingsKeyAutoRemove, ContainerExtensionModule.SettingsKeyNamePrefix, ContainerExtensionModule.SettingsKeyExtraLabels ]),
        ("LOGGING CONFIG",   [ ContainerExtensionModule.SettingsKeyLogLevel, ContainerExtensionModule.SettingsKeyTimestamps ]),
        ("DASHBOARD DATA",   [ ContainerExtensionModule.SettingsKeyDashboardRefresh, ContainerExtensionModule.SettingsKeyRetention ]),
        ("ADVANCED PATHS",   [ ContainerExtensionModule.SettingsKeyRuntimePath, ContainerExtensionModule.SettingsKeyAllowPrivileged, ContainerExtensionModule.SettingsKeyBypassNamedPipeCheck, ContainerExtensionModule.SettingsKeyAllowNativeFallback ]),
    ];
}
