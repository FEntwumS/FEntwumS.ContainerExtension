using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// A run inside a OneWare project mounts the project root read-only at /workspace and starts the tool in its working
/// directory below it, so a source folder beside the build folder is visible; a writing tool gets its working
/// directory alone writable, as under release 1.2.1. A path the container cannot reach fails the run with an error
/// that names it. Joins the telemetry collection and isolates the telemetry sink, because building a run may record
/// an error there.
/// </summary>
[Collection("TelemetryTests")]
public sealed class ProjectRootMountTests : IDisposable
{
    private readonly string _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_ProjectRootMount", Guid.NewGuid().ToString("N"));
    private readonly string _base = Path.Combine(Path.GetTempPath(), "prm_" + Guid.NewGuid().ToString("N"));

    // The project root as the run mounts it, every symbolic link resolved (on macOS the temp folder lies below one).
    private readonly string _root;
    private readonly string _workingDir;
    private readonly string _outside;

    public ProjectRootMountTests()
    {
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        var root = Path.Combine(_base, "riscv");
        Directory.CreateDirectory(Path.Combine(root, "Processor"));
        Directory.CreateDirectory(Path.Combine(root, "build", "netlist"));
        Directory.CreateDirectory(Path.Combine(_base, "elsewhere"));
        File.WriteAllText(Path.Combine(root, "riscv.fpgaproj"), "{}");
        File.WriteAllText(Path.Combine(root, "Processor", "ALU.v"), "module alu; endmodule");
        File.WriteAllText(Path.Combine(root, "build", "netlist", "top.v"), "module top; endmodule");
        File.WriteAllText(Path.Combine(_base, "elsewhere", "secret.v"), "module secret; endmodule");
        _root = PathCanonicalizer.GetCanonicalPath(root);
        _workingDir = Path.Combine(_root, "build", "netlist");
        _outside = Path.Combine(PathCanonicalizer.GetCanonicalPath(_base), "elsewhere");
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_telemetryDir))
            {
                Directory.Delete(_telemetryDir, true);
            }
            if (Directory.Exists(_base))
            {
                Directory.Delete(_base, true);
            }
        }
        catch { /* best effort */ }
    }

    private static ToolCommand Command(string executable, string workingDirectory, params ICommandArgument[] arguments) => new()
    {
        Executable = executable,
        ToolName = executable,
        WorkingDirectory = workingDirectory,
        CommandArguments = arguments.ToList(),
    };

    private static ToolCommand Command(string executable, string workingDirectory, params string[] arguments) =>
        Command(executable, workingDirectory, arguments.Select(argument => (ICommandArgument)new TestCommandArgument(argument)).ToArray());

    private static CreateContainerParameters Build(ToolCommand command) =>
        DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (_, _) => { });

    private static string Quoted(params string[] paths) => "\"" + string.Join("\" \"", paths) + "\"";

    [Fact]
    public void WorkingDirectoryBelowTheProjectRoot_MountsTheRootAndStartsInTheWorkingDirectory()
    {
        var script = $"read_verilog -sv {Quoted(Path.Combine(_root, "Processor", "ALU.v"), Path.Combine(_workingDir, "top.v"))}; write_json top-hier.json";

        var p = Build(Command("yosys", _workingDir, "-p", script));

        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], p.HostConfig.Binds);
        Assert.Equal("/workspace/build/netlist", p.WorkingDir);
        Assert.Equal("read_verilog -sv \"/workspace/Processor/ALU.v\" \"/workspace/build/netlist/top.v\"; write_json top-hier.json", p.Cmd[2]);
    }

    [Fact]
    public void RelativePathIntoASiblingFolder_StaysRelative()
    {
        var p = Build(Command("yosys", _workingDir, "../../Processor/ALU.v"));

        Assert.Equal("../../Processor/ALU.v", p.Cmd[1]);
    }

    [Fact]
    public void WithoutAProjectFile_TheWorkingDirectoryAloneIsMounted()
    {
        File.Delete(Path.Combine(_root, "riscv.fpgaproj"));

        var p = Build(Command("yosys", _workingDir, Path.Combine(_workingDir, "top.v")));

        Assert.Equal([$"{_workingDir}:/workspace"], p.HostConfig.Binds);
        Assert.Equal("/workspace", p.WorkingDir);
        Assert.Equal("/workspace/top.v", p.Cmd[1]);
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, Path.Combine(_root, "Processor", "ALU.v"))));
    }

    // A stray project file in the home folder, or in a folder above it such as /home or /Users, must not mount every
    // user's files: the working directory alone is mounted. HOME, USERPROFILE and the temporary folder all point into
    // the test's own folder, so the real ones are never read.
    [Theory]
    [InlineData("H/u")]
    [InlineData("H")]
    public void ProjectFileInOrAboveTheHomeFolder_MountsOnlyTheWorkingDirectory(string projectFolder)
    {
        var scratch = PathCanonicalizer.GetCanonicalPath(_base);
        var home = Path.Combine(scratch, "H", "u");
        var workingDir = Path.Combine(home, "work", "build", "netlist");
        Directory.CreateDirectory(workingDir);
        Directory.CreateDirectory(Path.Combine(scratch, "T"));
        File.WriteAllText(Path.Combine(scratch, projectFolder, "stray.fpgaproj"), "{}");
        var workingDirCanonical = PathCanonicalizer.GetCanonicalPath(workingDir);

        var p = WithRedirectedFolders(home, Path.Combine(scratch, "T"), () => Build(Command("yosys", workingDir, "-q")));

        Assert.Equal([$"{workingDirCanonical}:/workspace"], p.HostConfig.Binds);
        Assert.Equal("/workspace", p.WorkingDir);
    }

    // The same for a shared temporary folder: a project file planted in it, or in a folder above it, must not widen
    // every run below it. The temporary folder is redirected to the test's own folder.
    [Theory]
    [InlineData("T/tmp")]
    [InlineData("T")]
    public void ProjectFileInOrAboveTheTemporaryFolder_MountsOnlyTheWorkingDirectory(string projectFolder)
    {
        var scratch = PathCanonicalizer.GetCanonicalPath(_base);
        var home = Path.Combine(scratch, "home");
        var temp = Path.Combine(scratch, "T", "tmp");
        var workingDir = Path.Combine(temp, "run", "build");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workingDir);
        File.WriteAllText(Path.Combine(scratch, projectFolder, "stray.fpgaproj"), "{}");
        var workingDirCanonical = PathCanonicalizer.GetCanonicalPath(workingDir);

        var p = WithRedirectedFolders(home, temp, () => Build(Command("yosys", workingDir, "-q")));

        Assert.Equal([$"{workingDirCanonical}:/workspace"], p.HostConfig.Binds);
        Assert.Equal("/workspace", p.WorkingDir);
    }

    // The control for both guards: a project below the home folder and one below the temporary folder still mount
    // their project root.
    [Theory]
    [InlineData("home")]
    [InlineData("T/tmp")]
    public void ProjectBelowTheHomeOrTemporaryFolder_MountsTheProjectRoot(string parent)
    {
        var scratch = PathCanonicalizer.GetCanonicalPath(_base);
        var home = Path.Combine(scratch, "home");
        var temp = Path.Combine(scratch, "T", "tmp");
        var project = Path.Combine(scratch, parent, "proj");
        var workingDir = Path.Combine(project, "build");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(workingDir);
        File.WriteAllText(Path.Combine(project, "proj.fpgaproj"), "{}");
        var projectCanonical = PathCanonicalizer.GetCanonicalPath(project);

        var p = WithRedirectedFolders(home, temp, () => Build(Command("yosys", workingDir, "-q")));

        Assert.Equal([$"{projectCanonical}:/workspace:ro", $"{Path.Combine(projectCanonical, "build")}:/workspace/build"], p.HostConfig.Binds);
        Assert.Equal("/workspace/build", p.WorkingDir);
    }

    // A stray project file in a folder above one the bind check blocks, such as /usr above /usr/bin or /var above
    // /var/run, must not make that folder the mount. Only the decision is asked; nothing is written there. A folder
    // beside the blocked ones, /usr/local, stays a possible project root. Home and temporary folder are redirected to
    // the test's own folder, so they cannot decide either way.
    [Theory]
    [InlineData("/usr", true)]
    [InlineData("/var", true)]
    [InlineData("/usr/bin", true)]
    [InlineData("/usr/local", false)]
    public void ProjectFolderAboveABlockedHostFolder_IsTooWideToMount(string folder, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The blocked host folders of Linux and macOS are tested here.");
        }
        var scratch = PathCanonicalizer.GetCanonicalPath(_base);
        Directory.CreateDirectory(Path.Combine(scratch, "home"));
        Directory.CreateDirectory(Path.Combine(scratch, "T"));
        var canonical = PathCanonicalizer.GetCanonicalPath(folder);

        var tooWide = WithRedirectedFolders(Path.Combine(scratch, "home"), Path.Combine(scratch, "T"), () => DockerCommandBuilder.IsTooWideToMount(canonical));

        Assert.Equal(expected, tooWide);
    }

    // Points the home folder (HOME, USERPROFILE) and the temporary folder (TMPDIR, TMP, TEMP) at the given folders
    // while action runs, and restores every variable afterwards. The test assembly runs its tests one at a time.
    private static T WithRedirectedFolders<T>(string home, string temp, Func<T> action)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["TMPDIR"] = temp,
            ["TMP"] = temp,
            ["TEMP"] = temp,
        };
        var saved = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            return action();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void PathsOutsideTheProject_FailOnceWithEveryPathAndTheMountRoot()
    {
        var secret = Path.Combine(_outside, "secret.v");
        var other = Path.Combine(_outside, "other.v");
        var command = Command("yosys", _workingDir, "-p", $"read_verilog {Quoted(secret, other, Path.Combine(_root, "Processor", "ALU.v"))}", secret);

        var ex = Assert.Throws<DockerExecutionException>(() => Build(command));

        Assert.Contains("2 path(s)", ex.Message, StringComparison.Ordinal);
        Assert.Single(AllIndexesOf(ex.Message, $"'{secret}'"));
        Assert.Single(AllIndexesOf(ex.Message, $"'{other}'"));
        Assert.Contains($"'{_root}'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_escaped_path", ex.Message, StringComparison.Ordinal);
    }

    // The documented limit of the check: a path attached directly to an option, without '=' and other than after -P or
    // an absolute one after -o, such as -I<path> or +incdir+<path>, is not recognised as a path, so it is not mapped.
    // Checked as a whole, the option letters read as a folder in the working directory, it stays inside and reaches the
    // tool as written. The recognised forms still fail: after '=', after -P or -o, and in an argument that a space, a
    // comma or a semicolon splits.
    [Fact]
    public void OptionWithAnAttachedPath_IsPassedOnUnmappedWhileTheRecognisedFormsFail()
    {
        var secret = Path.Combine(_outside, "secret.v");
        var secretUnix = secret.Replace('\\', '/');

        Assert.Equal("-I" + secretUnix, Build(Command("yosys", _workingDir, "-I" + secret)).Cmd[1]);
        Assert.Equal("+incdir+" + secretUnix, Build(Command("iverilog", _workingDir, "+incdir+" + secret)).Cmd[1]);
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "--input=" + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-DFOO=" + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-I=" + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-P" + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-o" + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-I " + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "a.v," + secret)));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "a.v;" + secret)));
    }

    // How a run passes on an argument for an option such as -o, as the documentation describes it. An argument with an
    // attached value that is not recognised as a path, such as -obuild/x.json, is checked as a whole as a path in the
    // working directory, the option letters read as its first folder. So a '..' right after the option letters only
    // leaves that folder: the check climbs two levels fewer than the tool does.
    private static string[] Mapped(string workingDir, params string[] arguments) =>
        Build(Command("yosys", workingDir, arguments)).Cmd.Skip(1).ToArray();

    private static void AssertRefused(string workingDir, string named, params string[] arguments)
    {
        var ex = Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", workingDir, arguments)));
        Assert.Contains($"'{named}'", ex.Message, StringComparison.Ordinal);
    }

    // A working directory two levels below a folder without a project file, so that it alone is mounted.
    private string LooseWorkingDir()
    {
        var workingDir = Path.Combine(PathCanonicalizer.GetCanonicalPath(_base), "loose", "build", "netlist");
        Directory.CreateDirectory(workingDir);
        return workingDir;
    }

    [Fact]
    public void AttachedValueWithoutASeparator_ReachesTheToolUnchanged()
    {
        Assert.Equal(["-ox.json"], Mapped(_workingDir, "-ox.json"));
        Assert.Equal(["-ox.json"], Mapped(LooseWorkingDir(), "-ox.json"));

        // An attached -o does not count as an output flag, so a tool that is neither listed as writing nor as read-only
        // stays read-only.
        var p = Build(Command("mytool", _workingDir, "-ox.json"));
        Assert.Equal(["-ox.json"], p.Cmd.Skip(1));
        Assert.Equal([$"{_root}:/workspace:ro"], p.HostConfig.Binds);
    }

    [Fact]
    public void AttachedRelativeValueBelowTheWorkingDirectory_ReachesTheToolUnchanged()
    {
        Assert.Equal(["-obuild/x.json"], Mapped(_workingDir, "-obuild/x.json"));
        Assert.Equal(["-obuild/x.json"], Mapped(LooseWorkingDir(), "-obuild/x.json"));
    }

    [Fact]
    public void AttachedValueOneLevelUp_IsCheckedInTheWorkingDirectoryAndReachesTheToolUnchanged()
    {
        Assert.Equal(["-o../x.json"], Mapped(_workingDir, "-o../x.json"));
        Assert.Equal(["-o../x.json"], Mapped(LooseWorkingDir(), "-o../x.json"));
    }

    [Fact]
    public void AttachedValueTwoLevelsUp_IsCheckedInTheWorkingDirectoryAndReachesTheToolUnchanged()
    {
        Assert.Equal(["-o../../x"], Mapped(_workingDir, "-o../../x"));
        Assert.Equal(["-o../../x"], Mapped(LooseWorkingDir(), "-o../../x"));
    }

    // Checked as one level above the working directory: inside the project root, outside a working directory mounted
    // alone.
    [Fact]
    public void AttachedValueThreeLevelsUp_PassesInsideTheProjectAndFailsWithoutOne()
    {
        Assert.Equal(["-o../../../x"], Mapped(_workingDir, "-o../../../x"));
        AssertRefused(LooseWorkingDir(), "-o../../../x", "-o../../../x");
    }

    // Checked as two levels above the working directory: the project root for build/netlist, a folder above it for
    // build.
    [Fact]
    public void AttachedValueFourLevelsUp_FailsOnlyWhereTheCheckLeavesTheMount()
    {
        Assert.Equal(["-o../../../../x"], Mapped(_workingDir, "-o../../../../x"));
        AssertRefused(Path.Combine(_root, "build"), "-o../../../../x", "-o../../../../x");
        AssertRefused(LooseWorkingDir(), "-o../../../../x", "-o../../../../x");
    }

    // A link on the checked path makes the argument come back as the container path it resolves to, without its option
    // letters, as in release 1.2.1; a link that leads out of the project fails the run.
    [Fact]
    public void AttachedValueThroughASymbolicLink_ComesBackResolvedWithoutTheOption()
    {
        var build = Path.Combine(_root, "build");
        CreateLinkOrSkip(Path.Combine(build, "link"), Path.Combine(_root, "Processor"), isFolder: true);
        CreateLinkOrSkip(Path.Combine(build, "outlink"), _outside, isFolder: true);

        Assert.Equal(["/workspace/Processor/ALU.v"], Mapped(build, "-o../../link/ALU.v"));
        AssertRefused(build, "-o../../outlink/secret.v", "-o../../outlink/secret.v");
    }

    // A value after -o that starts with ~/ is recognised as a path. The extension does not expand the ~, so it checks
    // a folder ~ in the working directory and passes the argument on as written. A tool may still expand it itself,
    // as yosys does with its HOME, and then reaches a place in the container that this check never saw.
    [Fact]
    public void AttachedValueStartingWithATilde_IsAFolderInTheWorkingDirectory()
    {
        Assert.Equal(["-o~/x"], Mapped(_workingDir, "-o~/x"));
        Assert.Equal(["-o~/../../../x"], Mapped(_workingDir, "-o~/../../../x"));
        AssertRefused(_workingDir, "~/../../../../x", "-o~/../../../../x");
    }

    // A value without a / or \ that is neither rooted nor starts with ~/ or ~\ is not checked, also a bare '..' on its
    // own, after -o or after '='. Where the working directory alone is mounted, it names the folder above /workspace,
    // which the check never sees; the same parent with a separator fails the run.
    [Fact]
    public void BareParentFolderWithoutASeparator_ReachesTheToolUnchecked()
    {
        var loose = LooseWorkingDir();
        Assert.Equal([".."], Mapped(loose, ".."));
        Assert.Equal(["-o", ".."], Mapped(loose, "-o", ".."));
        Assert.Equal(["--input=.."], Mapped(loose, "--input=.."));
        AssertRefused(loose, "../", "../");
    }

    [Fact]
    public void AttachedAbsoluteValueInTheWorkingDirectory_IsMappedBelowTheProjectRoot()
    {
        var path = Path.Combine(_workingDir, "x.json");
        Assert.Equal(["-o/workspace/build/netlist/x.json"], Mapped(_workingDir, "-o" + path));
    }

    [Fact]
    public void AttachedAbsoluteValueElsewhereInTheProject_IsMappedBelowTheProjectRoot()
    {
        var path = Path.Combine(_root, "Processor", "x");
        Assert.Equal(["-o/workspace/Processor/x"], Mapped(_workingDir, "-o" + path));
    }

    [Fact]
    public void AttachedAbsoluteValueOutsideTheProject_FailsUnlessItIsAnImagePath()
    {
        var outside = Path.Combine(_outside, "x");
        AssertRefused(_workingDir, outside, "-o" + outside);
        Assert.Equal(["-o/dev/null"], Mapped(_workingDir, "-o/dev/null"));
    }

    // A value after a separate -o is a path on its own: it is checked as the tool reads it.
    [Fact]
    public void ValueAfterASeparateOption_IsCheckedAsTheToolReadsIt()
    {
        Assert.Equal(["-o", "x.json"], Mapped(_workingDir, "-o", "x.json"));
        Assert.Equal(["-o", "build/x.json"], Mapped(_workingDir, "-o", "build/x.json"));
        Assert.Equal(["-o", "../x.json"], Mapped(_workingDir, "-o", "../x.json"));
        Assert.Equal(["-o", "~/x"], Mapped(_workingDir, "-o", "~/x"));
        var alu = Path.Combine(_root, "Processor", "ALU.v");
        Assert.Equal(["-o", "/workspace/Processor/ALU.v"], Mapped(_workingDir, "-o", alu));
        AssertRefused(_workingDir, "../../../../x", "-o", "../../../../x");
        AssertRefused(LooseWorkingDir(), "../x.json", "-o", "../x.json");
    }

    // A value attached to -P is recognised as a path, a relative one too, and checked as the tool reads it.
    [Fact]
    public void ValueAttachedToP_IsCheckedAsTheToolReadsIt()
    {
        Assert.Equal(["-Pfoo"], Mapped(_workingDir, "-Pfoo"));
        Assert.Equal(["-P../lib"], Mapped(_workingDir, "-P../lib"));
        Assert.Equal(["-P~/x"], Mapped(_workingDir, "-P~/x"));
        Assert.Equal(["-P/workspace/Processor"], Mapped(_workingDir, "-P" + Path.Combine(_root, "Processor")));
        AssertRefused(_workingDir, "../../../../x", "-P../../../../x");
    }

    [Fact]
    public void ValueAfterAnEqualsSign_IsCheckedAsTheToolReadsIt()
    {
        Assert.Equal(["-o=build/x"], Mapped(_workingDir, "-o=build/x"));
        Assert.Equal(["-o=../x"], Mapped(_workingDir, "-o=../x"));
        AssertRefused(_workingDir, "../../../../x", "-o=../../../../x");
    }

    // Every option with an attached value is read the same way, -I and +incdir+ as -o.
    [Fact]
    public void OtherAttachedValues_AreCheckedWithTheOptionLettersAsAFolder()
    {
        Assert.Equal(["-I../../../x"], Mapped(_workingDir, "-I../../../x"));
        Assert.Equal(["+incdir+../../../x"], Mapped(_workingDir, "+incdir+../../../x"));
        Assert.Equal(["-I/a/../../../x"], Mapped(_workingDir, "-I/a/../../../x"));
        AssertRefused(_workingDir, "-I../../../../../x", "-I../../../../../x");
    }

    [Fact]
    public void SymbolicLinkInsideTheProjectToAFolderOutside_IsRejected()
    {
        var link = Path.Combine(_root, "vendor");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This host does not allow creating a symbolic link.");
        }

        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, Path.Combine(link, "secret.v"))));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "../../vendor/secret.v")));
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-p", $"read_verilog {Quoted(Path.Combine(link, "secret.v"))}")));
    }

    [Fact]
    public void ContainerSpaceEscape_StillFails()
    {
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "/workspace/../etc/passwd")));
        Assert.Equal("/workspace/Processor/ALU.v", Build(Command("yosys", _workingDir, "/workspace/build/../Processor/ALU.v")).Cmd[1]);
    }

    // OneWare maps the project root and a file in it to /workspace while it prepares the command. The build takes both
    // for paths in the container, and the run is built. Whether a path names a place in the container is read from the
    // path alone and does not depend on the host, so a host folder named /workspace would change nothing.
    [Fact]
    public void PreparedProjectRoot_OnAHostWithAWorkspaceFolder_StaysAContainerPath()
    {
        var command = Command("yosys", _workingDir,
            new TemplateArgumentStandIn("{root}", ("{root}", _root, true)),
            new TemplateArgumentStandIn("{file}", ("{file}", Path.Combine(_root, "Processor", "ALU.v"), true)));
        var mount = DockerCommandBuilder.ResolveMount(command.WorkingDirectory);
        command.PrepareCommand(OSPlatform.Linux, path => DockerCommandBuilder.MapPreparedPath(path, mount));

        var p = Build(command);

        Assert.Equal("/workspace", p.Cmd[1]);
        Assert.Equal("/workspace/Processor/ALU.v", p.Cmd[2]);
    }

    // A host project inside a folder literally named /workspace: a path into the project root is a host path and maps
    // into the mount, while another path below /workspace is taken for a path in the container and kept as it is, so
    // it names a place inside the project in the container rather than the host file.
    [Fact]
    public void HostProjectInsideAWorkspaceFolder_MapsOnlyItsOwnPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A path that starts with /workspace is not a full host path on Windows.");
        }

        const string root = "/workspace/project";
        var mount = new DockerCommandBuilder.ContainerMount(root, root, root, root, "/workspace");
        string Map(string path) => DockerCommandBuilder.MapPathToContainer(path, mount, outside => throw new InvalidOperationException(outside));

        Assert.Equal("/workspace", Map(root));
        Assert.Equal("/workspace/Processor/ALU.v", Map(root + "/Processor/ALU.v"));
        Assert.Equal("/workspace/other/top.v", Map("/workspace/other/top.v"));
    }

    // A bind is written as source:target:mode, so a colon in a bound folder's path splits it. The project folder here
    // has a colon inside its name; the run fails before it is built, with a message that names the folder and says to
    // rename or move it. Windows allows no colon in a folder name, so the check is tested there on path strings below.
    [Fact]
    public void ProjectFolderWithAColonInItsName_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A folder name cannot hold a colon on Windows.");
        }

        var root = Path.Combine(PathCanonicalizer.GetCanonicalPath(_base), "riscv:v2");
        var workingDir = Path.Combine(root, "build");
        Directory.CreateDirectory(workingDir);
        File.WriteAllText(Path.Combine(root, "riscv.fpgaproj"), "{}");

        var ex = Assert.Throws<DockerExecutionException>(() => DockerCommandBuilder.ResolveMount(workingDir));
        Assert.Contains($"'{root}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("contains a colon, which a container bind cannot carry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Rename the folder or move it", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("again", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", workingDir, "-q")));

        // Without the project file the working directory alone is mounted, and its path holds the same colon.
        File.Delete(Path.Combine(root, "riscv.fpgaproj"));
        var alone = Assert.Throws<DockerExecutionException>(() => DockerCommandBuilder.ResolveMount(workingDir));
        Assert.Contains($"'{workingDir}'", alone.Message, StringComparison.Ordinal);
    }

    // The bind check writes each bind with its host folder in canonical form, every symbolic link resolved, so a colon
    // that only the canonical form holds splits the bind as well. Here the working directory is reached through a link
    // to a folder with a colon in its name: once with no project file, where the working directory alone is bound at
    // /workspace, and once below a project root that holds no colon itself. Both runs fail before they are built, with
    // the colon message that names the folder and the place it resolves to, not with a message to start again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkingDirReachedThroughALinkToAFolderWithAColon_IsRefused(bool projectFile)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A folder name cannot hold a colon on Windows.");
        }

        var parent = Path.Combine(PathCanonicalizer.GetCanonicalPath(_base), "linked");
        var target = Path.Combine(parent, "fpga:v2");
        Directory.CreateDirectory(Path.Combine(target, "build"));
        if (projectFile)
        {
            File.WriteAllText(Path.Combine(parent, "linked.fpgaproj"), "{}");
        }
        CreateLinkOrSkip(Path.Combine(parent, "link"), target, isFolder: true);
        var workingDir = Path.Combine(parent, "link", "build");
        var resolved = Path.Combine(target, "build");
        Assert.False(DockerCommandBuilder.HasColonOutsideDriveLetter(workingDir, OperatingSystem.IsWindows()));

        var run = Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", workingDir, "-q")));
        Assert.Contains("contains a colon, which a container bind cannot carry", run.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("again", run.Message, StringComparison.OrdinalIgnoreCase);

        var ex = Assert.Throws<DockerExecutionException>(() => DockerCommandBuilder.ResolveMount(workingDir));
        Assert.Contains($"'{workingDir}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{resolved}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("contains a colon, which a container bind cannot carry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Rename the folder or move it", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // On Windows only the colon of a drive letter at the start passes, also after the device prefix that resolving a
    // link or junction can produce there; a colon anywhere else is refused. On any other system every colon is refused,
    // since a path there has neither a drive letter nor a device prefix: /??/C:/riscv is a folder named ?? at the root.
    // Only the string is read, and the system is a parameter, so every case runs on every host.
    [Theory]
    [InlineData(@"C:\Users\u\riscv", true, false)]
    [InlineData("C:/Users/u/riscv", true, false)]
    [InlineData(@"d:\riscv\build", true, false)]
    [InlineData(@"C:\", true, false)]
    [InlineData(@"\\?\C:\Users\u\riscv", true, false)]
    [InlineData(@"\\.\C:\Users\u\riscv", true, false)]
    [InlineData(@"\??\C:\Users\u\riscv", true, false)]
    [InlineData("//?/C:/Users/u/riscv", true, false)]
    [InlineData("/??/C:/riscv", true, false)]
    [InlineData(@"\\?/C:/riscv", true, false)]
    [InlineData(@"\\?\UNC\server\share\riscv", true, false)]
    [InlineData(@"\\server\share\riscv", true, false)]
    [InlineData("/home/u/riscv/build", true, false)]
    [InlineData(@"C:\Users\u\riscv:v2", true, true)]
    [InlineData("C:/Users/u/riscv:v2/build", true, true)]
    [InlineData(@"\\?\C:\Users\u\riscv:v2", true, true)]
    [InlineData(@"\\?\C:\Users\u\riscv\C:", true, true)]
    [InlineData(@"\\server\share\riscv:v2", true, true)]
    [InlineData("/home/u/riscv:v2/build", true, true)]
    [InlineData("/C:/riscv", true, true)]
    [InlineData(@"CC:\riscv", true, true)]
    [InlineData("C:riscv", true, true)]
    [InlineData("/home/u/riscv/build", false, false)]
    [InlineData(@"\\server\share\riscv", false, false)]
    [InlineData(@"\\?\UNC\server\share\riscv", false, false)]
    [InlineData("/home/u/riscv:v2/build", false, true)]
    [InlineData("/??/C:/riscv", false, true)]
    [InlineData("/??/C:", false, true)]
    [InlineData(@"/\?\C:/riscv", false, true)]
    [InlineData(@"/\.\C:/riscv", false, true)]
    [InlineData("//?/C:/Users/u/riscv", false, true)]
    [InlineData(@"C:\Users\u\riscv", false, true)]
    [InlineData("C:/Users/u/riscv", false, true)]
    [InlineData("/C:/riscv", false, true)]
    public void ColonOutsideADriveLetter_IsFound(string path, bool windows, bool refused)
    {
        Assert.Equal(refused, DockerCommandBuilder.HasColonOutsideDriveLetter(path, windows));
    }

    // On Windows every test folder lies below a drive letter, so a run there shows that the drive letter's colon passes
    // the check in ResolveMount itself and reaches the bind.
    [Fact]
    public void ProjectFolderBelowADriveLetter_IsMounted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Paths start with a drive letter only on Windows.");
        }

        var mount = DockerCommandBuilder.ResolveMount(_workingDir);

        Assert.Matches(@"^[A-Za-z]:[\\/]", mount.RootFull);
        Assert.Equal(
            [$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"],
            DockerCommandBuilder.BuildBinds(mount, writable: true));
    }

    [Fact]
    public void AllowlistedSystemPath_StillPassesThrough()
    {
        Assert.Equal("/dev/null", Build(Command("yosys", _workingDir, "/dev/null")).Cmd[1]);
    }

    // A writing tool below the project root reads the whole project and writes only in its working directory, as
    // under release 1.2.1: the root is bound read-only, the working directory a second time writable at its own path.
    [Fact]
    public void WritingToolBelowTheProjectRoot_GetsTheRootReadOnlyAndOnlyItsWorkingDirectoryWritable()
    {
        var p = Build(Command("yosys", _workingDir, "-q"));

        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], p.HostConfig.Binds);
        Assert.Equal("/workspace/build/netlist", p.WorkingDir);
        BindValidator.ValidateBinds(p.HostConfig.Binds);
        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], p.HostConfig.Binds);
    }

    [Fact]
    public void ReadOnlyTool_GetsTheWholeProjectReadOnly()
    {
        var p = Build(Command("gtkwave", _workingDir, Path.Combine(_workingDir, "top.v")));

        Assert.Equal([$"{_root}:/workspace:ro"], p.HostConfig.Binds);
    }

    // A docker.workspace of rw or ro decides as the tool's name does: rw adds the writable working directory, ro
    // leaves the read-only root alone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkspaceAccessOfTheCall_DecidesWhetherTheWorkingDirectoryIsWritable(bool writable)
    {
        var p = DockerCommandBuilder.BuildContainerParameters("img:latest", Command("gtkwave", _workingDir, "-q"), null!, null, null, (_, _) => { }, workspaceWritable: writable);

        string[] expected = writable ? [$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"] : [$"{_root}:/workspace:ro"];
        Assert.Equal(expected, p.HostConfig.Binds);
    }

    [Fact]
    public void EnvFile_IsReadFromTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "FROM_ROOT=1\n");
        File.WriteAllText(Path.Combine(_workingDir, ".env"), "FROM_WORKING_DIR=1\n");

        var p = Build(Command("yosys", _workingDir, "-q"));

        Assert.Contains("FROM_WORKING_DIR=1", p.Env);
        Assert.DoesNotContain("FROM_ROOT=1", p.Env);
    }

    // An output folder elsewhere in the project lies in the read-only root, where the tool cannot write, so the run
    // does not create it; the path is still mapped.
    [Fact]
    public void OutputFolderElsewhereInTheProject_IsNotCreated()
    {
        var output = Path.Combine(_root, "build", "synth", "top.json");

        var p = Build(Command("yosys", _workingDir, "-o", output));

        Assert.False(Directory.Exists(Path.Combine(_root, "build", "synth")));
        Assert.Equal("/workspace/build/synth/top.json", p.Cmd[2]);
    }

    [Fact]
    public void OutputFolderInsideTheWorkingDirectory_IsCreated()
    {
        var output = Path.Combine(_workingDir, "synth", "top.json");

        var p = Build(Command("yosys", _workingDir, "-o", output));

        Assert.True(Directory.Exists(Path.Combine(_workingDir, "synth")));
        Assert.Equal("/workspace/build/netlist/synth/top.json", p.Cmd[2]);
    }

    // The run is built for the mount its caller resolved while preparing the arguments, and resolves one itself only
    // without it. The mount given here, the working directory alone, differs from the one the project file selects,
    // so the bind shows which was used.
    [Fact]
    public void MountResolvedByTheCaller_IsUsedAndNotResolvedAgain()
    {
        var resolved = DockerCommandBuilder.ResolveMount(_workingDir);
        var workingDirOnly = resolved with { RootFull = resolved.WorkingDirFull, RootCanonical = resolved.WorkingDirCanonical, ContainerWorkingDir = "/workspace" };

        var given = DockerCommandBuilder.BuildContainerParameters("img:latest", Command("yosys", _workingDir, "-q"), null!, null, null, (_, _) => { }, mount: workingDirOnly);
        var own = Build(Command("yosys", _workingDir, "-q"));

        Assert.Equal([$"{_workingDir}:/workspace"], given.HostConfig.Binds);
        Assert.Equal("/workspace", given.WorkingDir);
        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], own.HostConfig.Binds);
    }

    // A run that fails because of a path outside the mount leaves no folder behind, also not the output folder of an
    // argument before the failing one.
    [Fact]
    public void RejectedRun_CreatesNoOutputFolder()
    {
        var output = Path.Combine(_workingDir, "rejected", "top.json");

        Assert.Throws<DockerExecutionException>(() => Build(Command("yosys", _workingDir, "-o", output, Path.Combine(_outside, "secret.v"))));

        Assert.False(Directory.Exists(Path.Combine(_workingDir, "rejected")));
    }

    // A .git of the project root that is a symbolic link to a folder the bind check blocks, here /etc, leaves the
    // project root mounted read-only and binds nothing below /etc: the bind check passes, and an output folder inside
    // the working directory is created as in any other run.
    [Fact]
    public void GitLinkOfTheProjectRootToABlockedFolder_IsNotBound()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The blocked host folders of Linux and macOS are tested here.");
        }
        CreateLinkOrSkip(Path.Combine(_root, ".git"), "/etc", isFolder: false);
        var output = Path.Combine(_workingDir, "bindchecked", "top.json");

        // Built and checked as the strategy does it, so the test holds wherever the bind check runs.
        var p = Build(Command("yosys", _workingDir, "-o", output));
        BindValidator.ValidateBinds(p.HostConfig.Binds);

        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], p.HostConfig.Binds);
        AssertEveryBindSourceIsAtOrBelow(p.HostConfig.Binds, _root);
        Assert.True(Directory.Exists(Path.Combine(_workingDir, "bindchecked")));
    }

    // OneWare maps a path placeholder while it prepares the command, before the run is built, so the output path
    // reaches the build in container form. Its folder is created on the host when it lies in the working directory,
    // and not when it lies elsewhere in the read-only project.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutputFolderOfAPreparedPath_IsCreatedOnlyInsideTheWorkingDirectory(bool insideTheWorkingDirectory)
    {
        var folder = insideTheWorkingDirectory ? Path.Combine(_workingDir, "synth") : Path.Combine(_root, "build", "synth");
        var expected = insideTheWorkingDirectory ? "/workspace/build/netlist/synth/top.json" : "/workspace/build/synth/top.json";
        var command = Command("yosys", _workingDir,
            new TestCommandArgument("-o"),
            new TemplateArgumentStandIn("{out}", ("{out}", Path.Combine(folder, "top.json"), true)));
        var mount = DockerCommandBuilder.ResolveMount(command.WorkingDirectory);

        command.PrepareCommand(OSPlatform.Linux, path => DockerCommandBuilder.MapPreparedPath(path, mount));
        var p = Build(command);

        Assert.Equal(expected, command.Arguments.ElementAt(1));
        Assert.Equal(expected, p.Cmd[2]);
        Assert.Equal(insideTheWorkingDirectory, Directory.Exists(folder));
    }

    [Fact]
    public void CopiedDockerRun_ShowsTheProjectBindAndTheWorkingDirectory()
    {
        var p = Build(Command("yosys", _workingDir, "-q"));

        var copied = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: CommandShell.Posix);

        Assert.Contains("-v " + DockerRunCommandFormatter.Quote($"{_root}:/workspace:ro", CommandShell.Posix), copied, StringComparison.Ordinal);
        Assert.Contains("-v " + DockerRunCommandFormatter.Quote($"{_workingDir}:/workspace/build/netlist", CommandShell.Posix), copied, StringComparison.Ordinal);
        Assert.Contains("-w /workspace/build/netlist", copied, StringComparison.Ordinal);
    }

    // Creates the .git of a folder: a folder with hooks, the .git file of a git worktree or submodule, or a symbolic
    // link to a folder of repository metadata beside it. Skips the test when the host cannot create a link.
    private static void CreateGitMetadata(string folder, string kind)
    {
        var git = Path.Combine(folder, ".git");
        switch (kind)
        {
            case "folder":
                Directory.CreateDirectory(Path.Combine(git, "hooks"));
                break;
            case "file":
                File.WriteAllText(git, "gitdir: ../repository/.git/worktrees/riscv\n");
                break;
            default:
                var metadata = Path.Combine(folder, "repository-metadata");
                Directory.CreateDirectory(Path.Combine(metadata, "hooks"));
                CreateLinkOrSkip(git, metadata, isFolder: true);
                break;
        }
    }

    // A writing tool whose working directory is the project root gets the root writable, as under release 1.2.1, but
    // never its repository metadata: a .git folder, or the .git file of a git worktree or submodule, is bound a second
    // time read-only over the writable bind. A .git that is a symbolic link gets no bind, as under release 1.2.1.
    [Theory]
    [InlineData("folder")]
    [InlineData("file")]
    [InlineData("link")]
    public void GitMetadataOfAWritingToolAtTheProjectRoot_IsBoundReadOnlyUnlessALink(string kind)
    {
        CreateGitMetadata(_root, kind);
        var git = Path.Combine(_root, ".git");

        var p = Build(Command("yosys", _root, "-q"));

        string[] expected = kind == "link" ? [$"{_root}:/workspace"] : [$"{_root}:/workspace", $"{git}:/workspace/.git:ro"];
        Assert.Equal(expected, p.HostConfig.Binds);
        Assert.Equal("/workspace", p.WorkingDir);
        var copied = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: CommandShell.Posix);
        Assert.Equal(kind != "link", copied.Contains(DockerRunCommandFormatter.Quote($"{git}:/workspace/.git:ro", CommandShell.Posix), StringComparison.Ordinal));
        BindValidator.ValidateBinds(p.HostConfig.Binds);
        DockerCommandBuilder.EnsureBindsStayInTheProject(p.HostConfig.Binds, DockerCommandBuilder.ResolveMount(_root));
        Assert.Equal(expected, p.HostConfig.Binds);
    }

    // The same when the working directory is the mounted folder, here without a project file.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GitMetadataInTheMountedWorkingDirectory_IsBoundReadOnly(bool isFolder)
    {
        File.Delete(Path.Combine(_root, "riscv.fpgaproj"));
        var git = Path.Combine(_workingDir, ".git");
        if (isFolder)
        {
            Directory.CreateDirectory(git);
        }
        else
        {
            File.WriteAllText(git, "gitdir: ../../.git/modules/netlist\n");
        }

        var p = Build(Command("yosys", _workingDir, "-q"));

        Assert.Equal([$"{_workingDir}:/workspace", $"{git}:/workspace/.git:ro"], p.HostConfig.Binds);
        BindValidator.ValidateBinds(p.HostConfig.Binds);
        Assert.Equal(2, p.HostConfig.Binds.Count);
    }

    // Below the project root, the .git bind protects the writable working directory: a real .git there is bound
    // read-only at its own path in the container, a link is not, and the .git of the read-only root is never bound.
    [Theory]
    [InlineData("folder")]
    [InlineData("file")]
    [InlineData("link")]
    public void GitMetadataInAWorkingDirectoryBelowTheProjectRoot_IsBoundReadOnlyUnlessALink(string kind)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        CreateGitMetadata(_workingDir, kind);
        var git = Path.Combine(_workingDir, ".git");

        var p = Build(Command("yosys", _workingDir, "-q"));
        BindValidator.ValidateBinds(p.HostConfig.Binds);

        List<string> expected = [$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"];
        if (kind != "link")
        {
            expected.Add($"{git}:/workspace/build/netlist/.git:ro");
        }
        Assert.Equal(expected, p.HostConfig.Binds);
    }

    // A read-only tool gets no .git bind: nothing it is given is writable.
    [Fact]
    public void ReadOnlyTool_GetsNoGitBind()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        Directory.CreateDirectory(Path.Combine(_workingDir, ".git"));

        Assert.Equal([$"{_root}:/workspace:ro"], Build(Command("gtkwave", _workingDir, "-q")).HostConfig.Binds);
        Assert.Equal([$"{_root}:/workspace:ro"], Build(Command("gtkwave", _root, "-q")).HostConfig.Binds);
    }

    // A .git of the project root that is a symbolic link to a folder outside the project, one the bind check allows,
    // no longer keeps the project root from being mounted: it is mounted read-only, the link is not bound, and no bind
    // source lies outside the project.
    [Fact]
    public void GitLinkOfTheProjectRootToAFolderOutside_StillMountsTheRootReadOnly()
    {
        CreateLinkOrSkip(Path.Combine(_root, ".git"), _outside, isFolder: true);

        var mount = DockerCommandBuilder.ResolveMount(_workingDir);
        var p = Build(Command("yosys", _workingDir, "-q"));
        BindValidator.ValidateBinds(p.HostConfig.Binds);

        Assert.Equal(_root, mount.RootCanonical);
        Assert.Equal([$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"], p.HostConfig.Binds);
        Assert.Equal("/workspace/build/netlist", p.WorkingDir);
        AssertEveryBindSourceIsAtOrBelow(p.HostConfig.Binds, _root);
        Assert.DoesNotContain(p.HostConfig.Binds, bind => bind.StartsWith(_outside, StringComparison.OrdinalIgnoreCase));
    }

    // A .git link to a folder outside, in a writable folder mounted alone: the working directory as the project root,
    // and a working directory without a project file. The folder is mounted as under release 1.2.1, with no .git bind,
    // so no bind source lies outside it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GitLinkToAFolderOutsideOfTheMountedFolder_IsNotBound(bool workingDirIsTheProjectRoot)
    {
        if (!workingDirIsTheProjectRoot)
        {
            File.Delete(Path.Combine(_root, "riscv.fpgaproj"));
        }
        var mounted = workingDirIsTheProjectRoot ? _root : _workingDir;
        CreateLinkOrSkip(Path.Combine(mounted, ".git"), _outside, isFolder: true);

        var mount = DockerCommandBuilder.ResolveMount(mounted);
        var p = Build(Command("yosys", mounted, "-q"));
        BindValidator.ValidateBinds(p.HostConfig.Binds);

        Assert.Equal(mounted, mount.RootCanonical);
        Assert.Equal([$"{mounted}:/workspace"], p.HostConfig.Binds);
        Assert.Equal("/workspace", p.WorkingDir);
        AssertEveryBindSourceIsAtOrBelow(p.HostConfig.Binds, mounted);
    }

    // A .git that is a symbolic link whose target does not exist, or a loop of links, gets no bind wherever the target
    // would lie. Below the root the project root is mounted read-only with the working directory writable; when the
    // working directory is the root, it is mounted alone. The run creates no target.
    [Theory]
    [InlineData("inside", true)]
    [InlineData("inside", false)]
    [InlineData("outside", true)]
    [InlineData("outside", false)]
    [InlineData("loop", true)]
    [InlineData("loop", false)]
    public void BrokenGitLink_IsNotBound(string target, bool workingDirBelowTheRoot)
    {
        var missing = target switch
        {
            "inside" => Path.Combine(_root, "not-yet-created"),
            "outside" => Path.Combine(_outside, "not-yet-created"),
            _ => ".git",
        };
        CreateLinkOrSkip(Path.Combine(_root, ".git"), missing, isFolder: true);
        var workingDir = workingDirBelowTheRoot ? _workingDir : _root;

        var mount = DockerCommandBuilder.ResolveMount(workingDir);
        var p = Build(Command("yosys", workingDir, "-q"));
        BindValidator.ValidateBinds(p.HostConfig.Binds);

        Assert.False(DockerCommandBuilder.IsPlainGitMetadata(_root));
        Assert.Equal(_root, mount.RootCanonical);
        string[] expected = workingDirBelowTheRoot ? [$"{_root}:/workspace:ro", $"{_workingDir}:/workspace/build/netlist"] : [$"{_root}:/workspace"];
        Assert.Equal(expected, p.HostConfig.Binds);
        if (target != "loop")
        {
            Assert.False(Path.Exists(missing));
        }
    }

    // The check after the bind check: every bind source lies at or below the project root, and a .git bind names the
    // .git of the writable folder itself. A .git that became a link meanwhile has its target as the canonical source,
    // and a folder that became a link has a source outside the project; both fail the run.
    [Fact]
    public void BindsThatChangedWhileTheRunWasPrepared_FailTheRun()
    {
        var mount = DockerCommandBuilder.ResolveMount(_workingDir);
        var root = $"{_root}:/workspace:ro";
        var workingDir = $"{_workingDir}:/workspace/build/netlist";

        DockerCommandBuilder.EnsureBindsStayInTheProject([root, workingDir, $"{Path.Combine(_workingDir, ".git")}:/workspace/build/netlist/.git:ro"], mount);

        var gitRedirected = Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.EnsureBindsStayInTheProject([root, workingDir, $"{Path.Combine(_root, "repository-metadata")}:/workspace/build/netlist/.git:ro"], mount));
        Assert.Contains("project changed while the run was prepared", gitRedirected.Message, StringComparison.Ordinal);
        Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.EnsureBindsStayInTheProject([root, workingDir, $"{Path.Combine(_root, ".git")}:/workspace/build/netlist/.git:ro"], mount));
        Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.EnsureBindsStayInTheProject([root, workingDir, $"{Path.Combine(_workingDir, ".git")}:/workspace/.git:ro"], mount));

        var outside = Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.EnsureBindsStayInTheProject([root, $"{_outside}:/workspace/build/netlist"], mount));
        Assert.Contains("project changed while the run was prepared", outside.Message, StringComparison.Ordinal);
        Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.EnsureBindsStayInTheProject([$"{_outside}:/workspace:ro"], mount));
    }

    // Each bind must be exactly the one the run decided on, not merely lie inside the project: a working directory
    // that became a link to the root's .git or to a sibling folder would otherwise make that place writable.
    [Fact]
    public void BindsRedirectedInsideTheProject_FailTheRun()
    {
        var mount = DockerCommandBuilder.ResolveMount(_workingDir);
        var root = $"{_root}:/workspace:ro";
        string[][] redirected =
        [
            [root, $"{Path.Combine(_root, ".git")}:/workspace/build/netlist"],
            [root, $"{Path.Combine(_root, "Processor")}:/workspace/build/netlist"],
            [root, $"{_root}:/workspace/build/netlist"],
            [$"{_root}:/workspace", $"{_workingDir}:/workspace/build/netlist"],
            [root, $"{_workingDir}:/workspace/build/netlist", $"{Path.Combine(_workingDir, ".git")}:/workspace/build/netlist/.git"],
            [root, $"{_workingDir}:/workspace/build/netlist", $"{Path.Combine(_root, "Processor")}:/workspace/Processor:ro"],
        ];

        foreach (var binds in redirected)
        {
            var ex = Assert.Throws<DockerExecutionException>(() => DockerCommandBuilder.EnsureBindsStayInTheProject(binds, mount));
            Assert.Contains("project changed while the run was prepared", ex.Message, StringComparison.Ordinal);
        }

        // At the project root the one bind may be writable or read-only, as the tool decides.
        var atTheRoot = DockerCommandBuilder.ResolveMount(_root);
        DockerCommandBuilder.EnsureBindsStayInTheProject([$"{_root}:/workspace"], atTheRoot);
        DockerCommandBuilder.EnsureBindsStayInTheProject([$"{_root}:/workspace:ro"], atTheRoot);
    }

    // A concurrent writing run replaces the working directory with a relative link after the mount was resolved and
    // before the run is built; the run fails instead of binding the place the link leads to writable.
    [Theory]
    [InlineData("../.git")]
    [InlineData("../src")]
    [InlineData("..")]
    public void WorkingDirectoryReplacedWithALinkInsideTheProject_FailsTheRun(string target)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var mount = DockerCommandBuilder.ResolveMount(_workingDir);
        Directory.Move(_workingDir, _workingDir + "-before");
        CreateLinkOrSkip(_workingDir, target.Replace('/', Path.DirectorySeparatorChar), isFolder: true);

        var ex = Assert.Throws<DockerExecutionException>(() =>
            DockerCommandBuilder.BuildContainerParameters("img:latest", Command("yosys", _workingDir, "-q"), null!, null, null, (_, _) => { }, workspaceWritable: true, mount: mount));

        Assert.Contains("project changed while the run was prepared", ex.Message, StringComparison.Ordinal);
    }

    // The bind check runs once more after the image is made available, right before the container is created: a
    // writing run in the same project that replaces the working directory with a link to the root's .git during a
    // pull fails the run instead of having that .git bound writable.
    [Fact]
    public void BindsRecheckedAfterTheWorkingDirectoryBecameALink_FailTheRun()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        var mount = DockerCommandBuilder.ResolveMount(_workingDir);
        var p = DockerCommandBuilder.BuildContainerParameters("img:latest", Command("yosys", _workingDir, "-q"), null!, null, null, (_, _) => { }, workspaceWritable: true, mount: mount);
        DockerCommandBuilder.RecheckBinds(p, mount);

        Directory.Move(_workingDir, _workingDir + "-before");
        CreateLinkOrSkip(_workingDir, Path.Combine("..", ".git"), isFolder: true);

        var ex = Assert.Throws<DockerExecutionException>(() => DockerCommandBuilder.RecheckBinds(p, mount));
        Assert.Contains("project changed while the run was prepared", ex.Message, StringComparison.Ordinal);
    }

    // The same through a whole run: the stand-in daemon reports the image as present and, while it is asked for it,
    // the working directory is replaced with a link to the root's .git. No container is created.
    [Fact]
    public async Task ExecuteAsync_WorkingDirectoryReplacedWhileTheImageIsEnsured_CreatesNoContainer()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
        CreateLinkOrSkip(Path.Combine(_base, "probe"), _outside, isFolder: true);
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        daemon.OnImageInspect = () =>
        {
            if (!Directory.Exists(_workingDir + "-before"))
            {
                Directory.Move(_workingDir, _workingDir + "-before");
                Directory.CreateSymbolicLink(_workingDir, Path.Combine("..", ".git"));
            }
        };
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = _workingDir,
            CommandArguments = [new TestCommandArgument("-q")],
            OutputHandler = line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
                return true;
            },
            ErrorHandler = line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
                return true;
            },
        };

        var (success, _) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.True(Directory.Exists(_workingDir + "-before"), "The stand-in daemon was never asked for the image.");
        Assert.True(lines.Any(line => line.Contains("project changed while the run was prepared", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(daemon.Paths, path => path.Contains("/containers/create", StringComparison.Ordinal));
    }

    // A run below the project root whose root .git leads outside it mounts the root read-only and says so at the Info
    // level, with no line about the .git.
    [Fact]
    public async Task ExecuteAsync_GitLinkLeadingOutsideTheProjectRoot_StillMountsTheRoot()
    {
        CreateLinkOrSkip(Path.Combine(_root, ".git"), _outside, isFolder: true);
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = _workingDir,
            CommandArguments = [new TestCommandArgument(Path.Combine(_outside, "secret.v"))],
            OutputHandler = line =>
            {
                lines.Add(line);
                return true;
            },
        };

        var (success, _) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Single(lines, line => line.Contains($"Mounting the project root '{_root}' read-only", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("leads outside", StringComparison.Ordinal));
    }

    private static void CreateLinkOrSkip(string link, string target, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This host does not allow creating a symbolic link.");
        }
    }

    // Every host path a bind names, as the bind check wrote it, is the folder or lies below it.
    private static void AssertEveryBindSourceIsAtOrBelow(IEnumerable<string> binds, string folder)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var bind in binds)
        {
            var source = Path.TrimEndingDirectorySeparator(BindValidator.SplitDockerBind(bind).host);
            Assert.True(
                string.Equals(source, folder, comparison) || source.StartsWith(folder + Path.DirectorySeparatorChar, comparison),
                $"The bind '{bind}' names a host path outside '{folder}'.");
        }
    }

    [Fact]
    public void QuotedPathListOfOnePlaceholder_IsMappedWhileOneWarePreparesTheCommand()
    {
        var list = Quoted(Path.Combine(_root, "Processor", "ALU.v"), Path.Combine(_workingDir, "top.v"));
        var command = Command("yosys", _workingDir,
            new TestCommandArgument("-p"),
            new TemplateArgumentStandIn("read_verilog -sv {files}; write_json top-hier.json", ("{files}", list, true)));
        var mount = DockerCommandBuilder.ResolveMount(command.WorkingDirectory);

        command.PrepareCommand(OSPlatform.Linux, path => DockerCommandBuilder.MapPreparedPath(path, mount));

        Assert.Equal("read_verilog -sv \"/workspace/Processor/ALU.v\" \"/workspace/build/netlist/top.v\"; write_json top-hier.json", command.Arguments.ElementAt(1));
        Assert.Equal(command.Arguments.ElementAt(1), Build(command).Cmd[2]);
    }

    [Fact]
    public void PathOutsideTheProjectInAPlaceholder_IsLeftForTheBuildToReport()
    {
        var secret = Path.Combine(_outside, "secret.v");
        var command = Command("yosys", _workingDir,
            new TestCommandArgument("-p"),
            new TemplateArgumentStandIn("read_verilog {files}", ("{files}", Quoted(secret), true)),
            new TemplateArgumentStandIn("{one}", ("{one}", secret, true)));
        var mount = DockerCommandBuilder.ResolveMount(command.WorkingDirectory);

        command.PrepareCommand(OSPlatform.Linux, path => DockerCommandBuilder.MapPreparedPath(path, mount));

        Assert.Equal($"read_verilog \"{secret.Replace('\\', '/')}\"", command.Arguments.ElementAt(1));
        var ex = Assert.Throws<DockerExecutionException>(() => Build(command));
        Assert.Contains("1 path(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PathOutsideTheProject_FailsBeforeTheImageIsLookedUp()
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        var errors = new List<string>();
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = _workingDir,
            CommandArguments =
            [
                new TestCommandArgument("-p"),
                new TemplateArgumentStandIn("read_verilog {files}", ("{files}", Quoted(Path.Combine(_root, "Processor", "ALU.v"), Path.Combine(_outside, "secret.v")), true)),
            ],
            ErrorHandler = line =>
            {
                errors.Add(line);
                return true;
            },
        };

        var (success, _) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.DoesNotContain(daemon.Paths, path => path.Contains("/images/", StringComparison.Ordinal));
        Assert.Contains(errors, line => line.Contains(nameof(DockerExecutionException), StringComparison.Ordinal) && line.Contains("secret.v", StringComparison.Ordinal));
    }

    // At the Info level the run says which project root it mounts read-only when that is not the working directory,
    // and for a writing tool that its working directory is writable; it says nothing of it when the working directory
    // is the mounted folder. A path outside the project ends each run before an image is looked up.
    [Theory]
    [InlineData(true, "yosys")]
    [InlineData(true, "gtkwave")]
    [InlineData(false, "yosys")]
    public async Task ExecuteAsync_ProjectRootAboveTheWorkingDirectory_IsLoggedAtInfo(bool belowTheRoot, string tool)
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        var command = new ToolCommand
        {
            Executable = tool,
            ToolName = tool,
            WorkingDirectory = belowTheRoot ? _workingDir : _root,
            CommandArguments = [new TestCommandArgument(Path.Combine(_outside, "secret.v"))],
            OutputHandler = line =>
            {
                lines.Add(line);
                return true;
            },
        };

        var (success, _) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.False(success);
        var access = tool == "yosys" ? ", its working directory, which alone is mounted writable." : ".";
        var expected = $"[Docker SDK] Mounting the project root '{_root}' read-only at /workspace; the tool starts in /workspace/build/netlist{access}";
        if (belowTheRoot)
        {
            Assert.Contains(lines, line => line.EndsWith(expected, StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(lines, line => line.Contains("Mounting the project root", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WindowsPathsInMixedCase_MapIntoTheProject()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows paths are rooted only on Windows.");
        }

        // The drive letter in lower case and the folders above the project in upper case name the same folder.
        var shouted = char.ToLowerInvariant(_root[0]) + _root[1..].ToUpperInvariant();
        var list = Quoted(Path.Combine(shouted, "Processor", "ALU.v"), Path.Combine(shouted, "build", "netlist", "top.v"));
        var command = Command("yosys", Path.Combine(shouted, "build", "netlist"),
            new TestCommandArgument("-p"),
            new TemplateArgumentStandIn("read_verilog -sv {files}", ("{files}", list, true)));
        var mount = DockerCommandBuilder.ResolveMount(command.WorkingDirectory);

        command.PrepareCommand(OSPlatform.Linux, path => DockerCommandBuilder.MapPreparedPath(path, mount));
        var p = Build(command);

        Assert.Equal("read_verilog -sv \"/workspace/Processor/ALU.v\" \"/workspace/build/netlist/top.v\"", p.Cmd[2]);
        Assert.Equal("/workspace/build/netlist", p.WorkingDir);
    }

    // The project path in upper case, as a path in a script or a placeholder may spell it, with the folders inside the
    // project as they are.
    private string Shouted(params string[] inside) => Path.Combine([_root.ToUpperInvariant(), .. inside]);

    // The file systems of macOS ignore case by default, so the project path in another case names the project.
    [Fact]
    public void MacOSPathInAnotherCase_MapsIntoTheProject()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Paths compare without case only on macOS here; Windows has its own test.");
        }

        var p = Build(Command("yosys", _workingDir, "-p", $"read_verilog {Quoted(Shouted("Processor", "ALU.v"))}", Shouted("build", "netlist", "top.v")));

        Assert.Equal("read_verilog \"/workspace/Processor/ALU.v\"", p.Cmd[2]);
        Assert.Equal("/workspace/build/netlist/top.v", p.Cmd[3]);
    }

    // Linux compares paths with case, so the same input names folders that do not exist and lies outside the mount.
    [Fact]
    public void LinuxPathInAnotherCase_IsOutsideTheMount()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Paths compare with case only on Linux.");
        }

        var ex = Assert.Throws<DockerExecutionException>(() =>
            Build(Command("yosys", _workingDir, "-p", $"read_verilog {Quoted(Shouted("Processor", "ALU.v"))}", Shouted("build", "netlist", "top.v"))));

        Assert.Contains("2 path(s)", ex.Message, StringComparison.Ordinal);
    }

    private static List<int> AllIndexesOf(string text, string value)
    {
        var result = new List<int>();
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Behaves as OneWare's TemplateArgument (OneWare 1.0.45, src/OneWare.ToolEngine/Arguments/TemplateArgument.cs),
    /// which lives in the host and not in the package the tests build against: the mapper gets the whole value of a
    /// path placeholder, and its result has its separators turned to the target platform's.
    /// </summary>
    private sealed class TemplateArgumentStandIn : ICommandArgument
    {
        private readonly string _template;
        private readonly (string Placeholder, string Value, bool IsPath)[] _mappings;
        private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

        public TemplateArgumentStandIn(string template, params (string Placeholder, string Value, bool IsPath)[] mappings)
        {
            _template = template;
            _mappings = mappings;
            foreach (var mapping in mappings)
            {
                _resolved[mapping.Placeholder] = mapping.Value;
            }
        }

        public void Prepare(OSPlatform osPlatform, Func<string, string>? pathMapper = null)
        {
            foreach (var (placeholder, value, isPath) in _mappings)
            {
                if (!isPath)
                {
                    _resolved[placeholder] = value;
                    continue;
                }
                var mapped = pathMapper != null ? pathMapper(value) : value;
                _resolved[placeholder] = osPlatform == OSPlatform.Windows ? mapped.Replace('/', '\\') : mapped.Replace('\\', '/');
            }
        }

        public string GetArgument() => _resolved.Aggregate(_template, (text, pair) => text.Replace(pair.Key, pair.Value, StringComparison.Ordinal));
    }
}
