// MA0051 (method length) is suppressed file-wide: the argv/mount construction methods are long but
// cohesive single-responsibility builders; splitting them would scatter tightly-coupled mapping logic.
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
    // The hardening every container gets; the copied docker run command renders the same values.
    internal const string DroppedCapabilities = "ALL";
    internal const string NoNewPrivilegesOption = "no-new-privileges:true";
    internal const long ProcessLimit = 4096;
    internal static readonly SearchValues<char> ShellSpecialChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`! \t\n\r");
    private static readonly SearchValues<char> DangerousEnvKeyChars = SearchValues.Create("&|;`$()<>\n\r\\ \t");
    private static readonly Dictionary<string, (List<string>? vars, DateTime lastWrite, DateTime lastAccess)> EnvCache = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock EnvCacheLock = new();

    // Whether a tool gets the project writable is decided by its file name, never by the folders it lies in: every
    // tool of a toolchain that OneWare installed lies under a folder named Packages.

    // Programmers and the waveform viewer only read the project, whatever their flags mean.
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "openfpgaloader", "iceprog", "black-iceprog", "iceprogduino", "icesprog", "openocd", "dfu-util", "ujprog", "fujprog", "gtkwave",
    };

    // Tools that write into the project without an output flag the argument scan recognizes: through a file argument
    // (prjoxide pack, vcd2fst, hex2bin), a flag of their own (icepll -f, icetime -j, ecpbram -g) or a work directory.
    private static readonly HashSet<string> WritingTools = new(StringComparer.Ordinal)
    {
        "eqy", "scy", "nvc", "vvp", "prjoxide", "icepll", "ecppll", "gowin_pll", "ecpbram", "icetime",
        "vcd2fst", "vcd2lxt", "vcd2lxt2", "vcd2vzt", "bin2hex", "hex2bin",
    };

    // Families of writing tools: yosys-smtbmc, nextpnr-ice40, sby-gui, mcy-dash, ghdl1-llvm, iverilog-vpi, verilator_bin.
    private static readonly string[] WritingToolPrefixes = ["yosys", "nextpnr-", "sby", "mcy", "ghdl", "iverilog", "verilator"];

    // The packers and unpackers of every family (icepack, ecpunpack, gowin_pack, gmpack), since "unpack" ends in it too.
    private const string WritingToolSuffix = "pack";

    /// <summary>
    /// The file name of a tool's executable, lowercased and without <c>.exe</c>. Splits at both separators, since a
    /// Windows path keeps its backslashes on every system.
    /// </summary>
    internal static string ToolFileName(string executable)
    {
        var name = executable[(executable.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static bool IsWritingTool(string name) =>
        WritingTools.Contains(name) ||
        name.EndsWith(WritingToolSuffix, StringComparison.Ordinal) ||
        Array.Exists(WritingToolPrefixes, prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool ToolRequiresWriteAccess(ToolCommand command)
    {
        // The tool's name in OneWare counts like the file name: OneWare runs the model that Verilator built, under the
        // file name simulation, as the tool verilator, and the model writes its waveform into the project.
        string[] names = [ToolFileName(command.Executable ?? ""), ToolFileName(command.ToolName ?? "")];
        if (Array.Exists(names, ReadOnlyTools.Contains))
        {
            return false;
        }
        if (Array.Exists(names, IsWritingTool))
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

    internal static string SanitizeContainerName(string input)
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
      double? remoteCpuCores = null,
      bool isRootless = false,
      bool? workspaceWritable = null,
      ContainerMount? mount = null)
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

        // OneWare hands us the host tool path, which on Windows carries sometimes contains ".exe" suffix
        // (e.g. ".../ghdl/bin/ghdl.exe"). The container is always Linux, where the binary is
        // named without that suffix, so execve("ghdl.exe", ...) fails with ENOENT (exit 127).
        // Strip the Windows executable extension so the container command matches the Linux binary.
        if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            executable = executable[..^4];
        }

        // The caller passes the mount it resolved for preparing the arguments, so that both use the same folders and
        // the file system is searched for the project file once per run.
        mount ??= ResolveMount(command.WorkingDirectory);
        var workingDirFull = mount.WorkingDirFull;
        var rawPrefix = settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");

        // The output folders the arguments name are collected here and created only once every check has passed, so
        // a run that is rejected leaves nothing behind.
        var outputFolders = new List<string>();
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
                                // A path OneWare already mapped while it prepared the command is in container form;
                                // its folder lies below the mounted folder on the host.
                                var absoluteDir = HostFolderOf(dir, mount);
                                if (absoluteDir == null)
                                {
                                    continue;
                                }
                                // Only a folder at or below the working directory is created, the one folder a writing
                                // tool may write to; elsewhere in the project the tool could not write anyway.
                                // Containment is checked on the fully canonical candidate alone, not the lexical path:
                                // a path OneWare mapped to /workspace comes back below the project root and may be
                                // spelled differently from the working directory, and GetCanonicalPath resolves
                                // intermediate symlinks realpath-style (appending any not-yet-created leaf verbatim),
                                // so a checked-in symlinked ancestor (e.g. out -> /home/victim/.ssh) is exposed as an
                                // out-of-bound target here; ResolvePhysicalPath would leave it unresolved and let
                                // Directory.CreateDirectory follow it at the OS level.
                                var canonicalDir = GetCanonicalPath(absoluteDir);
                                if (IsAtOrBelow(canonicalDir, mount.WorkingDirCanonical) && !Directory.Exists(absoluteDir))
                                {
                                    outputFolders.Add(absoluteDir);
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

            // Every path the container cannot reach is collected, so the run fails once and names them all.
            var outsideMount = new List<string>();
            string CollectOutside(string path)
            {
                outsideMount.Add(path);
                return path;
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
                    mapped = prefix + MapPathToContainer(pathPart, mount, CollectOutside);
                }
                else if (a.Contains(' ') || a.Contains(';') || a.Contains(','))
                {
                    mapped = MapCommandScriptPaths(a, mount, CollectOutside);
                }
                else
                {
                    mapped = ShouldMapArgument(a) ? MapPathToContainer(a, mount, CollectOutside) : a;
                }

                // Each mapped token becomes one argv element. No shell escaping or quoting is applied:
                // the token is delivered verbatim to the program through execve, exactly as OneWare's
                // native execution would, so shell metacharacters are inert data rather than syntax.
                cmdTokens.Add(NormalizeSeparators(mapped));
            }

            if (outsideMount.Count > 0)
            {
                throw OutsideMountError(outsideMount, mount);
            }
        }
        var fullCmd = cmdTokens;

        var counter = Interlocked.Increment(ref _containerCounter);
        var containerName = $"{SanitizeContainerName(rawPrefix)}{SanitizeContainerName(executable)}-{DateTime.Now.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)}-{counter}-{Guid.NewGuid().ToString("N")[..8]}";

        // On a rootless runtime (rootless Podman or rootless Docker), pinning --user to the host euid:egid
        // breaks every workspace write: the host uid is not the in-namespace owner of the bind mount, so the
        // tool's output hits EACCES. Omitting --user runs as the image default (root), which the rootless
        // user namespace maps back to the invoking host user that owns the workspace. Every other runtime —
        // including ROOTFUL Podman and Docker — still needs the explicit uid:gid for correct file ownership.
        var user = isRootless || string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(gid) || OperatingSystem.IsWindows()
          ? null
          : $"{uid}:{gid}";

        var autoRemove = settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var networkMode = settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");

        // A docker.workspace of the call or of the tool decides first; without one, the tool's name and flags do.
        var writable = IsWorkspaceWritable(command, workspaceWritable);

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = containerName,
            Cmd = fullCmd,
            User = user,
            WorkingDir = mount.ContainerWorkingDir,
            HostConfig = new HostConfig
            {
                AutoRemove = autoRemove,
                NetworkMode = networkMode,
                Init = true,
                CapDrop = new List<string> { DroppedCapabilities },
                // Defence-in-depth for every container. no-new-privileges blocks
                // setuid/setgid privilege escalation inside the container — a primary escape primitive —
                // and PidsLimit caps the task count as a fork-bomb backstop (generous so multi-threaded
                // synthesis and place-and-route are unaffected). No container starts privileged, so no
                // setting opts out of this baseline.
                SecurityOpt = new List<string> { NoNewPrivilegesOption },
                PidsLimit = ProcessLimit,
                Binds = BuildBinds(mount, writable)
            }
        };

        // The .env file comes from the tool's working directory, as the configuration guide says, also when the
        // project root above it is the mounted folder.
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
            ApplyExtraContainerLabels(createParams, extraFlags, message => sdkLog(command, message));
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

        // Set last, so that no label from Extra Container Labels can drop or change it.
        createParams.Labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        createParams.Labels[ContainerExtensionModule.ContainerOwnerLabel] = "true";

        // The bind check is the last check of the run; it rewrites each host path to its canonical form and rejects a
        // bind of a blocked host folder, also one reached through a symbolic link such as a working directory that is
        // a link into /etc.
        BindValidator.ValidateBinds(createParams.HostConfig.Binds);
        EnsureBindsStayInTheProject(createParams.HostConfig.Binds, mount);

        // Every check of the run has passed; only now are the output folders created.
        foreach (var folder in outputFolders.Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerCommandBuilder", $"Workspace dir creation failed for '{folder}'", ex);
            }
        }

        return createParams;
    }

    // The host folder a folder argument names: a container path below /workspace lies below the mounted folder, any
    // other path is taken relative to the working directory. Null for a container path that leaves /workspace.
    private static string? HostFolderOf(string dir, ContainerMount mount)
    {
        var unix = dir.Replace('\\', '/');
        if (!IsContainerSpacePath(unix, mount))
        {
            return Path.GetFullPath(Path.Combine(mount.WorkingDirFull, dir));
        }
        var collapsed = CollapseContainerPath(unix);
        if (collapsed == null)
        {
            return null;
        }
        var relative = collapsed.Length > ContainerWorkDir.Length ? collapsed[(ContainerWorkDir.Length + 1)..] : "";
        return Path.GetFullPath(Path.Combine(mount.RootFull, relative));
    }

    /// <summary>
    /// Applies the Extra Container Labels setting to <paramref name="createParams"/>: <c>-p</c> or <c>--publish</c>
    /// with the mapping after it publishes a port, on the loopback address unless the mapping names an address;
    /// every other word becomes a label, <c>key=value</c>, or <c>key</c> for <c>key=true</c>. The run and the
    /// copied <c>docker run</c> template both go through here, so they read the setting alike.
    /// </summary>
    internal static void ApplyExtraContainerLabels(CreateContainerParameters createParams, string extraFlags, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(createParams);
        ArgumentNullException.ThrowIfNull(warn);
        if (string.IsNullOrWhiteSpace(extraFlags))
        {
            return;
        }
        createParams.HostConfig ??= new HostConfig();
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
                                warn($"[Docker SDK] Warning: ignoring invalid port-range mapping '{portMappingStr}'.");
                                continue;
                            }
                            // A range holds one port more than the difference of its ends, and both sides must hold
                            // as many, or the mapping would publish other ports than the ones it names.
                            if (hostEnd - hostStart != guestEnd - guestStart)
                            {
                                warn($"[Docker SDK] Warning: ignoring port-range mapping '{portMappingStr}': the host and container ranges differ in length.");
                                continue;
                            }
                            var portCount = hostEnd - hostStart + 1;
                            if (portCount > maxRange)
                            {
                                warn($"[Docker SDK] Warning: port-range mapping '{portMappingStr}' exceeds {maxRange} ports; truncating.");
                                portCount = maxRange;
                            }
                            for (int r = 0; r < portCount; r++)
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
                            warn($"[Docker SDK] Warning: ignoring invalid port mapping '{portMappingStr}'.");
                            continue;
                        }
                        if (!string.IsNullOrEmpty(hostPart) &&
                            (!int.TryParse(hostPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hostPort) ||
                             hostPort < 0 || hostPort > 65535))
                        {
                            warn($"[Docker SDK] Warning: ignoring invalid port mapping '{portMappingStr}'.");
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

                    // Unbalanced quotes indicate a multi-line value
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

    /// <summary>
    /// True when the run gets write access to its working directory: a <c>docker.workspace</c> of the call or of the
    /// tool decides first, given as <paramref name="workspaceWritable"/>; without one, the tool's name and flags do.
    /// </summary>
    internal static bool IsWorkspaceWritable(ToolCommand command, bool? workspaceWritable)
    {
        ArgumentNullException.ThrowIfNull(command);
        return workspaceWritable ?? ToolRequiresWriteAccess(command);
    }

    /// <summary>
    /// The binds of a run. A tool writes only where release 1.2.1 let it, in its working directory. When the working
    /// directory lies below the project root, the root is bound read-only at <c>/workspace</c>, so the tool reads the
    /// whole project, and for a writing tool the working directory is bound a second time, writable, at its own path in
    /// the container. When the working directory is the project root, or there is no project file, it is bound alone
    /// at <c>/workspace</c>, read-only or writable by tool. The top-level <c>.git</c> of the writable folder, when it is
    /// a real folder or file, is bound read-only over it (<see cref="IsPlainGitMetadata"/>).
    /// </summary>
    internal static List<string> BuildBinds(ContainerMount mount, bool writable)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var binds = new List<string>(3);
        if (!PathsEqual(mount.RootCanonical, mount.WorkingDirCanonical))
        {
            // A read-only bind does not follow a symbolic link inside it to the host: such a link resolves inside the
            // container, so a .git link of the root that leads outside the project shows the tool nothing of the host.
            binds.Add($"{mount.RootFull}:{ContainerWorkDir}:ro");
            if (writable)
            {
                binds.Add($"{mount.WorkingDirFull}:{mount.ContainerWorkingDir}");
            }
        }
        else
        {
            binds.Add($"{mount.WorkingDirFull}:{ContainerWorkDir}{(writable ? "" : ":ro")}");
        }

        // The top-level .git of the writable folder stays read-only: a tool that could write .git/config or .git/hooks
        // would have the user's next git command on the host run what it wrote. Only this one path is bound, a folder or
        // the .git file of a git worktree or submodule; a repository nested deeper stays writable, as everything in the
        // working directory was under release 1.2.1. On a case-insensitive host the one bind also covers other
        // spellings such as .GIT, as measured with OrbStack on macOS, while a bind per spelling there let every write
        // through, so none is added. A .git that is a symbolic link gets no bind, as under release 1.2.1: binding it
        // would put the place it leads to into the container.
        if (writable && IsPlainGitMetadata(mount.WorkingDirFull))
        {
            binds.Add($"{Path.Combine(mount.WorkingDirFull, ".git")}:{GitContainerPath(mount)}:ro");
        }
        return binds;
    }

    private static string GitContainerPath(ContainerMount mount) => $"{mount.ContainerWorkingDir}/.git";

    /// <summary>
    /// True when the top-level <c>.git</c> of the folder is a real folder or a regular file: neither a symbolic link
    /// nor any other reparse point, such as a junction on Windows. A folder without <c>.git</c> has none.
    /// </summary>
    internal static bool IsPlainGitMetadata(string folderFull)
    {
        var gitMetadata = Path.Combine(folderFull, ".git");
        try
        {
            FileSystemInfo entry = Directory.Exists(gitMetadata) ? new DirectoryInfo(gitMetadata) : new FileInfo(gitMetadata);
            if (entry.LinkTarget != null)
            {
                return false;
            }
            return entry.Exists && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs after <see cref="BindValidator.ValidateBinds"/> has written every bind source in canonical form, and
    /// throws <see cref="DockerExecutionException"/> when a bind no longer is exactly what the run decided on. The
    /// bind at <c>/workspace</c> names the project root, read-only when the working directory lies below it; the bind
    /// at the working directory's container path names the working directory; a <c>.git</c> bind names the
    /// <c>.git</c> of the working directory, read-only; every source lies at or below the project root; and no other
    /// bind exists. Between the first look at the folders and the bind check, a writing run in the same project can
    /// replace a folder or the <c>.git</c> with a symbolic link, also one to another place inside the project; the
    /// bind would then follow it. The canonical forms are only compared here; nothing is read from the file system.
    /// </summary>
    internal static void EnsureBindsStayInTheProject(IList<string> binds, ContainerMount mount)
    {
        ArgumentNullException.ThrowIfNull(binds);
        ArgumentNullException.ThrowIfNull(mount);
        EnsureBindsMatchTheMount(binds, mount);
    }

    /// <summary>
    /// Runs the bind check of <see cref="BuildContainerParameters"/> once more on the binds it built, right before the
    /// container is created: the image may have been pulled in between, and the engine resolves each bind source
    /// again when it mounts it. The binds are already canonical, so a source that a symbolic link swapped in
    /// meanwhile now resolves to another place and fails the run. A window between this check and the mount remains.
    /// </summary>
    internal static void RecheckBinds(CreateContainerParameters createParams, ContainerMount mount)
    {
        ArgumentNullException.ThrowIfNull(createParams);
        ArgumentNullException.ThrowIfNull(mount);
        var binds = createParams.HostConfig?.Binds;
        if (binds == null)
        {
            return;
        }
        BindValidator.ValidateBinds(binds);
        EnsureBindsMatchTheMount(binds, mount);
    }

    private static void EnsureBindsMatchTheMount(IList<string> binds, ContainerMount mount)
    {
        var gitSource = Path.Combine(mount.WorkingDirCanonical, ".git");
        var belowTheRoot = !PathsEqual(mount.RootCanonical, mount.WorkingDirCanonical);
        foreach (var bind in binds)
        {
            var (host, container, options) = BindValidator.SplitDockerBind(bind);
            var source = host.Trim();
            var target = container?.Trim() ?? "";
            var readOnly = string.Equals(options?.Trim(), "ro", StringComparison.Ordinal);
            bool expected;
            if (string.Equals(target, ContainerWorkDir, StringComparison.Ordinal))
            {
                expected = PathsEqual(source, mount.RootCanonical) && (readOnly || !belowTheRoot);
            }
            else if (string.Equals(target, mount.ContainerWorkingDir, StringComparison.Ordinal))
            {
                expected = PathsEqual(source, mount.WorkingDirCanonical);
            }
            else if (string.Equals(target, GitContainerPath(mount), StringComparison.Ordinal))
            {
                expected = PathsEqual(source, gitSource) && readOnly;
            }
            else
            {
                expected = false;
            }
            if (!expected || !IsAtOrBelow(source, mount.RootCanonical))
            {
                throw new DockerExecutionException(
                    $"The project changed while the run was prepared: the bind '{bind}' no longer names the place inside " +
                    $"'{mount.RootFull}' it was made for, for example because a folder or a .git was replaced with a " +
                    "symbolic link. Start the run again.");
            }
        }
    }

    /// <summary>
    /// The host folder a run binds to <c>/workspace</c>, and the tool's working directory on the host and in the
    /// container. <see cref="RootFull"/> is the project root when the working directory lies inside a OneWare
    /// project, else the working directory itself; the canonical forms have every symbolic link resolved. Only the
    /// working directory is ever writable (<see cref="BuildBinds"/>).
    /// </summary>
    internal sealed record ContainerMount(string RootFull, string RootCanonical, string WorkingDirFull, string WorkingDirCanonical, string ContainerWorkingDir);

    /// <summary>
    /// Decides what a run mounts. When a <c>*.fpgaproj</c> file lies in the working directory or a folder above it,
    /// the folder holding it is mounted read-only at <c>/workspace</c> and the tool starts in its working directory
    /// below; a path elsewhere in the project, such as a source folder beside the build folder, is then visible to the
    /// tool. Without a project file, or when the project folder is a file system root or is or contains the user's
    /// home folder or a shared temporary folder, which would expose far more than a project, the working directory
    /// alone is mounted, as before.
    /// </summary>
    internal static ContainerMount ResolveMount(string? workingDirectory)
    {
        // The caller must supply an absolute working directory. Resolving a relative or empty value against the
        // process directory would silently mount the plugin's own bin folder
        // (e.g. "<bin>/Debug/net10.0/C:/Users/.../Project:/workspace"), producing a broken bind. Reject it
        // with an actionable message instead.
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException(
                "No working directory was supplied for the tool command; the container workspace cannot be mounted. " +
                "The caller must provide the absolute project path.");
        }
        if (!Path.IsPathRooted(workingDirectory))
        {
            throw new InvalidOperationException(
                $"The tool working directory '{workingDirectory}' is not an absolute path. It would be resolved " +
                "against the plugin's process directory and produce an incorrect bind mount. The caller must provide an " +
                "absolute project path.");
        }
        var workingDirFull = ResolvePhysicalPath(Path.GetFullPath(workingDirectory));
        if (!Path.IsPathRooted(workingDirFull))
        {
            throw new InvalidOperationException("Resolved mount path is not absolute.");
        }
        var workingDirCanonical = GetCanonicalPath(workingDirFull);

        var rootFull = workingDirFull;
        var rootCanonical = workingDirCanonical;
        var projectFile = FindProjectFile(workingDirCanonical);
        var projectDir = projectFile == null ? null : Path.GetDirectoryName(projectFile);
        if (!string.IsNullOrEmpty(projectDir))
        {
            var candidateFull = ResolvePhysicalPath(projectDir);
            var candidateCanonical = GetCanonicalPath(candidateFull);
            if (IsAtOrBelow(workingDirCanonical, candidateCanonical) && !IsTooWideToMount(candidateCanonical))
            {
                // The project root is bound read-only (BuildBinds), so its .git needs no check here: a symbolic link
                // inside a read-only bind resolves inside the container and leads to nothing of the host.
                rootFull = candidateFull;
                rootCanonical = candidateCanonical;
            }
        }

        var containerWorkingDir = ContainerWorkDir;
        if (!PathsEqual(workingDirCanonical, rootCanonical))
        {
            var relative = workingDirCanonical[WithTrailingSeparator(rootCanonical).Length..].Replace('\\', '/').TrimEnd('/');
            containerWorkingDir = $"{ContainerWorkDir}/{relative}";
        }

        // A bind is written as source:target:mode, so a colon in a bound folder's path would split the bind and the
        // engine would refuse it late, with a message that does not name the cause. The bind check writes each bind
        // with its folder in canonical form, so a colon that only the canonical form holds, reached through a
        // symbolic link, splits it as well; the message names the folder as given and, where it differs, the place it
        // resolves to.
        var bound = new[] { (rootFull, rootCanonical), (workingDirFull, workingDirCanonical) };
        foreach (var (full, canonical) in bound.Distinct())
        {
            if (HasColonOutsideDriveLetter(full, OperatingSystem.IsWindows())
                || HasColonOutsideDriveLetter(canonical, OperatingSystem.IsWindows()))
            {
                var folder = string.Equals(full, canonical, StringComparison.Ordinal)
                    ? $"'{full}'"
                    : $"'{full}', which resolves to '{canonical}',";
                throw new DockerExecutionException(
                    $"The folder {folder} cannot be mounted into the container: its path contains a colon, which a " +
                    "container bind cannot carry. Rename the folder or move it to a path without a colon.");
            }
        }
        return new ContainerMount(rootFull, rootCanonical, workingDirFull, workingDirCanonical, containerWorkingDir);
    }

    /// <summary>
    /// True when a host path holds a colon that a bind cannot carry. On Windows, when <paramref name="windows"/> is
    /// true, the colon of a drive letter at the start, such as <c>C:\</c> or <c>C:/</c>, passes, also after a device
    /// prefix, <c>\\?\</c>, <c>\\.\</c> or <c>\??\</c>, which resolving a symbolic link or junction can produce there.
    /// On any other system a path has neither, so every colon is refused. Only the string is read.
    /// </summary>
    internal static bool HasColonOutsideDriveLetter(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        var rest = path.AsSpan();
        if (!windows)
        {
            return rest.Contains(':');
        }
        if (rest.Length >= 4 && IsSeparator(rest[0]) && IsSeparator(rest[3])
            && ((IsSeparator(rest[1]) && (rest[2] is '?' or '.')) || (rest[1] == '?' && rest[2] == '?')))
        {
            rest = rest[4..];
        }
        if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':'
            && (rest.Length == 2 || IsSeparator(rest[2])))
        {
            rest = rest[2..];
        }
        return rest.Contains(':');

        static bool IsSeparator(char c) => c is '\\' or '/';
    }

    private static StringComparison HostPathComparison =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string WithTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), HostPathComparison);

    private static bool IsAtOrBelow(string path, string root) =>
        PathsEqual(path, root) || path.StartsWith(WithTrailingSeparator(root), HostPathComparison);

    /// <summary>
    /// True for a project folder that would expose more than a project: a file system root, a folder that is or
    /// contains the user's home folder or a shared temporary folder, or one that is or contains a folder the bind
    /// check blocks (<see cref="BindValidator.BlockedHostPaths"/>). A stray project file in /home, /Users or /tmp
    /// would otherwise mount every user's files or everything other programs left in the temporary folder, and one
    /// in /usr or /var would mount /usr/bin or /var/run, which the bind check refuses only for a bind at or below them.
    /// </summary>
    internal static bool IsTooWideToMount(string canonical)
    {
        if (PathsEqual(canonical, Path.GetPathRoot(canonical) ?? canonical))
        {
            return true;
        }
        return SharedFolders().Any(folder => ContainsFolder(canonical, folder))
            || BindValidator.BlockedHostPaths().Any(folder => ContainsFolder(canonical, folder));
    }

    // The folders a project root must neither be nor contain. The home folder is read from the runtime and from
    // HOME and USERPROFILE, since on Windows the runtime asks the shell and not the environment. Path.GetTempPath
    // follows TMPDIR on Unix and TMP or TEMP on Windows; the fixed Unix folders are listed beside it, because the
    // temporary folder a process reports need not be the one other programs share.
    private static IEnumerable<string?> SharedFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetEnvironmentVariable("HOME");
        yield return Environment.GetEnvironmentVariable("USERPROFILE");
        yield return Path.GetTempPath();
        if (!OperatingSystem.IsWindows())
        {
            // S5443 warns of writing into a publicly writable folder. These names are only compared against, so
            // that such a folder is refused as a mount; nothing is written there.
#pragma warning disable S5443
            yield return "/tmp";
            yield return "/private/tmp";
            yield return "/var/tmp";
#pragma warning restore S5443
        }
    }

    // True when the folder at canonical is the given folder or one of its ancestors. Both the written and the
    // canonical form of the folder are compared, so a folder reached through a symbolic link, such as /tmp on
    // macOS, is recognised either way.
    private static bool ContainsFolder(string canonical, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
        {
            return false;
        }
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        if (IsAtOrBelow(full, canonical))
        {
            return true;
        }
        try
        {
            return IsAtOrBelow(GetCanonicalPath(full), canonical);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static DockerExecutionException OutsideMountError(IEnumerable<string> paths, ContainerMount mount)
    {
        var distinct = paths.Distinct(StringComparer.Ordinal).ToList();
        return new DockerExecutionException(
            $"The tool's arguments name {distinct.Count} path(s) that the container cannot reach, because they lie outside " +
            $"'{mount.RootFull}', the folder mounted at {ContainerWorkDir}, or cannot be resolved: " +
            $"{string.Join(", ", distinct.Select(p => $"'{p}'"))}. Keep the files inside the project folder, the folder that " +
            "holds the .fpgaproj file, or without a project file inside the tool's working directory.");
    }

    /// <summary>
    /// Maps <paramref name="path"/> into the container as a run started in <paramref name="workingDirFull"/> sees it,
    /// and throws <see cref="DockerExecutionException"/> for a path the container cannot reach. There is no
    /// placeholder for such a path: the class is internal, and no caller needs one.
    /// </summary>
    public static string MapPathToContainer(string path, string workingDirFull)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        var mount = ResolveMount(workingDirFull);
        return MapPathToContainer(path, mount, outside => throw OutsideMountError([outside], mount));
    }

    /// <summary>
    /// The mapper OneWare applies while it prepares the arguments, before the run is built. OneWare hands it the
    /// whole value of a path placeholder, which may be a quoted, space-separated list of paths; such a value is
    /// split as a script is. A path the container cannot reach stays as it is here, so that building the run
    /// finds it again and fails with every such path named once.
    /// </summary>
    internal static string MapPreparedPath(string value, ContainerMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        if (!Path.IsPathRooted(value) && value.AsSpan().IndexOfAny('"', '\'') >= 0)
        {
            return MapCommandScriptPaths(value, mount, static outside => outside, stripOuterQuotes: false);
        }
        return MapPathToContainer(value, mount, static outside => outside);
    }

    /// <summary>
    /// Maps <paramref name="path"/> into the container for <paramref name="mount"/>. A path the container cannot
    /// reach goes to <paramref name="onOutside"/>, whose result takes its place.
    /// </summary>
    internal static string MapPathToContainer(string path, ContainerMount mount, Func<string, string> onOutside)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var prefix = "";
        var part = path;
        string? mapped;
        try
        {
            var eqIdx = path.IndexOf('=');
            if (eqIdx > 0)
            {
                var optionPrefix = path[..(eqIdx + 1)];
                var suffix = path[(eqIdx + 1)..];
                if (!string.IsNullOrWhiteSpace(suffix) && (suffix.Contains('/') || suffix.Contains('\\') || Path.IsPathRooted(suffix) || optionPrefix.Equals("--workdir=", StringComparison.Ordinal)))
                {
                    // Special case for GHDL work library path passed to --work=
                    if (optionPrefix.Equals("--work=", StringComparison.OrdinalIgnoreCase) || optionPrefix.Equals("-work=", StringComparison.OrdinalIgnoreCase))
                    {
                        var normalized = suffix.Replace('\\', '/');
                        var libName = Path.GetFileName(normalized.TrimEnd('/'));
                        var mappedLib = FindLibraryForDirectory(mount.WorkingDirFull, normalized);
                        var finalLibName = !string.IsNullOrWhiteSpace(mappedLib) ? mappedLib : (string.IsNullOrWhiteSpace(libName) ? "work" : libName);
                        return optionPrefix + finalLibName;
                    }

                    prefix = optionPrefix;
                    part = suffix;
                }
            }

            if (prefix.Length == 0 && path.StartsWith("-P", StringComparison.Ordinal) && path.Length > 2 && !string.IsNullOrWhiteSpace(path[2..]))
            {
                prefix = "-P";
                part = path[2..];
            }

            mapped = MapPathToContainerInternal(part, mount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A path that cannot be resolved, such as one in a loop of symbolic links, is not shown to stay inside.
            mapped = null;
        }
        return prefix + (mapped ?? onOutside(part));
    }

    private static string GetCanonicalPath(string path) => PathCanonicalizer.GetCanonicalPath(path);

    // Collapses '.' and '..' segments in an absolute container-space path and enforces that the result
    // stays within the workspace root. A path that escapes /workspace yields null, so the caller treats it
    // as a path the container cannot reach. Operates purely on the Linux container path; it must not touch
    // the host filesystem or host path semantics.
    private static string? CollapseContainerPath(string containerPath)
    {
        var segments = containerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (string.Equals(seg, ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(seg, "..", StringComparison.Ordinal))
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                continue;
            }
            stack.Add(seg);
        }
        var collapsed = "/" + string.Join('/', stack);
        return collapsed.Equals(ContainerWorkDir, StringComparison.Ordinal) || collapsed.StartsWith(ContainerWorkDir + "/", StringComparison.Ordinal)
            ? collapsed
            : null;
    }

    // True for a path, with forward slashes, that names a place in the container: /workspace or a path below it that
    // is not a path into the mounted folder on the host. It does not ask whether the path exists on the host: OneWare
    // maps paths such as the project root to /workspace before the run is built, so a host folder of that name must
    // not turn them back into host paths. A host project inside a literal /workspace folder therefore works only for
    // its own paths; a host path elsewhere below /workspace is read as a path in the container.
    private static bool IsContainerSpacePath(string normalizedPath, ContainerMount mount)
    {
        var rootUnix = mount.RootCanonical.Replace('\\', '/');
        var rootNormalizedUnix = WithTrailingSeparator(mount.RootCanonical).Replace('\\', '/');
        return (normalizedPath.StartsWith(ContainerWorkDir + "/", StringComparison.Ordinal) || normalizedPath.Equals(ContainerWorkDir, StringComparison.Ordinal))
            && !normalizedPath.StartsWith(rootNormalizedUnix, HostPathComparison)
            && !normalizedPath.Equals(rootUnix, HostPathComparison);
    }

    // Maps one path for the mount, or returns null for a path outside it. The mounted folder is the project root or
    // the working directory; the container starts in the working directory below /workspace.
    private static string? MapPathToContainerInternal(string path, ContainerMount mount)
    {
        var rootCanonical = mount.RootCanonical;
        var rootNormalized = WithTrailingSeparator(rootCanonical);
        var workingDirCanonical = mount.WorkingDirCanonical;

        var osComparison = HostPathComparison;

        var normalizedPath = path.Replace('\\', '/');

        if (IsContainerSpacePath(normalizedPath, mount))
        {
            // Already a container-space path (/workspace/...). Collapse '.'/'..' and enforce containment so a
            // crafted "/workspace/../etc/passwd" cannot escape the bind mount. Do NOT use Path.GetFullPath here;
            // it would re-anchor this Linux container path against the host OS root.
            return CollapseContainerPath(normalizedPath);
        }

        var isRelative = !Path.IsPathRooted(path);
        var fullPath = GetCanonicalPath(isRelative ? Path.Combine(workingDirCanonical, path) : path);
        var insideMount = fullPath.StartsWith(rootNormalized, osComparison) || fullPath.Equals(rootCanonical, osComparison);

        // The container starts in the working directory, so a relative path that stays in the mounted folder names the
        // same file there, and the tool's messages name it as the caller passed it. A path through a symbolic link
        // keeps its resolved form below, since the target of an absolute link does not exist inside the container.
        if (isRelative && insideMount
            && fullPath.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(workingDirCanonical, normalizedPath))), osComparison))
        {
            return normalizedPath;
        }

        if (fullPath.StartsWith(rootNormalized, osComparison))
        {
            var relativePath = fullPath[rootNormalized.Length..].Replace('\\', '/');
            return $"{ContainerWorkDir}/{relativePath}";
        }
        if (fullPath.Equals(rootCanonical, osComparison))
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
        return null;
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

    /// <summary>
    /// Maps the paths in <paramref name="script"/> with the working directory as the mounted folder, and throws
    /// <see cref="DockerExecutionException"/> for a path outside it.
    /// </summary>
    internal static string MapCommandScriptPaths(string script, string workingDirFull, string workingDirCanonical)
    {
        var mount = new ContainerMount(workingDirFull, workingDirCanonical, workingDirFull, workingDirCanonical, ContainerWorkDir);
        return MapCommandScriptPaths(script, mount, outside => throw OutsideMountError([outside], mount));
    }

    /// <summary>
    /// Maps the paths in <paramref name="script"/> for <paramref name="mount"/>. A script wrapped in quotes as a whole
    /// is unwrapped first, unless <paramref name="stripOuterQuotes"/> is false: a list of quoted paths, such as
    /// <c>"a.v" "b.v"</c>, starts and ends with a quote without being wrapped.
    /// </summary>
    internal static string MapCommandScriptPaths(string script, ContainerMount mount, Func<string, string> onOutside, bool stripOuterQuotes = true)
    {
        if (string.IsNullOrWhiteSpace(script)) return script;

        var trimmedScript = script;
        var leadingQuotes = new List<char>();
        var trailingQuotes = new List<char>();

        while (stripOuterQuotes && trimmedScript.Length >= 2)
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
                    sb.Append(MapSingleScriptSegment(part, mount, onOutside));
                }
                sb.Append(c);
                start = idx + 1;
            }
        }
        if (span.Length > start)
        {
            var part = span[start..].ToString();
            sb.Append(MapSingleScriptSegment(part, mount, onOutside));
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

    private static string MapSingleScriptSegment(string segment, ContainerMount mount, Func<string, string> onOutside)
    {
        if (string.IsNullOrWhiteSpace(segment)) return segment;

        var trimmed = segment.Trim('"', '\'', ';', ',');
        if (ShouldMapArgument(trimmed))
        {
            var mapped = MapPathToContainer(trimmed, mount, onOutside);
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
