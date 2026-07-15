// MA0004 (ConfigureAwait) is suppressed file-wide: Avalonia UI code whose awaits must resume on the UI
// thread. MA0006/S108 cover pervasive UI-event-handler style (control reference equality; empty
// best-effort catch blocks).
#pragma warning disable MA0004, MA0006, S108
using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace ContainerExtension.Views;

/// <summary>
/// Partial class containing the Container Engine settings dialog: the form-item/section
/// layout primitives, the dialog assembly, and validation/persistence wiring. Split out of the
/// primary view to keep the largest method isolated.
/// </summary>
public partial class DockerDiagnosticsView
{
    private Panel CreateFormItem(string label, string desc, Control control)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        var labelBlock = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = FontColor };
        panel.Children.Add(labelBlock);
        if (!string.IsNullOrEmpty(desc))
        {
            panel.Children.Add(new TextBlock { Text = desc, FontSize = 10, Foreground = MutedColor, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(control);

        // Tie the field label to its input so assistive technology announces a name and the
        // description as help text, instead of a bare "edit"/"combo box"/"slider" (a11y).
        AutomationProperties.SetLabeledBy(control, labelBlock);
        AutomationProperties.SetName(control, label);
        if (!string.IsNullOrEmpty(desc))
        {
            AutomationProperties.SetHelpText(control, desc);
        }
        return panel;
    }

    private Panel CreateFormSectionHeader(string title)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = AccentColor });
        panel.Children.Add(new Border { Height = 1, Background = MutedColor, Opacity = 0.2 });
        return panel;
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new Window
        {
            Title = "Configure Container Engine Settings",
            MinWidth = 520,
            MinHeight = 500,
            Width = 520,
            Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 16
        };

        var headerPanel = CreateDialogHeader("Container Engine Settings", AccentColor);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        var formPanel = new StackPanel { Spacing = 12 };

        formPanel.Children.Add(CreateFormSectionHeader("IMAGE & EXECUTION"));

        var defaultImage = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.OssCadSuiteImage);
        var defaultImageTextBox = new TextBox { Text = defaultImage, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Default Toolchain Image", "The default container image for all tools. It is build-only (not published to a registry): produce it via Build Local Image rather than pulling.", defaultImageTextBox));

        var pullPolicy = _settingsService.SafeGetSetting(ContainerExtensionModule.PullPolicySetting, "if-not-present");
        var pullPolicyComboBox = new ComboBox
        {
            ItemsSource = new[] { "always", "if-not-present", "never" },
            SelectedItem = pullPolicy,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Image Pull Policy", "Determines when the plugin should pull images from the registry.", pullPolicyComboBox));

        var platform = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto");
        var platformComboBox = new ComboBox
        {
            ItemsSource = new[] { "auto", "linux/amd64", "linux/arm64", "linux/arm/v7" },
            SelectedItem = platform,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Image Platform", "Forces a specific system architecture platform when running containers.", platformComboBox));

        var networkMode = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var networkModeComboBox = new ComboBox
        {
            ItemsSource = new[] { "bridge", "host", "none" },
            SelectedItem = networkMode,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Network Mode", "The Docker network mode used for containerized tool executions.", networkModeComboBox));

        formPanel.Children.Add(CreateFormSectionHeader("RESOURCE LIMITS"));

        var totalRam = ContainerExtensionModule.GetHostMemoryMB();
        var currentMem = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        var memValueText = new TextBlock { Text = currentMem == 0 ? "Unlimited" : $"{currentMem:N0} MB", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        // Step 512 MB so every snap value satisfies the validator's 512 MB floor (a 256 MB step would let the user land on a rejected 256 MB).
        var memSlider = new Slider { Minimum = 0, Maximum = totalRam, Value = currentMem, SmallChange = 512, LargeChange = 1024, TickFrequency = 512, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        memSlider.ValueChanged += (s, e) =>
        {
            var val = Math.Round(memSlider.Value);
            memValueText.Text = val == 0 ? "Unlimited" : $"{val:N0} MB";
        };
        var memGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(memSlider, 0);
        Grid.SetColumn(memValueText, 2);
        memGrid.Children.Add(memSlider);
        memGrid.Children.Add(memValueText);
        formPanel.Children.Add(CreateFormItem($"Memory Limit (0 = unlimited) — Max: {totalRam:N0} MB", "Restricts memory consumption of container tasks.", memGrid));

        var totalCores = (double)Environment.ProcessorCount;
        var currentCpu = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        var cpuValueText = new TextBlock { Text = currentCpu == 0 ? "Unlimited" : $"{currentCpu:F1} Cores", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        var cpuSlider = new Slider { Minimum = 0, Maximum = totalCores, Value = currentCpu, SmallChange = 0.5, LargeChange = 1.0, TickFrequency = 0.5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        cpuSlider.ValueChanged += (s, e) =>
        {
            var val = Math.Round(cpuSlider.Value * 2.0) / 2.0;
            cpuValueText.Text = val == 0 ? "Unlimited" : $"{val:F1} Cores";
        };
        var cpuGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(cpuSlider, 0);
        Grid.SetColumn(cpuValueText, 2);
        cpuGrid.Children.Add(cpuSlider);
        cpuGrid.Children.Add(cpuValueText);
        formPanel.Children.Add(CreateFormItem($"CPU Cores Limit (0 = unlimited) — Max: {totalCores:N0} Cores", "Restricts CPU cores usage for container tasks.", cpuGrid));

        var currentTimeout = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        var timeoutValueText = new TextBlock { Text = currentTimeout == 0 ? "No timeout" : $"{currentTimeout:N0} min", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        var timeoutSlider = new Slider { Minimum = 0, Maximum = 480, Value = currentTimeout, SmallChange = 5, LargeChange = 30, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        timeoutSlider.ValueChanged += (s, e) =>
        {
            var val = Math.Round(timeoutSlider.Value);
            timeoutValueText.Text = val == 0 ? "No timeout" : $"{val:N0} min";
        };
        var timeoutGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(timeoutSlider, 0);
        Grid.SetColumn(timeoutValueText, 2);
        timeoutGrid.Children.Add(timeoutSlider);
        timeoutGrid.Children.Add(timeoutValueText);
        formPanel.Children.Add(CreateFormItem("Execution Timeout (0 = no timeout)", "Maximum execution time for containers before cancellation.", timeoutGrid));

        formPanel.Children.Add(CreateFormSectionHeader("CONTAINER CONFIG"));

        var autoRemoveSetting = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var autoRemoveCheckBox = new CheckBox { Content = "Auto-Remove Containers on Completion", IsChecked = autoRemoveSetting, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Auto-Remove Containers", "Automatically delete containers once the executable process exits.", autoRemoveCheckBox));

        var allowPrivileged = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowPrivilegedSetting, false);
        var allowPrivilegedCheckBox = new CheckBox { Content = "Allow Privileged Containers", IsChecked = allowPrivileged, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Allow Privileged Mode", "Runs containers with privileged capabilities (required in some complex mounting setups).", allowPrivilegedCheckBox));

        var namePrefix = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var prefixTextBox = new TextBox { Text = namePrefix, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Container Name Prefix", "Prefix assigned to all containers spawned by this extension.", prefixTextBox));

        var extraFlags = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");
        var extraFlagsTextBox = new TextBox { Text = extraFlags, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Extra Container Labels", "Additional labels applied to containers (space-separated key=value pairs, e.g. env=prod team=fpga).", extraFlagsTextBox));

        formPanel.Children.Add(CreateFormSectionHeader("LOGGING & DASHBOARD"));

        var logLevel = _settingsService.SafeGetSetting(ContainerExtensionModule.LogLevelSetting, "Errors Only");
        var logLevelComboBox = new ComboBox
        {
            ItemsSource = new[] { "Off", "Errors Only", "Info", "Verbose" },
            SelectedItem = logLevel,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Log Level", "Detail level for container task diagnostics logs.", logLevelComboBox));

        var showTimestamps = _settingsService.SafeGetSetting(ContainerExtensionModule.ShowTimestampsSetting, true);
        var showTimestampsCheckBox = new CheckBox { Content = "Include Timestamps in Logs", IsChecked = showTimestamps, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Timestamps", "Prepend time signatures to stdout/stderr in log windows.", showTimestampsCheckBox));

        var dashboardRefresh = _settingsService.SafeGetSetting(ContainerExtensionModule.DashboardRefreshSetting, "Manual");
        var refreshComboBox = new ComboBox
        {
            ItemsSource = new[] { "Manual", "2s", "5s", "10s", "15s", "30s", "60s", "120s" },
            SelectedItem = dashboardRefresh,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Dashboard Refresh", "Auto-refresh frequency for container list, images, and metrics.", refreshComboBox));

        var telemetryRetention = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "25");
        var retentionComboBox = new ComboBox
        {
            ItemsSource = new[] { "None", "25", "50", "100", "250", "500", "1000", "Unlimited" },
            SelectedItem = telemetryRetention,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Telemetry Retention", "Number of recent executions to retain in history logs.", retentionComboBox));

        formPanel.Children.Add(CreateFormSectionHeader("ADVANCED PATHS"));

        var runtimePath = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "");
        var runtimePathTextBox = new TextBox { Text = runtimePath, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        var browseBtn = new Button { Content = "Browse...", FontSize = 11, Padding = new Thickness(10, 4) };
        AutomationProperties.SetName(browseBtn, "Browse for container runtime executable");
        browseBtn.Command = new AsyncRelayCommand(async () =>
        {
            try
            {
#pragma warning disable CS0618
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Select Container Runtime Executable",
                    AllowMultiple = false
                };
                var result = await openFileDialog.ShowAsync(dialog);
                if (result != null && result.Length > 0)
                {
                    runtimePathTextBox.Text = result[0];
                }
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "BrowseRuntimePath", ex);
            }
        });
        var runtimeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        Grid.SetColumn(runtimePathTextBox, 0);
        Grid.SetColumn(browseBtn, 2);
        runtimeGrid.Children.Add(runtimePathTextBox);
        runtimeGrid.Children.Add(browseBtn);
        formPanel.Children.Add(CreateFormItem("Container Runtime Path", "Explicit path to docker or podman binary (leave empty for system auto-detection).", runtimeGrid));

        var customSocket = _settingsService.SafeGetSetting(ContainerExtensionModule.DaemonSocketSetting, "");
        var socketTextBox = new TextBox { Text = customSocket, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Custom Daemon Socket", "Overrides the standard DOCKER_HOST endpoint (e.g. unix:///var/run/docker.sock).", socketTextBox));

        var scroll = new ScrollViewer
        {
            Content = formPanel
        };
        Grid.SetRow(scroll, 1);
        mainGrid.Children.Add(scroll);

        var errorText = new TextBlock
        {
            Foreground = RedColor,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
            IsVisible = false
        };
        Grid.SetRow(errorText, 2);
        mainGrid.Children.Add(errorText);

        // Register error cleaner to auto-hide error text on any user edit
        RegisterErrorCleaner(formPanel, errorText);

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var resetBtn = new Button
        {
            Content = "Reset to Defaults",
            Padding = new Thickness(14, 8),
            CornerRadius = InnerCornerRadius,
            Background = SubCardBg,
            Foreground = FontColor,
            BorderBrush = BorderColor,
            BorderThickness = HairlineThickness
        };

        var rightButtonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var saveBtn = new Button
        {
            Content = "Save Settings",
            FontWeight = FontWeight.SemiBold,
            Background = AccentColor,
            Foreground = OnAccentColor,
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius,
            IsDefault = true
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius,
            IsCancel = true
        };

        rightButtonPanel.Children.Add(cancelBtn);
        rightButtonPanel.Children.Add(saveBtn);

        Grid.SetColumn(resetBtn, 0);
        Grid.SetColumn(rightButtonPanel, 2);
        footerGrid.Children.Add(resetBtn);
        footerGrid.Children.Add(rightButtonPanel);
        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        resetBtn.Command = new RelayCommand(() =>
        {
            defaultImageTextBox.Text = ContainerExtensionModule.OssCadSuiteImage;
            pullPolicyComboBox.SelectedItem = "if-not-present";
            platformComboBox.SelectedItem = "auto";
            networkModeComboBox.SelectedItem = "bridge";

            memSlider.Value = 0;
            cpuSlider.Value = 0;
            timeoutSlider.Value = 0;

            autoRemoveCheckBox.IsChecked = true;
            allowPrivilegedCheckBox.IsChecked = false;
            prefixTextBox.Text = "containerextension-";
            extraFlagsTextBox.Text = "";

            // Match the registered privacy-by-default defaults (ContainerExtensionModule: "Errors Only" /
            // "25"); resetting to Verbose/100 would silently worsen the telemetry posture this action implies.
            logLevelComboBox.SelectedItem = "Errors Only";
            showTimestampsCheckBox.IsChecked = true;
            refreshComboBox.SelectedItem = "Manual";
            retentionComboBox.SelectedItem = "25";

            runtimePathTextBox.Text = "";
            socketTextBox.Text = "";

            errorText.IsVisible = false;
        });

        // Surface the >75% resource-allocation advisory (ResourceThresholdValidation returns valid-with-
        // warning in that band) before committing, and require an explicit second Save to confirm. The
        // acknowledgement resets whenever the memory or CPU value changes, so a fresh over-allocation is
        // always re-flagged rather than silently carried over.
        bool resourceWarningAcknowledged = false;
        memSlider.ValueChanged += (_, _) => resourceWarningAcknowledged = false;
        cpuSlider.ValueChanged += (_, _) => resourceWarningAcknowledged = false;

        saveBtn.Command = new RelayCommand(() =>
        {
            // Hard-validation failures are red; reset here so a prior amber advisory does not tint them.
            errorText.Foreground = RedColor;
            var imageVal = new ContainerExtension.Validations.DockerImageFormatValidation(allowEmpty: false);
            var prefixVal = new ContainerExtension.Validations.ContainerNameValidation();
            var socketVal = new ContainerExtension.Validations.DaemonSocketValidation();
            var memVal = new ContainerExtension.Validations.ResourceThresholdValidation(totalRam * 0.75, totalRam, "memory");
            var cpuVal = new ContainerExtension.Validations.ResourceThresholdValidation(totalCores * 0.75, totalCores, "CPU");

            string? warn;
            if (!imageVal.Validate(defaultImageTextBox.Text, out warn))
            {
                errorText.Text = $"Image Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!prefixVal.Validate(prefixTextBox.Text, out warn))
            {
                errorText.Text = $"Prefix Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!socketVal.Validate(socketTextBox.Text, out warn))
            {
                errorText.Text = $"Socket Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!memVal.Validate(memSlider.Value, out var memWarn))
            {
                errorText.Text = $"Memory Limit Error: {memWarn}";
                errorText.IsVisible = true;
                return;
            }
            if (!cpuVal.Validate(cpuSlider.Value, out var cpuWarn))
            {
                errorText.Text = $"CPU limit Error: {cpuWarn}";
                errorText.IsVisible = true;
                return;
            }

            // Both values are within host capacity, but the validator may still have returned a valid-with-
            // warning advisory for the >75% band. Surface it (amber) and require a second Save to confirm,
            // rather than silently persisting a host-starving allocation.
            if (!resourceWarningAcknowledged)
            {
                var advisories = new List<string>(2);
                if (!string.IsNullOrEmpty(memWarn)) advisories.Add(memWarn);
                if (!string.IsNullOrEmpty(cpuWarn)) advisories.Add(cpuWarn);
                if (advisories.Count > 0)
                {
                    resourceWarningAcknowledged = true;
                    errorText.Foreground = YellowColor;
                    errorText.Text = string.Join("  ", advisories) + "  Click Save again to confirm.";
                    errorText.IsVisible = true;
                    return;
                }
            }

            // Apply the settings transactionally: snapshot each current value, apply the new ones in
            // order, and on any failure roll the applied writes back, so a mid-save error never leaves
            // the configuration half-applied.
            var updates = new List<(string key, object value)>
            {
                (ContainerExtensionModule.DefaultImageSetting, defaultImageTextBox.Text?.Trim() ?? ""),
                (ContainerExtensionModule.PullPolicySetting, pullPolicyComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.PlatformSetting, platformComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.NetworkModeSetting, networkModeComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.MemoryLimitSetting, Math.Round(memSlider.Value)),
                (ContainerExtensionModule.CpuLimitSetting, Math.Round(cpuSlider.Value * 2.0) / 2.0),
                (ContainerExtensionModule.TimeoutSetting, Math.Round(timeoutSlider.Value)),
                (ContainerExtensionModule.AutoRemoveSetting, autoRemoveCheckBox.IsChecked == true),
                (ContainerExtensionModule.AllowPrivilegedSetting, allowPrivilegedCheckBox.IsChecked == true),
                (ContainerExtensionModule.ContainerNamePrefixSetting, prefixTextBox.Text?.Trim() ?? ""),
                (ContainerExtensionModule.ExtraFlagsSetting, extraFlagsTextBox.Text?.Trim() ?? ""),
                (ContainerExtensionModule.LogLevelSetting, logLevelComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.ShowTimestampsSetting, showTimestampsCheckBox.IsChecked == true),
                (ContainerExtensionModule.DashboardRefreshSetting, refreshComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.TelemetryRetentionSetting, retentionComboBox.SelectedItem as string ?? ""),
                (ContainerExtensionModule.DockerRuntimePathSetting, runtimePathTextBox.Text?.Trim() ?? ""),
                (ContainerExtensionModule.DaemonSocketSetting, socketTextBox.Text?.Trim() ?? ""),
            };
            var applied = new List<(string key, object? old)>(updates.Count);
            try
            {
                foreach (var (key, value) in updates)
                {
                    object? old = null;
                    try { old = _settingsService.GetSettingValue<object>(key); }
                    catch (Exception) { /* no prior value to snapshot; treat as non-restorable */ }
                    _settingsService.SetSettingValue(key, value);
                    applied.Add((key, old));
                }

                _ = RefreshAllSafeAsync();
                dialog.Close();
            }
            catch (Exception ex)
            {
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    if (applied[i].old is null) continue;
                    try { _settingsService.SetSettingValue(applied[i].key, applied[i].old!); }
                    catch (Exception) { /* best-effort restore */ }
                }
                errorText.Text = $"Save Error (changes rolled back): {ex.Message}";
                errorText.IsVisible = true;
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "SaveSettings", ex);
            }
        });

        cancelBtn.Command = new RelayCommand(() => dialog.Close());

        var wrapper = new Border
        {
            Padding = new Thickness(24),
            Child = mainGrid
        };

        dialog.Content = wrapper;

        await ShowDialogWithOwnerAsync(dialog);
    }

    private static void RegisterErrorCleaner(Avalonia.Controls.Control control, Avalonia.Controls.TextBlock errorText)
    {
        if (control is Avalonia.Controls.TextBox tb)
        {
            tb.TextChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.ComboBox cb)
        {
            cb.SelectionChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.Slider sl)
        {
            sl.ValueChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.CheckBox chk)
        {
            chk.IsCheckedChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
            {
                RegisterErrorCleaner(child, errorText);
            }
        }
        else if (control is Avalonia.Controls.ContentControl cc && cc.Content is Avalonia.Controls.Control childControl)
        {
            RegisterErrorCleaner(childControl, errorText);
        }
        else if (control is Avalonia.Controls.ScrollViewer sv && sv.Content is Avalonia.Controls.Control svChild)
        {
            RegisterErrorCleaner(svChild, errorText);
        }
        else if (control is Avalonia.Controls.Border border && border.Child is Avalonia.Controls.Control borderChild)
        {
            RegisterErrorCleaner(borderChild, errorText);
        }
    }
}
