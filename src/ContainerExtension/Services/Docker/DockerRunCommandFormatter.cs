using System.Buffers;
using System.Globalization;
using System.Text;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Renders human-readable <c>docker run</c> command lines. <see cref="Generate"/> builds a generic
/// template from the current settings (with <c>&lt;tool&gt;</c>/<c>&lt;args&gt;</c> placeholders) for the
/// dashboard; <see cref="Reconstruct"/> renders the exact command for a specific
/// <see cref="CreateContainerParameters"/> from a real execution. Environment values are masked unless
/// the caller explicitly opts into the verbatim, clipboard-only form.
/// </summary>
internal static class DockerRunCommandFormatter
{
    private const string ContainerWorkDir = "/workspace";
    private static readonly SearchValues<char> ShellSpecialAndWhitespaceChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`! \t\n\r\v\f");

    /// <summary>
    /// Builds a generic, copy-pasteable <c>docker run</c> template from the active settings, using
    /// <c>&lt;tool&gt;</c>/<c>&lt;args&gt;</c> placeholders in place of a concrete command.
    /// </summary>
    internal static string Generate(ISettingsService settings, string runtimePath, string image)
    {
        var memMb = settings.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0);
        var cpuCores = settings.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0);
        var network = settings.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var autoRemove = settings.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var platform = settings.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto");
        var namePrefix = settings.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var extraFlags = settings.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{runtimePath} run");
        if (autoRemove)
        {
            sb.Append(" --rm");
        }
        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --name {namePrefix.TrimEnd('-')}-<tool>-<hhmmss>");
        }
        sb.Append(CultureInfo.InvariantCulture, $" -v \"$(pwd)\":{ContainerWorkDir} -w {ContainerWorkDir}");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            sb.Append(" --user $(id -u):$(id -g)");
        }
        if (memMb > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {memMb:F0}m --memory-swap {memMb:F0}m");
        }
        if (cpuCores > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --cpus {cpuCores:N1}");
        }
        sb.Append(" --init");
        if (!string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --network {network}");
        }
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --platform {platform}");
        }
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            foreach (var flag in extraFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --label {flag}");
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $" {image} <tool> <args>");

        return sb.ToString();
    }

    /// <summary>
    /// Renders the exact <c>docker run</c> command corresponding to <paramref name="p"/>. When
    /// <paramref name="maskEnvValues"/> is true (the default, used for anything logged or persisted),
    /// environment values are replaced with <c>********</c>; pass false only for the in-session,
    /// clipboard-only verbatim command.
    /// </summary>
    internal static string Reconstruct(CreateContainerParameters p, string runtimePath, bool maskEnvValues = true)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{runtimePath} run");

        if (p.HostConfig?.AutoRemove == true)
        {
            sb.Append(" --rm");
        }
        if (!string.IsNullOrEmpty(p.Name))
        {
            var escapedName = p.Name.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --name \"{escapedName}\"");
        }
        if (!string.IsNullOrEmpty(p.User))
        {
            var escapedUser = p.User.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --user \"{escapedUser}\"");
        }

        if (p.HostConfig?.Binds != null)
        {
            foreach (var bind in p.HostConfig.Binds)
            {
                var escapedBind = bind.Replace("\"", "\\\"").Replace('\\', '/');
                sb.Append(CultureInfo.InvariantCulture, $" -v \"{escapedBind}\"");
            }
        }

        if (!string.IsNullOrEmpty(p.WorkingDir))
        {
            var escapedWorkingDir = p.WorkingDir.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" -w \"{escapedWorkingDir}\"");
        }

        if (p.HostConfig?.Memory > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {p.HostConfig.Memory / (1024 * 1024)}m");
            if (p.HostConfig.MemorySwap == p.HostConfig.Memory)
            {
                sb.Append(CultureInfo.InvariantCulture, $" --memory-swap {p.HostConfig.MemorySwap / (1024 * 1024)}m");
            }
        }
        if (p.HostConfig?.NanoCPUs > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --cpus {p.HostConfig.NanoCPUs / 1_000_000_000.0:N1}");
        }
        if (p.HostConfig?.Init == true)
        {
            sb.Append(" --init");
        }

        if (!string.IsNullOrEmpty(p.HostConfig?.NetworkMode) &&
          !p.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            var escapedNetworkMode = p.HostConfig.NetworkMode.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --network \"{escapedNetworkMode}\"");
        }

        if (p.Env != null)
        {
            foreach (var env in p.Env)
            {
                var eqIdx = env.IndexOf('=');
                if (eqIdx > 0)
                {
                    // Record the variable NAME only; the value is always masked. This command is
                    // persisted to the telemetry log, and environment values can carry secrets
                    // (license keys, tokens) under arbitrary, non-obvious names that a keyword
                    // denylist cannot catch reliably — so no value is ever written.
                    var key = env[..eqIdx];
                    // Logged/persisted commands always mask the value (it can carry secrets). The in-session
                    // exact-copy path (maskEnvValues:false) renders the real value for a verbatim, runnable
                    // command placed only on the clipboard — never written to the telemetry log.
                    var rendered = maskEnvValues ? $"{key}=********" : env;
                    var escapedEnv = rendered.Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" -e \"{escapedEnv}\"");
                }
                else
                {
                    var escapedEnv = env.Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" -e \"{escapedEnv}\"");
                }
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $" {p.Image}");
        if (p.Cmd != null)
        {
            foreach (var arg in p.Cmd)
            {
                if (string.IsNullOrEmpty(arg))
                {
                    sb.Append(" \"\"");
                }
                else if (arg.AsSpan().ContainsAny(ShellSpecialAndWhitespaceChars))
                {
                    var escapedArg = arg.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" \"{escapedArg}\"");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $" {arg}");
                }
            }
        }

        return sb.ToString();
    }
}
