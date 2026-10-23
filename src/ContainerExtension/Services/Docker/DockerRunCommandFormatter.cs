using System.Buffers;
using System.Globalization;
using System.Text;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;

namespace ContainerExtension.Services.Docker;

/// <summary>The shell a copied <c>docker run</c> command is quoted for.</summary>
internal enum CommandShell
{
    /// <summary>A POSIX shell such as sh, bash or zsh: the shell of a macOS or Linux host.</summary>
    Posix,

    /// <summary>PowerShell 7.3 or later: the shell of a Windows host.</summary>
    PowerShell,
}

/// <summary>
/// Renders <c>docker run</c> command lines for the shell of the host. <see cref="Generate"/> builds a
/// template from the current settings (with <c>&lt;tool&gt;</c>/<c>&lt;args&gt;</c> placeholders) for the
/// dashboard before the first run; <see cref="Reconstruct"/> renders the command for the
/// <see cref="CreateContainerParameters"/> of a real execution, with the hardening flags, the published and
/// exposed ports and the labels the run sets, except the extension's own label, so that the startup sweep
/// does not take a container started from the copy for one of its own. Every word is quoted for the shell,
/// so the shell hands the words to <c>docker</c> unchanged. Environment values are masked unless the caller
/// explicitly opts into the verbatim, clipboard-only form.
/// </summary>
internal static class DockerRunCommandFormatter
{
    private const string ContainerWorkDir = "/workspace";
    private const string MaskedValue = "********";
    private const string StopParsingToken = "--%";

    // A word made of these characters alone means itself to a POSIX shell, unless it starts with '=',
    // which zsh expands to a command's path.
    private static readonly SearchValues<char> PosixBareChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@%+=:,./_-");

    // The same for PowerShell, without '@', '%' and ',' (splatting, the stop-parsing token, arrays).
    private static readonly SearchValues<char> PowerShellBareChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+=:./\\_-");

    private static readonly SearchValues<char> FlagChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");

    // PowerShell reads all four of these as a single quote, so each one is doubled inside a quoted word.
    private static readonly SearchValues<char> PowerShellSingleQuotes = SearchValues.Create("'‘’‚‛");

    /// <summary>The shell of this host: PowerShell on Windows, a POSIX shell elsewhere.</summary>
    internal static CommandShell HostShell => OperatingSystem.IsWindows() ? CommandShell.PowerShell : CommandShell.Posix;

    /// <summary>
    /// Quotes <paramref name="word"/> so that <paramref name="shell"/> passes it on as exactly one argument
    /// with exactly this text. A word that needs no quoting is returned as it is.
    /// </summary>
    internal static string Quote(string word, CommandShell shell)
    {
        ArgumentNullException.ThrowIfNull(word);
        return shell == CommandShell.PowerShell ? QuotePowerShell(word) : QuotePosix(word);
    }

    private static string QuotePosix(string word)
    {
        if (word.Length > 0 && word[0] != '=' && !word.AsSpan().ContainsAnyExcept(PosixBareChars))
        {
            return word;
        }
        // Inside single quotes nothing is special; a single quote ends the quoting, is escaped, and reopens it.
        return "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string QuotePowerShell(string word)
    {
        if (IsPowerShellBare(word))
        {
            return word;
        }
        if (string.Equals(word, StopParsingToken, StringComparison.Ordinal))
        {
            // PowerShell drops an argument whose value is the stop-parsing token, quoted or not (measured with
            // PowerShell 7.6.3); an array holding the string passes it on.
            return "@('--%')";
        }
        var sb = new StringBuilder(word.Length + 2);
        sb.Append('\'');
        foreach (var c in word)
        {
            sb.Append(c);
            if (PowerShellSingleQuotes.Contains(c))
            {
                sb.Append(c);
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private static bool IsPowerShellBare(string word)
    {
        if (word.Length == 0 || word.AsSpan().ContainsAnyExcept(PowerShellBareChars))
        {
            return false;
        }
        if (word[0] == '-')
        {
            // A flag such as -p or --cap-drop; PowerShell may split anything else after a dash, such as -a.b.
            var name = word.StartsWith("--", StringComparison.Ordinal) ? word[2..] : word[1..];
            return name.Length > 0 && char.IsAsciiLetter(name[0]) && !name.AsSpan().ContainsAnyExcept(FlagChars);
        }
        if (char.IsAsciiDigit(word[0]) || word[0] == '.' || word[0] == '+')
        {
            // A word that starts like a number is read as one (1kb is 1024, 0x10 is 16) unless it is all digits.
            return word.All(char.IsAsciiDigit);
        }
        return true;
    }

    /// <summary>
    /// Builds a generic, copy-pasteable <c>docker run</c> template from the active settings, using
    /// <c>&lt;tool&gt;</c>/<c>&lt;args&gt;</c> placeholders in place of a concrete command.
    /// </summary>
    internal static string Generate(ISettingsService settings, string runtimePath, string image, CommandShell? shell = null)
    {
        var sh = shell ?? HostShell;
        var memMb = settings.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0);
        var cpuCores = settings.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0);
        var network = settings.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var autoRemove = settings.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var platform = settings.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto");
        var namePrefix = settings.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var extraFlags = settings.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");

        var sb = new StringBuilder();
        AppendCommandWord(sb, runtimePath, sh);
        sb.Append(" run");
        if (autoRemove)
        {
            sb.Append(" --rm");
        }
        var prefix = DockerCommandBuilder.SanitizeContainerName(namePrefix ?? "").TrimEnd('-');
        if (prefix.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --name {prefix}-<tool>-<hhmmss>");
        }
        sb.Append(sh == CommandShell.PowerShell
            ? $" -v \"${{PWD}}:{ContainerWorkDir}\" -w {ContainerWorkDir}"
            : $" -v \"$(pwd)\":{ContainerWorkDir} -w {ContainerWorkDir}");
        if (sh == CommandShell.Posix)
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
        if (!string.IsNullOrWhiteSpace(network) && !string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            AppendOption(sb, "--network", network, sh);
        }
        AppendHardening(sb, [DockerCommandBuilder.DroppedCapabilities], null, [DockerCommandBuilder.NoNewPrivilegesOption], DockerCommandBuilder.ProcessLimit, sh);
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
        {
            AppendOption(sb, "--platform", platform, sh);
        }
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            // Read the setting the way a run does, so the template publishes and labels what a run would.
            var fromSettings = new CreateContainerParameters();
            DockerCommandBuilder.ApplyExtraContainerLabels(fromSettings, extraFlags, _ => { });
            AppendPortsAndLabels(sb, fromSettings, sh);
        }
        sb.Append(' ').Append(Quote(image, sh)).Append(" <tool> <args>");

        return sb.ToString();
    }

    /// <summary>
    /// Renders the <c>docker run</c> command that starts the container <paramref name="p"/> describes, quoted
    /// for <paramref name="shell"/> (the host's shell by default). When <paramref name="maskEnvValues"/> is
    /// true (the default, used for anything logged or persisted), environment values are replaced with
    /// <c>********</c>; pass false only for the in-session, clipboard-only verbatim command.
    /// </summary>
    internal static string Reconstruct(CreateContainerParameters p, string runtimePath, bool maskEnvValues = true, CommandShell? shell = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        var sh = shell ?? HostShell;
        var sb = new StringBuilder();
        AppendCommandWord(sb, runtimePath, sh);
        sb.Append(" run");

        var host = p.HostConfig;
        if (host?.AutoRemove == true)
        {
            sb.Append(" --rm");
        }
        if (!string.IsNullOrEmpty(p.Name))
        {
            AppendOption(sb, "--name", p.Name, sh);
        }
        if (!string.IsNullOrEmpty(p.User))
        {
            AppendOption(sb, "--user", p.User, sh);
        }
        if (host?.Binds != null)
        {
            foreach (var bind in host.Binds)
            {
                AppendOption(sb, "-v", bind, sh);
            }
        }
        if (!string.IsNullOrEmpty(p.WorkingDir))
        {
            AppendOption(sb, "-w", p.WorkingDir, sh);
        }
        if (host?.Memory > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {host.Memory / (1024 * 1024)}m");
            if (host.MemorySwap == host.Memory)
            {
                sb.Append(CultureInfo.InvariantCulture, $" --memory-swap {host.MemorySwap / (1024 * 1024)}m");
            }
        }
        if (host?.NanoCPUs > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --cpus {host.NanoCPUs / 1_000_000_000.0:N1}");
        }
        if (host?.Init == true)
        {
            sb.Append(" --init");
        }
        if (!string.IsNullOrEmpty(host?.NetworkMode) &&
            !host.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            AppendOption(sb, "--network", host.NetworkMode, sh);
        }
        if (host != null)
        {
            AppendHardening(sb, host.CapDrop, host.CapAdd, host.SecurityOpt, host.PidsLimit, sh);
        }
        AppendPortsAndLabels(sb, p, sh);

        if (p.Env != null)
        {
            foreach (var env in p.Env)
            {
                // Logged and persisted commands carry the variable NAME only: environment values can hold
                // secrets (license keys, tokens) under arbitrary names that no keyword list catches. The
                // in-session exact copy (maskEnvValues: false) renders the real value, for a verbatim,
                // runnable command placed only on the clipboard and never written to the telemetry log.
                var eqIdx = env.IndexOf('=', StringComparison.Ordinal);
                var rendered = maskEnvValues && eqIdx > 0 ? $"{env[..eqIdx]}={MaskedValue}" : env;
                AppendOption(sb, "-e", rendered, sh);
            }
        }

        sb.Append(' ').Append(Quote(p.Image ?? "", sh));
        if (p.Cmd != null)
        {
            foreach (var arg in p.Cmd)
            {
                sb.Append(' ').Append(Quote(arg ?? "", sh));
            }
        }

        return sb.ToString();
    }

    private static void AppendCommandWord(StringBuilder sb, string runtimePath, CommandShell sh)
    {
        var path = string.IsNullOrEmpty(runtimePath) ? "docker" : runtimePath;
        if (sh == CommandShell.PowerShell)
        {
            var quoted = Quote(path, sh);
            // PowerShell reads a quoted first word as a string, not a command, unless the call operator runs it.
            sb.Append(string.Equals(quoted, path, StringComparison.Ordinal) ? path : "& " + quoted);
        }
        else
        {
            // A first word with '=' in it would be a variable assignment.
            sb.Append(path.Contains('=', StringComparison.Ordinal) ? "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'" : Quote(path, sh));
        }
    }

    private static void AppendOption(StringBuilder sb, string flag, string value, CommandShell sh)
        => sb.Append(' ').Append(flag).Append(' ').Append(Quote(value, sh));

    private static void AppendHardening(
        StringBuilder sb, IList<string>? capDrop, IList<string>? capAdd, IList<string>? securityOpt, long? pidsLimit, CommandShell sh)
    {
        foreach (var cap in capDrop ?? [])
        {
            AppendOption(sb, "--cap-drop", cap, sh);
        }
        foreach (var cap in capAdd ?? [])
        {
            AppendOption(sb, "--cap-add", cap, sh);
        }
        foreach (var opt in securityOpt ?? [])
        {
            AppendOption(sb, "--security-opt", opt, sh);
        }
        if (pidsLimit > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --pids-limit {pidsLimit}");
        }
    }

    private static void AppendPortsAndLabels(StringBuilder sb, CreateContainerParameters p, CommandShell sh)
    {
        var bindings = p.HostConfig?.PortBindings;
        if (bindings != null)
        {
            foreach (var key in bindings.Keys.Order(StringComparer.Ordinal))
            {
                foreach (var binding in bindings[key] ?? [])
                {
                    var hostIp = binding.HostIP ?? "";
                    var hostPort = binding.HostPort ?? "";
                    var mapping = hostIp.Length > 0 ? $"{hostIp}:{hostPort}:{key}"
                        : hostPort.Length > 0 ? $"{hostPort}:{key}"
                        : key;
                    AppendOption(sb, "-p", mapping, sh);
                }
            }
        }
        if (p.ExposedPorts != null)
        {
            foreach (var key in p.ExposedPorts.Keys.Order(StringComparer.Ordinal))
            {
                // A published port is exposed by its -p already.
                if (bindings == null || !bindings.TryGetValue(key, out var list) || list == null || list.Count == 0)
                {
                    AppendOption(sb, "--expose", key, sh);
                }
            }
        }
        if (p.Labels != null)
        {
            foreach (var key in p.Labels.Keys.Order(StringComparer.Ordinal))
            {
                // The extension's own label stays off: the startup sweep would take a container started
                // from the copy for a leftover of its own and remove it.
                if (!string.Equals(key, ContainerExtensionModule.ContainerOwnerLabel, StringComparison.Ordinal))
                {
                    AppendOption(sb, "--label", $"{key}={p.Labels[key]}", sh);
                }
            }
        }
    }
}
