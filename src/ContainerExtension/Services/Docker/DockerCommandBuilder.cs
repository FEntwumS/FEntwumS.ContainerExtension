#pragma warning disable MA0051

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Docker.DotNet.Models;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using ContainerExtension.Validations;

namespace ContainerExtension.Services.Docker;

internal static class DockerCommandBuilder
{
    private const string ContainerWorkDir = "/workspace";
    private static readonly char[] ArgSplitChars = ['=', ' '];
    private static readonly System.Text.RegularExpressions.Regex ContainerNameSanitizer = new(
        @"[^a-zA-Z0-9._\-]", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static CreateContainerParameters BuildContainerParameters(
        string image,
        ToolCommand command,
        ISettingsService settingsService,
        string? uid,
        string? gid,
        Action<ToolCommand, string> sdkLog)
    {
        var executable = (command.Executable ?? command.ToolName).Replace("\r", "").Replace('\\', '/');
        var workingDirFull = Path.GetFullPath(command.WorkingDirectory);
        var rawPrefix = SafeGetSetting(settingsService, ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");

        // Compute strict bounds suffix to prevent prefix bleed (e.g. matching /project2 against /project)
        var workingDirBound = workingDirFull;
        if (!workingDirBound.EndsWith(Path.DirectorySeparatorChar) && !workingDirBound.EndsWith(Path.AltDirectorySeparatorChar))
            workingDirBound += Path.DirectorySeparatorChar;

        if (command.Arguments != null)
        {
            foreach (var arg in command.Arguments)
            {
                try
                {
                    var parts = arg.Split(ArgSplitChars, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    var potentialPath = parts[parts.Length - 1].Trim('"', '\'', '\r', '\n', ' ');

                    // Fast reject: must contain a path struct separator
                    if (potentialPath.Contains('/') || potentialPath.Contains('\\'))
                    {
                        var dir = (potentialPath.EndsWith('/') || potentialPath.EndsWith('\\'))
                            ? potentialPath
                            : Path.GetDirectoryName(potentialPath);

                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            var absoluteDir = Path.GetFullPath(Path.Combine(workingDirFull, dir));

                            // Bounds string normalization for check
                            var absBound = absoluteDir;
                            if (!absBound.EndsWith(Path.DirectorySeparatorChar) && !absBound.EndsWith(Path.AltDirectorySeparatorChar))
                                absBound += Path.DirectorySeparatorChar;

                            // Security check: rigorously verify that the determined path physically lives within the workspace
                            var osAwareComparison = OperatingSystem.IsLinux() 
                                ? StringComparison.Ordinal 
                                : StringComparison.OrdinalIgnoreCase;
                            if (absBound.StartsWith(workingDirBound, osAwareComparison) &&
                                !Directory.Exists(absoluteDir))
                            {
                                Directory.CreateDirectory(absoluteDir);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerCommandBuilder", "Path validation failed", ex);
                }
            }
        }

        var fullCmdString = executable;
        if (command.Arguments != null && command.Arguments.Count > 0)
        {
            var argsStr = string.Join(" ", command.Arguments.Select(a =>
            {
                var processed = a.Replace("\r", "").Replace('\\', '/');
                if (processed.Any(c => char.IsWhiteSpace(c) || ";|&<>()[`#".Contains(c)))
                {
                    return $"\"{processed.Replace("\"", "\\\"")}\"";
                }
                return processed;
            }));
            fullCmdString += " " + argsStr;
        }
        var fullCmd = new List<string> { "sh", "-c", fullCmdString };

        var autoRemove = SafeGetSetting(settingsService, ContainerExtensionModule.AutoRemoveSetting, true);

        string? containerName = null;
        if (!string.IsNullOrWhiteSpace(rawPrefix))
        {
            var sanitized = ContainerNameSanitizer.Replace(rawPrefix, "");
            if (sanitized.Length > 0)
            {
                var safeToolName = ContainerNameSanitizer.Replace(command.ToolName ?? "tool", "");
                containerName = $"{sanitized.TrimEnd('-')}-{safeToolName}-{DateTime.Now:HHmmssfff}-{Guid.NewGuid().ToString("N")[..4]}";
            }
        }

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = containerName,
            Cmd = fullCmd,
            WorkingDir = ContainerWorkDir,
            HostConfig = new HostConfig
            {
                Binds = new List<string> { $"{workingDirFull}:{ContainerWorkDir}" },
                AutoRemove = autoRemove,
                NetworkMode = SafeGetSetting(settingsService, ContainerExtensionModule.NetworkModeSetting, "bridge"),
                Init = true
            }
        };

        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(gid))
        {
            createParams.User = $"{uid}:{gid}";
        }

        var envVars = ParseEnvFile(workingDirFull);
        if (envVars != null)
        {
            createParams.Env = envVars;
            sdkLog(command, $"[Docker SDK] Injecting {envVars.Count} environment variable(s) from .env file.");
        }

        var memMb = SafeGetSetting<double>(settingsService, ContainerExtensionModule.MemoryLimitSetting, 0);
        var hostMemMb = ContainerExtensionModule.GetHostMemoryMB();
        if (memMb > 0)
        {
            memMb = Math.Min(memMb, hostMemMb);
            var memBytes = (long)(memMb * 1024 * 1024);
            createParams.HostConfig.Memory = memBytes;
            createParams.HostConfig.MemorySwap = memBytes;
        }

        var cpuCores = SafeGetSetting<double>(settingsService, ContainerExtensionModule.CpuLimitSetting, 0);
        var hostCores = (double)Environment.ProcessorCount;
        if (cpuCores > 0)
        {
            cpuCores = Math.Min(cpuCores, hostCores);
            createParams.HostConfig.NanoCPUs = (long)(cpuCores * 1_000_000_000);
        }

        var extraFlags = SafeGetSetting(settingsService, ContainerExtensionModule.ExtraFlagsSetting, "");
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            createParams.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var flag in extraFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIdx = flag.IndexOf('=');
                if (eqIdx > 0)
                    createParams.Labels[flag.Substring(0, eqIdx)] = flag.Substring(eqIdx + 1);
                else
                    createParams.Labels[flag] = "true";
            }
            sdkLog(command, $"[Docker SDK] Injecting {createParams.Labels.Count} extra label(s) from Extra Container Labels.");
        }

        return createParams;
    }

    internal static List<string>? ParseEnvFile(string workingDir)
    {
        var envPath = Path.Combine(workingDir, ".env");
        if (!File.Exists(envPath)) return null;

        var envVars = new List<string>();
        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#') continue;

            if (trimmed.StartsWith("export ".AsSpan(), StringComparison.Ordinal))
                trimmed = trimmed[7..].TrimStart();

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = trimmed[..eqIdx];
            var valueSpan = trimmed[(eqIdx + 1)..];

            if (valueSpan.Length == 0 || (valueSpan[0] != '"' && valueSpan[0] != '\''))
            {
                var commentIdx = valueSpan.IndexOf(" #".AsSpan(), StringComparison.Ordinal);
                if (commentIdx >= 0)
                    valueSpan = valueSpan[..commentIdx];
            }
            valueSpan = valueSpan.Trim();

            if (valueSpan.Length >= 2 &&
                ((valueSpan[0] == '"' && valueSpan[^1] == '"') ||
                 (valueSpan[0] == '\'' && valueSpan[^1] == '\'')))
            {
                valueSpan = valueSpan[1..^1];
            }

            envVars.Add($"{key.ToString()}={valueSpan.ToString()}");
        }
        return envVars.Count > 0 ? envVars : null;
    }

    private static T SafeGetSetting<T>(ISettingsService settingsService, string key, T defaultValue)
    {
        try
        {
            if (settingsService.HasSetting(key))
                return settingsService.GetSettingValue<T>(key);
        }
        catch { /* Fallback to default */ }
        return defaultValue;
    }
}
