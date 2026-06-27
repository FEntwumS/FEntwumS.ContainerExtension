#pragma warning disable MA0051

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Constructs Docker 'run' argument lists, escapes shell parameters, and parses <c>.env</c> files
/// for container execution. Environment keys and values are validated against shell-injection
/// vectors before inclusion.
/// </summary>
internal static class DockerCommandBuilder
{
    private static long _containerCounter = 0;
    private const string ContainerWorkDir = "/workspace";
    internal static readonly SearchValues<char> ShellSpecialChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`! \t\n\r");
    private static readonly SearchValues<char> DangerousEnvKeyChars = SearchValues.Create("&|;`$()<>\n\r\\ \t");
    private static readonly Dictionary<string, (List<string>? vars, DateTime lastWrite, DateTime lastAccess)> EnvCache = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock EnvCacheLock = new();

    private static bool ToolRequiresWriteAccess(ToolCommand command)
    {
        var exe = command.Executable ?? command.ToolName ?? "";
        if (exe.Contains("openfpgaloader", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("iceprog", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("icesprog", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("openocd", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("dfu-util", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("ujprog", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("gtkwave", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (exe.Contains("yosys", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("ghdl", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("nvc", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("iverilog", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("verilator", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("apicula", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("nextpnr", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("pack", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (command.Arguments != null)
        {
            foreach (var arg in command.Arguments)
            {
                if (arg == null) continue;

                if (arg.Contains('>') || arg.Contains(">>"))
                {
                    return true;
                }

                if (arg.Equals("-o", StringComparison.Ordinal) || arg.StartsWith("-o=", StringComparison.Ordinal) || arg.StartsWith("-o ", StringComparison.Ordinal) ||
                    arg.Equals("-w", StringComparison.Ordinal) || arg.StartsWith("-w=", StringComparison.Ordinal) || arg.StartsWith("-w ", StringComparison.Ordinal) ||
                    arg.Equals("-a", StringComparison.Ordinal) || arg.StartsWith("-a=", StringComparison.Ordinal) || arg.StartsWith("-a ", StringComparison.Ordinal) ||
                    arg.Equals("-e", StringComparison.Ordinal) || arg.StartsWith("-e=", StringComparison.Ordinal) || arg.StartsWith("-e ", StringComparison.Ordinal) ||
                    arg.Equals("-r", StringComparison.Ordinal) || arg.StartsWith("-r=", StringComparison.Ordinal) || arg.StartsWith("-r ", StringComparison.Ordinal) ||
                    arg.Equals("--output", StringComparison.Ordinal) || arg.StartsWith("--output=", StringComparison.Ordinal) ||
                    arg.Equals("--write", StringComparison.Ordinal) || arg.StartsWith("--write=", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string SanitizeLabel(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        bool isClean = true;
        foreach (var c in input)
        {
            if (char.IsControl(c) || c == ';' || c == '&' || c == '|' || c == '`' || c == '$' || c == '<' || c == '>' || c == '\\' || c == '"' || c == '\'')
            {
                isClean = false;
                break;
            }
        }
        if (isClean) return input;

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (!(char.IsControl(c) || c == ';' || c == '&' || c == '|' || c == '`' || c == '$' || c == '<' || c == '>' || c == '\\' || c == '"' || c == '\''))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string SanitizeContainerName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }
        bool isClean = true;
        foreach (char c in input)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
            {
                isClean = false;
                break;
            }
        }
        if (isClean) return input;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static CreateContainerParameters BuildContainerParameters(
      string image,
      ToolCommand command,
      ISettingsService? settingsService,
      string? uid,
      string? gid,
      Action<ToolCommand, string> sdkLog,
      double? remoteCpuCores = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(command);
        sdkLog ??= (_, _) => { };

        var rawExePath = command.Executable?.Trim('\r', '\n', ' ', '\t');
        if (string.IsNullOrWhiteSpace(rawExePath))
        {
            rawExePath = command.ToolName?.Trim('\r', '\n', ' ', '\t');
        }
        if (string.IsNullOrWhiteSpace(rawExePath))
        {
            rawExePath = "container-run";
        }
        rawExePath = HealEscapedPaths(rawExePath);
        var executablePath = NormalizeSeparators(rawExePath);
        var executable = Path.GetFileName(executablePath);

        // The caller must supply an absolute working directory (the project root). Resolving a relative
        // or empty value against the process directory would silently mount the plugin's own bin folder
        // (e.g. "<bin>/Debug/net10.0/C:/Users/.../Project:/workspace"), producing a broken bind. Reject it
        // with an actionable message instead.
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            throw new InvalidOperationException(
                "No working directory was supplied for the tool command; the container workspace cannot be mounted. " +
                "The caller must provide the absolute project path.");
        }
        if (!Path.IsPathRooted(command.WorkingDirectory))
        {
            throw new InvalidOperationException(
                $"The tool working directory '{command.WorkingDirectory}' is not an absolute path. It would be resolved " +
                "against the plugin's process directory and produce an incorrect bind mount. The caller must provide an " +
                "absolute project path.");
        }
        var workingDirFull = Path.GetFullPath(command.WorkingDirectory);
        workingDirFull = ResolvePhysicalPath(workingDirFull);
        if (!Path.IsPathRooted(workingDirFull))
        {
            throw new InvalidOperationException("Resolved mount path is not absolute.");
        }
        var workingDirCanonical = GetCanonicalPath(workingDirFull);
        var rawPrefix = settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");

        // Compute strict bounds suffix to prevent prefix bleed (e.g. matching /project2 against /project)
        var workingDirBound = workingDirFull;
        if (!workingDirBound.EndsWith(Path.DirectorySeparatorChar) && !workingDirBound.EndsWith(Path.AltDirectorySeparatorChar))
        {
            workingDirBound += Path.DirectorySeparatorChar;
        }

        if (command.Arguments != null && command.Arguments.Count > 0)
        {
            foreach (var arg in command.Arguments)
            {
                if (arg == null)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(arg))
                {
                    continue;
                }
                if (arg.Length > 2048)
                {
                    throw new ArgumentException("Command argument exceeds maximum allowed length of 2048 characters.", nameof(command));
                }
                try
                {
                    ReadOnlySpan<char> argSpan = arg.AsSpan();
                    var segments = new List<string>(arg.Length / 10 + 2);
                    int start = 0;
                    bool inDoubleQuotes = false;
                    bool inSingleQuotes = false;
                    bool escaped = false;
                    for (int idx = 0; idx < argSpan.Length; idx++)
                    {
                        char c = argSpan[idx];
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"' && !inSingleQuotes)
                        {
                            inDoubleQuotes = !inDoubleQuotes;
                        }
                        else if (c == '\'' && !inDoubleQuotes)
                        {
                            inSingleQuotes = !inSingleQuotes;
                        }
                        else if ((c == '=' || c == ' ') && !inDoubleQuotes && !inSingleQuotes)
                        {
                            if (idx > start)
                            {
                                segments.Add(argSpan[start..idx].ToString());
                            }
                            start = idx + 1;
                        }
                    }
                    if (argSpan.Length > start)
                    {
                        segments.Add(argSpan[start..].ToString());
                    }

                    foreach (var rawSeg in segments)
                    {
                        var segment = rawSeg.Trim('"', '\'', '\r', '\n', ' ');
                        if (string.IsNullOrEmpty(segment) || segment.StartsWith('-'))
                        {
                            continue;
                        }

                        if (segment.Contains('/') || segment.Contains('\\'))
                        {
                            var potentialPath = segment;
                            var dir = (potentialPath.EndsWith('/') || potentialPath.EndsWith('\\'))
                              ? potentialPath
                              : Path.GetDirectoryName(potentialPath);

                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                var absoluteDir = Path.GetFullPath(Path.Combine(workingDirFull, dir));
                                var osAwareComparison = OperatingSystem.IsLinux()
                                    ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase;

                                if (absoluteDir.StartsWith(workingDirFull, osAwareComparison))
                                {
                                    var physicalDir = ResolvePhysicalPath(absoluteDir);
                                    var absBound = physicalDir;
                                    if (!absBound.EndsWith(Path.DirectorySeparatorChar) && !absBound.EndsWith(Path.AltDirectorySeparatorChar))
                                        absBound += Path.DirectorySeparatorChar;

                                    if (absBound.StartsWith(workingDirBound, osAwareComparison) && !Directory.Exists(absoluteDir))
                                    {
                                        try
                                        {
                                            Directory.CreateDirectory(absoluteDir);
                                        }
                                        catch (Exception ex) when (ex is not OutOfMemoryException)
                                        {
                                            ContainerTelemetry.TrackError("DockerCommandBuilder", $"Workspace dir creation failed for '{absoluteDir}'", ex);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerCommandBuilder", $"Workspace path scanner failed for '{arg}'", ex);
                }
            }
        }

        // Argv/exec command model: emit the command as a token vector [executable, ...mapped args]
        // rather than reconstructing a single `sh -c <string>` line. OneWare hands the strategy an
        // already-tokenized, path-mapped argument vector (ToolCommand.Arguments) and its NativeStrategy
        // runs it shell-lessly; the container entrypoint (tini) together with HostConfig.Init execs argv
        // directly. Passing argv removes the host-to-container shell entirely, so no shell-injection
        // surface exists by construction, and keeps the containerized invocation faithful to the native
        // one. Per-token path mapping and the GHDL library remapping below are preserved; only the shell
        // reconstruction and its denylist quoting are removed.
        var cmdTokens = new List<string>(1 + (command.Arguments?.Count ?? 0)) { executable };
        if (command.Arguments != null && command.Arguments.Count > 0)
        {
            var argsList = command.Arguments.ToList();
            bool isGhdlMakeOrElabOrRun = false;
            var rawExe = command.Executable ?? command.ToolName ?? "";
            if (rawExe.Contains("ghdl", StringComparison.OrdinalIgnoreCase) &&
                argsList.Any(arg => arg != null && (
                    arg.Equals("-m", StringComparison.Ordinal) ||
                    arg.Equals("-e", StringComparison.Ordinal) ||
                    arg.Equals("-r", StringComparison.Ordinal) ||
                    arg.Equals("--synth", StringComparison.Ordinal) ||
                    arg.Equals("--elab", StringComparison.Ordinal) ||
                    arg.Equals("--run", StringComparison.Ordinal)
                )))
            {
                isGhdlMakeOrElabOrRun = true;
            }

            if (isGhdlMakeOrElabOrRun)
            {
                string? libraryName = null;
                for (int idx = argsList.Count - 1; idx >= 0; idx--)
                {
                    var candidate = argsList[idx];
                    if (candidate == null || candidate.StartsWith('-')) continue;

                    if (idx > 0)
                    {
                        var prev = argsList[idx - 1];
                        if (prev != null && (
                            prev.Equals("-o", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("--out", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("--workdir", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("-P", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("--std", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("--work", StringComparison.OrdinalIgnoreCase) ||
                            prev.Equals("-work", StringComparison.OrdinalIgnoreCase)
                        ))
                        {
                            continue;
                        }
                    }

                    var unitName = Path.GetFileNameWithoutExtension(candidate);
                    var lib = FindLibraryForUnit(workingDirFull, unitName);
                    if (!string.IsNullOrWhiteSpace(lib))
                    {
                        libraryName = lib;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(libraryName))
                {
                    bool replaced = false;
                    for (int j = 0; j < argsList.Count; j++)
                    {
                        var arg = argsList[j];
                        if (arg == null) continue;

                        if (arg.StartsWith("--work=", StringComparison.OrdinalIgnoreCase))
                        {
                            argsList[j] = $"--work={libraryName}";
                            replaced = true;
                            break;
                        }
                        else if (arg.StartsWith("-work=", StringComparison.OrdinalIgnoreCase))
                        {
                            argsList[j] = $"-work={libraryName}";
                            replaced = true;
                            break;
                        }
                        else if ((arg.Equals("--work", StringComparison.OrdinalIgnoreCase) ||
                                  arg.Equals("-work", StringComparison.OrdinalIgnoreCase)) &&
                                 j + 1 < argsList.Count)
                        {
                            argsList[j + 1] = libraryName;
                            replaced = true;
                            break;
                        }
                    }

                    if (!replaced)
                    {
                        argsList.Insert(1, $"--work={libraryName}");
                    }
                }
            }

            for (int i = 0; i < argsList.Count; i++)
            {
                var a = argsList[i];
                if (a == null) continue;

                // A library name following --work/-work is not a path and must not be rewritten.
                bool isWorkValue = false;
                if (i > 0)
                {
                    var prev = argsList[i - 1];
                    if (prev != null && (prev.Equals("--work", StringComparison.OrdinalIgnoreCase) || prev.Equals("-work", StringComparison.OrdinalIgnoreCase)))
                    {
                        isWorkValue = true;
                    }
                }

                string mapped;
                if (isWorkValue)
                {
                    var normalized = a.Replace('\\', '/');
                    var libName = Path.GetFileName(normalized.TrimEnd('/'));
                    var mappedLib = FindLibraryForDirectory(workingDirFull, normalized);
                    mapped = !string.IsNullOrWhiteSpace(mappedLib) ? mappedLib : (string.IsNullOrWhiteSpace(libName) ? "work" : libName);
                }
                else if (isGhdlMakeOrElabOrRun && (a.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".vhdl", StringComparison.OrdinalIgnoreCase)))
                {
                    var normalized = a.Replace('\\', '/');
                    var baseName = Path.GetFileNameWithoutExtension(normalized);
                    mapped = string.IsNullOrWhiteSpace(baseName) ? a : baseName;
                }
                else if (IsSinglePathArgument(a, out var prefix, out var pathPart))
                {
                    mapped = prefix + MapPathToContainer(pathPart, workingDirFull, workingDirCanonical);
                }
                else if (a.Contains(' ') || a.Contains(';') || a.Contains(','))
                {
                    mapped = MapCommandScriptPaths(a, workingDirFull, workingDirCanonical);
                }
                else
                {
                    mapped = ShouldMapArgument(a) ? MapPathToContainer(a, workingDirFull, workingDirCanonical) : a;
                }

                // Each mapped token becomes one argv element. No shell escaping or quoting is applied:
                // the token is delivered verbatim to the program through execve, exactly as OneWare's
                // native execution would, so shell metacharacters are inert data rather than syntax.
                cmdTokens.Add(NormalizeSeparators(mapped));
            }
        }
        var fullCmd = cmdTokens;

        var counter = Interlocked.Increment(ref _containerCounter);
        var containerName = $"{SanitizeContainerName(rawPrefix)}{SanitizeContainerName(executable)}-{DateTime.Now.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)}-{counter}-{Guid.NewGuid().ToString("N")[..8]}";

        var user = string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(gid) || OperatingSystem.IsWindows()
          ? null
          : $"{uid}:{gid}";

        var autoRemove = settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var networkMode = settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");

        var bindSuffix = ToolRequiresWriteAccess(command) ? "" : ":ro";

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = containerName,
            Cmd = fullCmd,
            User = user,
            WorkingDir = ContainerWorkDir,
            HostConfig = new HostConfig
            {
                AutoRemove = autoRemove,
                NetworkMode = networkMode,
                Init = true,
                CapDrop = new List<string> { "ALL" },
                Binds = new List<string>(4) { $"{workingDirFull}:{ContainerWorkDir}{bindSuffix}" }
            }
        };

        var envVars = ParseEnvFile(workingDirFull);
        var hostVars = command.EnvironmentVariables;
        var hostCount = hostVars?.Count ?? 0;
        var envCount = (envVars?.Count ?? 0) + hostCount + 1;
        var envList = new List<string>(envCount);

        // The variable-count cap must only ever drop bulk .env entries, never the host-provided
        // variables. Those also carry higher precedence, so they are appended LAST (Docker keeps
        // the last occurrence of a duplicate key). Reserve room for the host vars plus a possible
        // HOME fallback and truncate only the .env contribution to fit within the limit.
        const int maxEnv = 500;
        var envBudget = Math.Max(0, maxEnv - hostCount - 1);
        if (envVars != null && envVars.Count > 0)
        {
            if (envVars.Count > envBudget)
            {
                envList.AddRange(envVars.Take(envBudget));
                var dropped = envVars.Count - envBudget;
                sdkLog(command, $"[Docker SDK] Warning: .env had {envVars.Count} entries; dropped {dropped} to stay within the {maxEnv}-variable limit (host variables are always kept).");
                ContainerTelemetry.TrackError("DockerCommandBuilder", "Environment variable truncation", null, $"Dropped {dropped} of {envVars.Count} .env entries (limit {maxEnv})");
            }
            else
            {
                envList.AddRange(envVars);
            }
        }
        if (hostVars != null && hostCount > 0)
        {
            foreach (var kvp in hostVars)
            {
                envList.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        if (!envList.Any(e => e.StartsWith("HOME=", StringComparison.Ordinal)))
        {
            envList.Add("HOME=/tmp");
        }

        if (envList.Count > 0)
        {
            createParams.Env = envList;
            sdkLog(command, $"[Docker SDK] Injecting {envList.Count} environment variable(s).");
        }

        var memMb = settingsService.SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        if (memMb > 0 && !double.IsNaN(memMb) && !double.IsInfinity(memMb))
        {
            const double maxMb = 8 * 1024 * 1024.0; // 8 TB max
            var boundedMb = Math.Clamp(memMb, 6.0, maxMb);
            createParams.HostConfig.Memory = (long)(boundedMb * 1024 * 1024);
            createParams.HostConfig.MemorySwap = createParams.HostConfig.Memory;
        }

        var cpuCores = settingsService.SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        if (cpuCores > 0 && !double.IsNaN(cpuCores) && !double.IsInfinity(cpuCores))
        {
            var boundedCpus = remoteCpuCores.HasValue
                ? Math.Clamp(cpuCores, 0, remoteCpuCores.Value)
                : cpuCores;
            createParams.HostConfig.NanoCPUs = (long)Math.Round(boundedCpus * 1_000_000_000);
        }

        var extraFlags = settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            createParams.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
            createParams.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>(StringComparer.OrdinalIgnoreCase);
            createParams.ExposedPorts ??= new Dictionary<string, EmptyStruct>(StringComparer.OrdinalIgnoreCase);

            var flags = TokenizeExtraFlags(extraFlags);
            int i = 0;
            while (i < flags.Count)
            {
                var flag = flags[i];
                if ((string.Equals(flag, "-p", StringComparison.Ordinal) || string.Equals(flag, "--publish", StringComparison.Ordinal)) && i + 1 < flags.Count)
                {
                    var portMappingStr = flags[i + 1];
                    i += 2;

                    var parts = portMappingStr.Split(':');
                    string hostIp = "";
                    string hostPart = "";
                    string guestPart = "";

                    if (parts.Length == 1)
                    {
                        guestPart = parts[0];
                    }
                    else if (parts.Length == 2)
                    {
                        if (parts[0].Contains('.', StringComparison.Ordinal))
                        {
                            hostIp = parts[0];
                            guestPart = parts[1];
                        }
                        else
                        {
                            hostPart = parts[0];
                            guestPart = parts[1];
                        }
                    }
                    else if (parts.Length == 3)
                    {
                        hostIp = parts[0];
                        hostPart = parts[1];
                        guestPart = parts[2];
                    }

                    if (!string.IsNullOrEmpty(guestPart))
                    {
                        var proto = "tcp";
                        var protoIdx = guestPart.IndexOf('/');
                        if (protoIdx >= 0)
                        {
                            if (protoIdx + 1 < guestPart.Length)
                            {
                                var parsedProto = guestPart[(protoIdx + 1)..].ToLowerInvariant();
                                if (!string.IsNullOrEmpty(parsedProto))
                                {
                                    proto = parsedProto;
                                }
                            }
                            guestPart = guestPart[..protoIdx];
                        }

                        var hostDash = hostPart.IndexOf('-');
                        var guestDash = guestPart.IndexOf('-');

                        if (hostDash >= 0 && guestDash >= 0)
                        {
                            if (int.TryParse(hostPart[..hostDash], out var hostStart) &&
                                int.TryParse(hostPart[(hostDash + 1)..], out var hostEnd) &&
                                int.TryParse(guestPart[..guestDash], out var guestStart) &&
                                int.TryParse(guestPart[(guestDash + 1)..], out var guestEnd))
                            {
                                // Reject out-of-range or inverted endpoints and clamp the span so a
                                // malformed mapping such as "0-2000000000" cannot spin a near-unbounded
                                // loop and exhaust memory during container creation.
                                const int maxPort = 65535;
                                const int maxRange = 1024;
                                if (hostStart < 0 || hostStart > maxPort || hostEnd < hostStart || hostEnd > maxPort ||
                                    guestStart < 0 || guestStart > maxPort || guestEnd < guestStart || guestEnd > maxPort)
                                {
                                    sdkLog(command, $"[Docker SDK] Warning: ignoring invalid port-range mapping '{portMappingStr}'.");
                                    continue;
                                }
                                var rangeCount = Math.Min(hostEnd - hostStart, guestEnd - guestStart);
                                if (rangeCount > maxRange)
                                {
                                    sdkLog(command, $"[Docker SDK] Warning: port-range mapping '{portMappingStr}' exceeds {maxRange} ports; truncating.");
                                    rangeCount = maxRange;
                                }
                                for (int r = 0; r <= rangeCount; r++)
                                {
                                    var hp = (hostStart + r).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    var gp = (guestStart + r).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    var key = $"{gp}/{proto}";
                                    createParams.ExposedPorts[key] = default;
                                    var binding = new PortBinding { HostPort = hp };
                                    if (!string.IsNullOrEmpty(hostIp))
                                    {
                                        binding.HostIP = hostIp;
                                    }
                                    else
                                    {
                                        binding.HostIP = "127.0.0.1";
                                    }
                                    if (createParams.HostConfig.PortBindings.TryGetValue(key, out var list))
                                    {
                                        list.Add(binding);
                                    }
                                    else
                                    {
                                        createParams.HostConfig.PortBindings[key] = new List<PortBinding> { binding };
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (!int.TryParse(guestPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var guestPort) ||
                                guestPort < 1 || guestPort > 65535)
                            {
                                sdkLog(command, $"[Docker SDK] Warning: ignoring invalid port mapping '{portMappingStr}'.");
                                continue;
                            }
                            if (!string.IsNullOrEmpty(hostPart) &&
                                (!int.TryParse(hostPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hostPort) ||
                                 hostPort < 0 || hostPort > 65535))
                            {
                                sdkLog(command, $"[Docker SDK] Warning: ignoring invalid port mapping '{portMappingStr}'.");
                                continue;
                            }

                            var key = $"{guestPart}/{proto}";
                            createParams.ExposedPorts[key] = default;
                            var binding = new PortBinding { HostPort = hostPart };
                            if (!string.IsNullOrEmpty(hostIp))
                            {
                                binding.HostIP = hostIp;
                            }
                            else
                            {
                                binding.HostIP = "127.0.0.1";
                            }
                            if (createParams.HostConfig.PortBindings.TryGetValue(key, out var list))
                            {
                                list.Add(binding);
                            }
                            else
                            {
                                createParams.HostConfig.PortBindings[key] = new List<PortBinding> { binding };
                            }
                        }
                    }
                }
                else
                {
                    i++;
                    var eqIdx = flag.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = SanitizeLabel(flag[..eqIdx].Trim('"', '\''));
                        var val = SanitizeLabel(flag[(eqIdx + 1)..].Trim('"', '\''));
                        if (!string.IsNullOrEmpty(key))
                        {
                            createParams.Labels[key] = val;
                        }
                    }
                    else
                    {
                        var key = SanitizeLabel(flag.Trim('"', '\''));
                        if (!string.IsNullOrEmpty(key))
                        {
                            createParams.Labels[key] = "true";
                        }
                    }
                }
            }
            sdkLog(command, $"[Docker SDK] Injecting extra label(s) and port mapping(s) from Extra Container Labels.");
        }

        if (command.ExposedPorts != null && command.ExposedPorts.Count > 0)
        {
            createParams.ExposedPorts ??= new Dictionary<string, EmptyStruct>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in command.ExposedPorts)
            {
                var proto = string.IsNullOrEmpty(port.Protocol) ? "tcp" : port.Protocol.ToLowerInvariant();
                var key = $"{port.Number}/{proto}";
                createParams.ExposedPorts[key] = default;
            }
            sdkLog(command, $"[Docker SDK] Exposing {command.ExposedPorts.Count} port(s) inside container.");
        }

        if (command.PortMappings != null && command.PortMappings.Count > 0)
        {
            createParams.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in command.PortMappings)
            {
                if (mapping.Guest == null || mapping.Host == null)
                {
                    continue;
                }
                var proto = string.IsNullOrEmpty(mapping.Guest.Protocol) ? "tcp" : mapping.Guest.Protocol.ToLowerInvariant();
                var key = $"{mapping.Guest.Number}/{proto}";
                var hostPortStr = mapping.Host.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (createParams.HostConfig.PortBindings.TryGetValue(key, out var list))
                {
                    if (!list.Any(b => { return string.Equals(b.HostPort, hostPortStr, StringComparison.Ordinal); }))
                    {
                        list.Add(new PortBinding { HostPort = hostPortStr, HostIP = "127.0.0.1" });
                    }
                }
                else
                {
                    createParams.HostConfig.PortBindings[key] = new List<PortBinding>
                    {
                        new PortBinding { HostPort = hostPortStr, HostIP = "127.0.0.1" }
                    };
                }
            }
            sdkLog(command, $"[Docker SDK] Configured {command.PortMappings.Count} port mapping(s).");
        }

        return createParams;
    }

    internal static List<string>? ParseEnvFile(string workingDir)
    {
        var envPath = Path.Combine(workingDir, ".env");
        try
        {
            DateTime currentWriteTime;
            try
            {
                currentWriteTime = File.GetLastWriteTimeUtc(envPath);
            }
            catch (Exception)
            {
                currentWriteTime = DateTime.MinValue;
            }

            lock (EnvCacheLock)
            {
                if (EnvCache.TryGetValue(envPath, out var cached) && cached.lastWrite == currentWriteTime)
                {
                    EnvCache[envPath] = (cached.vars, cached.lastWrite, DateTime.UtcNow);
                    return cached.vars;
                }
            }

            if (currentWriteTime == DateTime.MinValue || !File.Exists(envPath))
            {
                lock (EnvCacheLock)
                {
                    EnvCache[envPath] = (null, currentWriteTime, DateTime.UtcNow);
                    if (EnvCache.Count > 100)
                    {
                        var keysToRemove = EnvCache.OrderBy(kvp => { return kvp.Value.lastAccess; }).Take(10).Select(kvp => { return kvp.Key; }).ToList();
                        foreach (var k in keysToRemove)
                        {
                            EnvCache.Remove(k);
                        }
                    }
                }
                return null;
            }

            var envVars = new List<string>();
            var keyIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var fs = new FileStream(envPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                while (reader.ReadLine() is string line)
                {
                    var lineSpan = line.AsSpan().Trim();
                    if (lineSpan.IsEmpty || lineSpan[0] == '#')
                    {
                        continue;
                    }

                    if (lineSpan.StartsWith("export ", StringComparison.Ordinal))
                    {
                        lineSpan = lineSpan[7..].TrimStart();
                    }

                    var eqIdx = lineSpan.IndexOf('=');
                    if (eqIdx <= 0)
                    {
                        continue;
                    }

                    var key = lineSpan[..eqIdx].Trim().ToString();
                    if (key.AsSpan().ContainsAny(DangerousEnvKeyChars))
                    {
                        continue;
                    }
                    var value = lineSpan[(eqIdx + 1)..].Trim().ToString();

                    // Multi-line quoted values are unsupported; reject unbalanced quotes within the line.
                    bool inDoubleQuotes = false;
                    bool inSingleQuotes = false;
                    for (int charIdx = 0; charIdx < value.Length; charIdx++)
                    {
                        char c = value[charIdx];
                        if (c == '"' && !inSingleQuotes)
                        {
                            int backslashCount = 0;
                            int prevIdx = charIdx - 1;
                            while (prevIdx >= 0 && value[prevIdx] == '\\')
                            {
                                backslashCount++;
                                prevIdx--;
                            }
                            if (backslashCount % 2 == 0)
                            {
                                inDoubleQuotes = !inDoubleQuotes;
                            }
                        }
                        else if (c == '\'' && !inDoubleQuotes)
                        {
                            int backslashCount = 0;
                            int prevIdx = charIdx - 1;
                            while (prevIdx >= 0 && value[prevIdx] == '\\')
                            {
                                backslashCount++;
                                prevIdx--;
                            }
                            if (backslashCount % 2 == 0)
                            {
                                inSingleQuotes = !inSingleQuotes;
                            }
                        }
                    }

                    // If quotes are not balanced, we have a multi-line value
                    if ((inDoubleQuotes || inSingleQuotes) && value.Length > 0)
                    {
                        char quoteChar = inDoubleQuotes ? '"' : '\'';
                        var sbVal = new StringBuilder(value);
                        while (reader.ReadLine() is string nextLine)
                        {
                            sbVal.Append('\n').Append(nextLine);

                            var nextLineSpan = nextLine.AsSpan();
                            bool quoteClosed = false;
                            for (int k = 0; k < nextLineSpan.Length; k++)
                            {
                                if (nextLineSpan[k] == quoteChar)
                                {
                                    int backslashCount = 0;
                                    int prevIdx = k - 1;
                                    while (prevIdx >= 0 && nextLineSpan[prevIdx] == '\\')
                                    {
                                        backslashCount++;
                                        prevIdx--;
                                    }
                                    if (backslashCount % 2 == 0)
                                    {
                                        quoteClosed = true;
                                        break;
                                    }
                                }
                            }
                            if (quoteClosed)
                            {
                                break;
                            }
                        }
                        value = sbVal.ToString();
                    }

                    // Strip inline comments
                    int commentIdx = -1;
                    inDoubleQuotes = false;
                    inSingleQuotes = false;
                    for (int charIdx = 0; charIdx < value.Length; charIdx++)
                    {
                        char c = value[charIdx];
                        if (c == '"' && !inSingleQuotes)
                        {
                            int backslashCount = 0;
                            int prevIdx = charIdx - 1;
                            while (prevIdx >= 0 && value[prevIdx] == '\\')
                            {
                                backslashCount++;
                                prevIdx--;
                            }
                            if (backslashCount % 2 == 0)
                            {
                                inDoubleQuotes = !inDoubleQuotes;
                            }
                        }
                        else if (c == '\'' && !inDoubleQuotes)
                        {
                            int backslashCount = 0;
                            int prevIdx = charIdx - 1;
                            while (prevIdx >= 0 && value[prevIdx] == '\\')
                            {
                                backslashCount++;
                                prevIdx--;
                            }
                            if (backslashCount % 2 == 0)
                            {
                                inSingleQuotes = !inSingleQuotes;
                            }
                        }
                        else if (c == '#' && !inDoubleQuotes && !inSingleQuotes)
                        {
                            bool isHexColor = false;
                            if (charIdx + 6 < value.Length)
                            {
                                bool isHex6 = true;
                                for (int k = 1; k <= 6; k++)
                                {
                                    if (!char.IsAsciiHexDigit(value[charIdx + k]))
                                    {
                                        isHex6 = false;
                                        break;
                                    }
                                }
                                if (isHex6 && (charIdx + 7 == value.Length || !char.IsLetterOrDigit(value[charIdx + 7])))
                                {
                                    isHexColor = true;
                                }
                            }
                            if (!isHexColor && charIdx + 3 < value.Length)
                            {
                                bool isHex3 = true;
                                for (int k = 1; k <= 3; k++)
                                {
                                    if (!char.IsAsciiHexDigit(value[charIdx + k]))
                                    {
                                        isHex3 = false;
                                        break;
                                    }
                                }
                                if (isHex3 && (charIdx + 4 == value.Length || !char.IsLetterOrDigit(value[charIdx + 4])))
                                {
                                    isHexColor = true;
                                }
                            }

                            bool isInsideUrl = false;
                            int colonSlashIdx = value.AsSpan(0, charIdx).LastIndexOf("://", StringComparison.Ordinal);
                            if (colonSlashIdx >= 0)
                            {
                                isInsideUrl = true;
                                for (int k = colonSlashIdx + 3; k < charIdx; k++)
                                {
                                    if (char.IsWhiteSpace(value[k]))
                                    {
                                        isInsideUrl = false;
                                        break;
                                    }
                                }
                            }

                            if (!isHexColor && !isInsideUrl)
                            {
                                commentIdx = charIdx;
                                break;
                            }
                        }
                    }

                    if (commentIdx >= 0)
                    {
                        value = value[..commentIdx].Trim();
                    }

                    // Strip outer quotes
                    if (value.Length >= 2 &&
                        ((value[0] == '"' && value[^1] == '"') ||
                         (value[0] == '\'' && value[^1] == '\'')))
                    {
                        value = value[1..^1];
                    }

                    value = SanitizeEnvValue(value);

                    var envVarEntry = $"{key}={value}";
                    if (keyIndices.TryGetValue(key, out var existingIndex))
                    {
                        envVars[existingIndex] = envVarEntry;
                    }
                    else
                    {
                        keyIndices.Add(key, envVars.Count);
                        envVars.Add(envVarEntry);
                    }
                }
            }

            lock (EnvCacheLock)
            {
                EnvCache[envPath] = (envVars, currentWriteTime, DateTime.UtcNow);
                if (EnvCache.Count > 100)
                {
                    var keysToRemove = EnvCache.OrderBy(kvp => { return kvp.Value.lastAccess; }).Take(10).Select(kvp => { return kvp.Key; }).ToList();
                    foreach (var k in keysToRemove)
                    {
                        EnvCache.Remove(k);
                    }
                }
            }
            return envVars.Count > 0 ? envVars : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerCommandBuilder", "ParseEnvFile failed", ex);
        }

        return null;
    }

    private static string ResolvePhysicalPath(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                var target = di.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    return target.FullName;
                }
            }
            else if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    return target.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fallback to original path if resolving target fails
        }
        return path;
    }

    public static bool ShouldMapArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }
        var span = arg.AsSpan();
        if (span.Contains("://".AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }
        if (span.StartsWith("--workdir=".AsSpan(), StringComparison.Ordinal) || span.StartsWith("-P".AsSpan(), StringComparison.Ordinal))
        {
            return true;
        }
        if (Path.IsPathRooted(span) || span.ContainsAny('/', '\\'))
        {
            return true;
        }
        return false;
    }

    public static string MapPathToContainer(string path, string workingDirFull)
    {
        return MapPathToContainer(path, workingDirFull, GetCanonicalPath(workingDirFull));
    }

    public static string MapPathToContainer(string path, string workingDirFull, string workingDirCanonical)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var eqIdx = path.IndexOf('=');
            if (eqIdx > 0)
            {
                var prefix = path[..(eqIdx + 1)];
                var suffix = path[(eqIdx + 1)..];
                if (!string.IsNullOrWhiteSpace(suffix) && (suffix.Contains('/') || suffix.Contains('\\') || Path.IsPathRooted(suffix) || prefix.Equals("--workdir=", StringComparison.Ordinal)))
                {
                    // Special case for GHDL work library path passed to --work=
                    if (prefix.Equals("--work=", StringComparison.OrdinalIgnoreCase) || prefix.Equals("-work=", StringComparison.OrdinalIgnoreCase))
                    {
                        var normalized = suffix.Replace('\\', '/');
                        var libName = Path.GetFileName(normalized.TrimEnd('/'));
                        var mappedLib = FindLibraryForDirectory(workingDirFull, normalized);
                        var finalLibName = !string.IsNullOrWhiteSpace(mappedLib) ? mappedLib : (string.IsNullOrWhiteSpace(libName) ? "work" : libName);
                        return prefix + finalLibName;
                    }

                    var mappedSuffix = MapPathToContainerInternal(suffix, workingDirCanonical);
                    return prefix + mappedSuffix;
                }
            }

            if (path.StartsWith("-P", StringComparison.Ordinal) && path.Length > 2)
            {
                var suffix = path[2..];
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    var mappedSuffix = MapPathToContainerInternal(suffix, workingDirCanonical);
                    return "-P" + mappedSuffix;
                }
            }

            return MapPathToContainerInternal(path, workingDirCanonical);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{ContainerWorkDir}/invalid_escaped_path";
        }
    }

    private static string GetCanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var seenSymlinks = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string ResolveCanonicalInternal(string currentPath, int depth)
        {
            if (depth > 40)
            {
                throw new ContainerExtension.DockerExecutionException("Too many levels of symbolic links.");
            }

            string absolutePath = currentPath;
            if (!Path.IsPathRooted(absolutePath))
            {
                absolutePath = Path.Combine(Directory.GetCurrentDirectory(), absolutePath);
            }

            string root = Path.GetPathRoot(absolutePath) ?? (OperatingSystem.IsWindows() ? @"C:\" : "/");
            if (string.IsNullOrEmpty(root))
            {
                root = OperatingSystem.IsWindows() ? @"C:\" : "/";
            }

            string remainder = absolutePath.Substring(root.Length);
            var separatorChars = new char[] { '/', '\\' };
            var components = remainder.Split(separatorChars, StringSplitOptions.RemoveEmptyEntries);

            string current = root;

            foreach (var component in components)
            {
                if (string.Equals(component, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(component, "..", StringComparison.Ordinal))
                {
                    var parent = Path.GetDirectoryName(current);
                    current = parent ?? root;
                    continue;
                }

                string next = Path.Combine(current, component);

                bool isSymlink = false;
                string? target = null;

                try
                {
                    if (Directory.Exists(next))
                    {
                        var info = new DirectoryInfo(next);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                    }
                    else if (File.Exists(next))
                    {
                        var info = new FileInfo(next);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                    }
                    else
                    {
                        var info = new DirectoryInfo(next);
                        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                        else
                        {
                            var fInfo = new FileInfo(next);
                            if (fInfo.Exists && fInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                target = fInfo.LinkTarget;
                                isSymlink = !string.IsNullOrEmpty(target);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Ignore and treat as non-symlink
                }

                if (isSymlink && target != null)
                {
                    string canonicalSymlink = Path.GetFullPath(next);
                    if (!seenSymlinks.Add(canonicalSymlink))
                    {
                        throw new ContainerExtension.DockerExecutionException($"Circular symbolic link detected: '{next}'");
                    }

                    try
                    {
                        string resolvedTarget;
                        if (Path.IsPathRooted(target))
                        {
                            resolvedTarget = ResolveCanonicalInternal(target, depth + 1);
                        }
                        else
                        {
                            resolvedTarget = ResolveCanonicalInternal(Path.Combine(current, target), depth + 1);
                        }
                        current = resolvedTarget;
                    }
                    finally
                    {
                        seenSymlinks.Remove(canonicalSymlink);
                    }
                }
                else
                {
                    current = next;
                }
            }

            return Path.GetFullPath(current);
        }

        return ResolveCanonicalInternal(path, 0);
    }

    private static string MapPathToContainerInternal(string path, string workingDirCanonical)
    {
        var resolvedWorkingDir = workingDirCanonical;
        var workingDirNormalized = resolvedWorkingDir;
        if (!workingDirNormalized.EndsWith(Path.DirectorySeparatorChar) && !workingDirNormalized.EndsWith(Path.AltDirectorySeparatorChar))
        {
            workingDirNormalized += Path.DirectorySeparatorChar;
        }

        var osComparison = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var normalizedPath = path.Replace('\\', '/');
        var workingDirNormalizedUnix = workingDirNormalized.Replace('\\', '/');
        var resolvedWorkingDirUnix = resolvedWorkingDir.Replace('\\', '/');

        if ((normalizedPath.StartsWith(ContainerWorkDir + "/", StringComparison.Ordinal) || normalizedPath.Equals(ContainerWorkDir, StringComparison.Ordinal))
            && !normalizedPath.StartsWith(workingDirNormalizedUnix, osComparison)
            && !normalizedPath.Equals(resolvedWorkingDirUnix, osComparison))
        {
            return path;
        }

        var fullPath = GetCanonicalPath(Path.IsPathRooted(path) ? path : Path.Combine(resolvedWorkingDir, path));

        if (fullPath.StartsWith(workingDirNormalized, osComparison))
        {
            var relativePath = fullPath[workingDirNormalized.Length..].Replace('\\', '/');
            return $"{ContainerWorkDir}/{relativePath}";
        }
        if (fullPath.Equals(resolvedWorkingDir, osComparison))
        {
            return ContainerWorkDir;
        }

        // Prevent directory traversal outside workspace
        if (Path.IsPathRooted(path) && !path.Contains("..") &&
            (string.Equals(path, "/dev/null", StringComparison.Ordinal) ||
             string.Equals(path, "/dev/zero", StringComparison.Ordinal) ||
             string.Equals(path, "/dev/urandom", StringComparison.Ordinal) ||
             path.StartsWith("/bin/", StringComparison.Ordinal) ||
             path.StartsWith("/usr/bin/", StringComparison.Ordinal) ||
             path.StartsWith("/lib/", StringComparison.Ordinal) ||
             path.StartsWith("/lib64/", StringComparison.Ordinal)))
        {
            return path.Replace('\\', '/');
        }
        return $"{ContainerWorkDir}/invalid_escaped_path";
    }

    private static List<string> TokenizeExtraFlags(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>(0);
        var result = new List<string>(input.Length / 10 + 1);

        var sb = new StringBuilder(input.Length);
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        bool escaped = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
            }
            else if (c == '\\')
            {
                if (i + 1 < input.Length)
                {
                    char next = input[i + 1];
                    if (char.IsWhiteSpace(next) || next == '"' || next == '\'' || next == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                }
                sb.Append(c);
            }
            else if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
            }
            else if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inDoubleQuotes && !inSingleQuotes)
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }
        if (inDoubleQuotes || inSingleQuotes)
        {
            ContainerTelemetry.TrackError("DockerCommandBuilder", "Mismatched quotes detected in extra flags configuration.", null);
        }
        return result;
    }

    private static string NormalizeSeparators(string val)
    {
        if (string.IsNullOrEmpty(val)) return val;
        var hasCr = val.Contains('\r');
        var hasBackslash = val.Contains('\\');
        if (!hasCr && !hasBackslash) return val;
        var result = val;
        if (hasCr) result = result.Replace("\r", "", StringComparison.Ordinal);
        if (hasBackslash) result = result.Replace('\\', '/');
        return result;
    }

    internal static string HealEscapedPaths(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path
            .Replace("\u0008", "/b", StringComparison.Ordinal)
            .Replace("\t", "/t", StringComparison.Ordinal)
            .Replace("\n", "/n", StringComparison.Ordinal)
            .Replace("\r", "/r", StringComparison.Ordinal)
            .Replace("\v", "/v", StringComparison.Ordinal)
            .Replace("\f", "/f", StringComparison.Ordinal)
            .Replace("\a", "/a", StringComparison.Ordinal);
    }

    internal static string MapCommandScriptPaths(string script, string workingDirFull, string workingDirCanonical)
    {
        if (string.IsNullOrWhiteSpace(script)) return script;

        var trimmedScript = script;
        var leadingQuotes = new List<char>();
        var trailingQuotes = new List<char>();

        while (trimmedScript.Length >= 2)
        {
            if (trimmedScript.StartsWith('"') && trimmedScript.EndsWith('"'))
            {
                leadingQuotes.Add('"');
                trailingQuotes.Add('"');
                trimmedScript = trimmedScript[1..^1];
            }
            else if (trimmedScript.StartsWith('\'') && trimmedScript.EndsWith('\''))
            {
                leadingQuotes.Add('\'');
                trailingQuotes.Add('\'');
                trimmedScript = trimmedScript[1..^1];
            }
            else
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(trimmedScript)) return script;

        var sb = new StringBuilder();
        var span = trimmedScript.AsSpan();
        int start = 0;
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;
        bool escaped = false;

        for (int idx = 0; idx < span.Length; idx++)
        {
            char c = span[idx];
            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
            }
            else if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
            }
            else if ((c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '=' || c == ';' || c == ',') && !inDoubleQuotes && !inSingleQuotes)
            {
                if (idx > start)
                {
                    var part = span[start..idx].ToString();
                    sb.Append(MapSingleScriptSegment(part, workingDirFull, workingDirCanonical));
                }
                sb.Append(c);
                start = idx + 1;
            }
        }
        if (span.Length > start)
        {
            var part = span[start..].ToString();
            sb.Append(MapSingleScriptSegment(part, workingDirFull, workingDirCanonical));
        }

        var result = sb.ToString();
        if (leadingQuotes.Count > 0)
        {
            var finalSb = new StringBuilder(result.Length + leadingQuotes.Count * 2);
            for (int i = 0; i < leadingQuotes.Count; i++)
            {
                finalSb.Append(leadingQuotes[i]);
            }
            finalSb.Append(result);
            for (int i = trailingQuotes.Count - 1; i >= 0; i--)
            {
                finalSb.Append(trailingQuotes[i]);
            }
            return finalSb.ToString();
        }
        return result;
    }

    private static string MapSingleScriptSegment(string segment, string workingDirFull, string workingDirCanonical)
    {
        if (string.IsNullOrWhiteSpace(segment)) return segment;

        var trimmed = segment.Trim('"', '\'', ';', ',');
        if (ShouldMapArgument(trimmed))
        {
            var mapped = MapPathToContainer(trimmed, workingDirFull, workingDirCanonical);
            var idx = segment.IndexOf(trimmed, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var prefix = segment[..idx];
                var suffix = segment[(prefix.Length + trimmed.Length)..];
                return prefix + mapped + suffix;
            }
        }
        return segment;
    }

    private static bool IsSinglePathArgument(string arg, out string prefix, out string pathPart)
    {
        prefix = "";
        pathPart = arg;

        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }

        var eqIdx = arg.IndexOf('=');
        if (eqIdx > 0)
        {
            var p = arg[..(eqIdx + 1)];
            var s = arg[(eqIdx + 1)..];
            if (p.Equals("--work=", StringComparison.OrdinalIgnoreCase) || p.Equals("-work=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (Path.IsPathRooted(s) || s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            {
                prefix = p;
                pathPart = s;
                return true;
            }
        }

        if (arg.StartsWith("-P", StringComparison.Ordinal) && arg.Length > 2)
        {
            var s = arg[2..];
            if (Path.IsPathRooted(s) || s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            {
                prefix = "-P";
                pathPart = s;
                return true;
            }
        }
        if (arg.StartsWith("-o", StringComparison.Ordinal) && arg.Length > 2)
        {
            var s = arg[2..];
            if (Path.IsPathRooted(s) || s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            {
                prefix = "-o";
                pathPart = s;
                return true;
            }
        }

        if (Path.IsPathRooted(arg) || arg.StartsWith("~/", StringComparison.Ordinal) || arg.StartsWith("~\\", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string? FindProjectFile(string startDir)
    {
        try
        {
            var current = new DirectoryInfo(startDir);
            while (current != null && current.Exists)
            {
                var files = current.GetFiles("*.fpgaproj");
                if (files.Length > 0)
                {
                    return files[0].FullName;
                }
                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ignore
        }
        return null;
    }

    private static string? FindLibraryForUnit(string workingDir, string unitName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(unitName))
            {
                return null;
            }

            var projFile = FindProjectFile(workingDir);
            if (projFile == null || !File.Exists(projFile))
            {
                return null;
            }

            using (var fs = new FileStream(projFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var document = JsonDocument.Parse(fs))
            {
                var root = document.RootElement;
                foreach (var property in root.EnumerateObject())
                {
                    var propName = property.Name;
                    if (propName.StartsWith("GHDL-LIB_", StringComparison.OrdinalIgnoreCase))
                    {
                        var libName = propName["GHDL-LIB_".Length..];
                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var fileEl in property.Value.EnumerateArray())
                            {
                                var filePath = fileEl.GetString();
                                if (filePath != null)
                                {
                                    var normalizedPath = filePath.Replace('\\', '/');
                                    var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
                                    if (string.Equals(fileName, unitName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return libName;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort library lookup; any I/O or parse failure falls through to no mapping.
        }
        return null;
    }

    private static string? FindLibraryForDirectory(string workingDir, string dirPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(dirPath))
            {
                return null;
            }

            var projFile = FindProjectFile(workingDir);
            if (projFile == null || !File.Exists(projFile))
            {
                return null;
            }

            var targetDir = dirPath.Replace('\\', '/').Trim('/');
            var targetDirName = Path.GetFileName(targetDir);

            using (var fs = new FileStream(projFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var document = JsonDocument.Parse(fs))
            {
                var root = document.RootElement;
                foreach (var property in root.EnumerateObject())
                {
                    var propName = property.Name;
                    if (propName.StartsWith("GHDL-LIB_", StringComparison.OrdinalIgnoreCase))
                    {
                        var libName = propName["GHDL-LIB_".Length..];
                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var fileEl in property.Value.EnumerateArray())
                            {
                                var filePath = fileEl.GetString();
                                if (filePath != null)
                                {
                                    var normalizedPath = filePath.Replace('\\', '/');
                                    if (normalizedPath.Contains("/" + targetDirName + "/", StringComparison.OrdinalIgnoreCase) ||
                                        normalizedPath.StartsWith(targetDirName + "/", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return libName;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort library lookup; any I/O or parse failure falls through to no mapping.
        }
        return null;
    }

    private static string SanitizeEnvValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = value.Replace("`", "", StringComparison.Ordinal);
        while (true)
        {
            int start = result.IndexOf("$(", StringComparison.Ordinal);
            if (start < 0) break;
            int end = result.IndexOf(')', start);
            if (end < 0)
            {
                result = result.Remove(start, 2);
            }
            else
            {
                result = result.Remove(start, end - start + 1);
            }
        }
        return result;
    }
}
