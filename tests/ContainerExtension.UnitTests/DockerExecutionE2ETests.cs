#pragma warning disable xUnit1051, CA2201
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ContainerExtension;
using ContainerExtension.Validations;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Comprehensive E2E test suite (71 test cases) for the OneWare Container Extension.
/// </summary>
[Collection("TelemetryTests")]
public sealed class DockerExecutionE2ETests : IDisposable
{
    private readonly string _testTelemetryDir;
    private readonly string _localTestsDir;

    public DockerExecutionE2ETests()
    {
        _testTelemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_E2E", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_testTelemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";

        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, "local_tests")))
        {
            current = current.Parent;
        }
        if (current != null)
        {
            _localTestsDir = Path.Combine(current.FullName, "local_tests");
        }
        else
        {
            _localTestsDir = "/Users/mtorun/Library/Mobile Documents/com~apple~CloudDocs/Masterarbeit/FEntwumS.ContainerExtension/local_tests";
        }
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.LogLevelChecker = () => "Verbose";
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_testTelemetryDir))
            {
                Directory.Delete(_testTelemetryDir, true);
            }
        }
        catch { /* best effort */ }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        }
        foreach (var sub in Directory.GetDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }
    }

    private List<ICommandArgument> BuildArgs(params string[] args)
    {
        return args.Select(a => (ICommandArgument)new E2ETestCommandArgument(a)).ToList();
    }

    // =======================================================================
    //  TIER 1: HAPPY PATH TESTS (30 tests)
    // =======================================================================

    // -- Feature 1: VHDL Simulation Flow Happy-Paths --

    [FactIfNoCI]
    public async Task F1_GHDL_Analyze_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_Analyze_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "work-obj93.cf")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_Elaborate_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_Elab_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var cmdAnalyze = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd", "VHDL_Blink_tb.vhd")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdAnalyze);
            Assert.True(s1);

            var cmdElab = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-e", "VHDL_Blink_tb")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdElab);
            Assert.True(s2);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_Simulate_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_Sim_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var cmdAnalyze = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd", "VHDL_Blink_tb.vhd")
            };
            await strategy.ExecuteAsync(cmdAnalyze);

            var cmdElab = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-e", "VHDL_Blink_tb")
            };
            await strategy.ExecuteAsync(cmdElab);

            var cmdSim = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-r", "VHDL_Blink_tb", "--stop-time=1us")
            };
            var (success, _) = await strategy.ExecuteAsync(cmdSim);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_WorkLib_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_WorkLib_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "--work=custom_lib", "VHDL_Blink.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "custom_lib-obj93.cf")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_SyntaxCheck_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_Syntax_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-s", "VHDL_Blink.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 2: Verilog Simulation & Synthesis Happy-Paths --

    [FactIfNoCI]
    public async Task F2_Verilog_Compile_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_VCompile_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Verilog_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_iverilog", "hdlc/iverilog:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "iverilog",
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-o", "Blink.vvp", "Verilog_Blink.v", "Verilog_Blink_tb.v")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "Blink.vvp")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F2_Verilog_Execute_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_VExecute_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Verilog_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_iverilog", "hdlc/iverilog:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Compile first
            var cmdCompile = new ToolCommand
            {
                Executable = "iverilog",
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-o", "Blink.vvp", "Verilog_Blink.v", "Verilog_Blink_tb.v")
            };
            await strategy.ExecuteAsync(cmdCompile);

            // Execute using vvp
            var cmdExec = new ToolCommand
            {
                Executable = "iverilog/vvp", // force read-write mount via path trick
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("Blink.vvp")
            };
            var (success, _) = await strategy.ExecuteAsync(cmdExec);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F2_Verilator_Compile_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_Verilator_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Verilog_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_verilator", "hdlc/verilator:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "verilator",
                ToolName = "verilator",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--cc", "Verilog_Blink.v")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F2_Yosys_Synth_Ice40_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_YosysIce_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_yosys", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys",
                ToolName = "yosys",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-p", "synth_ice40 -json ice40_blink.json", "ice40_blink.v")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "ice40_blink.json")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F2_Yosys_Synth_Ecp5_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_YosysEcp_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "ECP5_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_yosys", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys",
                ToolName = "yosys",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-p", "synth_ecp5 -json ecp5_blink_test.json", "ecp5_blink.v")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "ecp5_blink_test.json")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 3: Formal Verification Happy-Paths --

    [FactIfNoCI]
    public async Task F3_Formal_SbyRun_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_SbyRun_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(Directory.Exists(Path.Combine(tempDir, "Blink")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_SbyFail_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_SbyFail_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Mutate Blink.v so that assertion fails immediately
            var blinkV = Path.Combine(tempDir, "Blink.v");
            var content = File.ReadAllText(blinkV);
            var mutatedContent = content.Replace("assert (counter < 24'hFFFFFF);", "assert (counter < 5);");
            File.WriteAllText(blinkV, mutatedContent);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success); // Verification should fail due to assertion violation
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_SbyTask_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_SbyTask_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Mutate Blink.sby to add a task definition
            var sbyPath = Path.Combine(tempDir, "Blink.sby");
            var sbyContent = "[tasks]\nbmc\n\n" + File.ReadAllText(sbyPath);
            File.WriteAllText(sbyPath, sbyContent);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby", "bmc")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_SbyEngine_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_SbyEngine_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_SbyClean_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_SbyClean_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };

            // Run twice; the second run should clean the output directory without error
            var (s1, _) = await strategy.ExecuteAsync(command);
            Assert.True(s1);
            var (s2, _) = await strategy.ExecuteAsync(command);
            Assert.True(s2);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 4: Physical/Bitstream Flow Happy-Paths --

    [FactIfNoCI]
    public async Task F4_NextPNR_Ice40_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_NextIce_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ice40", "hdlc/nextpnr:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "nextpnr-ice40",
                ToolName = "nextpnr-ice40",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ice40_blink.json", "--pcf", "ice40_blink.pcf", "--asc", "test_ice.asc")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "test_ice.asc")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_IcePack_Gen_HappyPath()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_IcePack_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_icepack", "fentwums/oss-cad-suite:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "icepack",
                ToolName = "icepack",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("ice40_blink.asc", "test_out.bin")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "test_out.bin")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_NextPNR_Ecp5_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_NextEcp_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "ECP5_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ecp5", "hdlc/nextpnr:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "nextpnr-ecp5",
                ToolName = "nextpnr-ecp5",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ecp5_blink.json", "--lpf", "ecp5_blink.lpf", "--textcfg", "test_ecp.config")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "test_ecp.config")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_EcpPack_Gen_HappyPath()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_EcpPack_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "ECP5_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ecppack", "fentwums/oss-cad-suite:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ecppack",
                ToolName = "ecppack",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("ecp5_blink.config", "test_out.bit")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "test_out.bit")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_NextPNR_Ice40_Pcf_HappyPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_NextIcePcf_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ice40", "hdlc/nextpnr:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "nextpnr-ice40",
                ToolName = "nextpnr-ice40",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ice40_blink.json", "--pcf", "ice40_blink.pcf", "--asc", "temp_pcf.asc")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(tempDir, "temp_pcf.asc")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 5: Telemetry & Log Verification Happy-Paths --

    [FactIfNoCI]
    public void F5_Telemetry_SuccessLog_HappyPath()
    {
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.LogExecution("hdlc/ghdl:latest", "ghdl", 1.5, 0);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.Equal("hdlc/ghdl:latest", entries[0].Image);
        Assert.Equal(0, entries[0].ExitCode);
    }

    [FactIfNoCI]
    public void F5_Telemetry_FailureLog_HappyPath()
    {
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.LogExecution("hdlc/ghdl:latest", "ghdl", 2.1, 1);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.Equal(1, entries[0].ExitCode);
    }

    [FactIfNoCI]
    public void F5_Telemetry_StatsUpdate_HappyPath()
    {
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.LogExecution("img", "tool", 1.0, 0);
        ContainerTelemetry.LogExecution("img", "tool", 2.0, 1);

        var (total, successRate, avgDuration) = ContainerTelemetry.GetStats();
        Assert.Equal(2, total);
        Assert.Equal(50.0, successRate);
        Assert.Equal(1.5, avgDuration);
    }

    [FactIfNoCI]
    public void F5_Telemetry_LevelVerbose_HappyPath()
    {
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.ClearEntries();

        try
        {
            throw new InvalidOperationException("verbose error");
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("comp", "act", ex);
        }

        System.Threading.Thread.Sleep(50);

        var file = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("verbose error", content);
        Assert.Contains("stack", content);
    }

    [FactIfNoCI]
    public void F5_Telemetry_LevelErrorsOnly_HappyPath()
    {
        ContainerTelemetry.LogLevelChecker = () => "Errors Only";
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.TrackError("comp", "act", new InvalidOperationException("errors only test"));
        System.Threading.Thread.Sleep(50);

        var file = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("errors only test", content);
    }

    // -- Feature 6: Diagnostics & Status Checks Happy-Paths --

    [FactIfNoCI]
    public async Task F6_Diagnostics_PingSuccess_HappyPath()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        await strategy.EnsureInitializedAsync();
        await strategy.Client.System.PingAsync();
        Assert.NotNull(strategy.DetectedRuntime);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_ImageValidate_HappyPath()
    {
        var validator = new DockerImageFormatValidation();
        var result = validator.Validate("hdlc/ghdl:yosys", out var warning);
        Assert.True(result);
        Assert.Null(warning);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_SocketURI_HappyPath()
    {
        var validator = new DaemonSocketValidation();
        bool expected = !OperatingSystem.IsWindows(); // unix socket not valid on windows unless changed, npipes valid on windows.
        var result = validator.Validate("unix:///var/run/docker.sock", out _);
        Assert.Equal(expected, result);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_ContainerPrefix_HappyPath()
    {
        var validator = new ContainerNameValidation();
        var result = validator.Validate("my-test-prefix-", out var warning);
        Assert.True(result);
        Assert.Null(warning);
    }

    [FactIfNoCI]
    public async Task F6_Diagnostics_AllowNativeFallback_HappyPath()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.AllowNativeFallbackSetting, true);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, "unix:///invalid/offline/socket.sock");

        using var strategy = new DockerExecutionStrategy(provider);
        var cmd = new ToolCommand
        {
            Executable = "git",
            ToolName = "git",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("--version")
        };

        var (success, _) = await strategy.ExecuteAsync(cmd);
        Assert.True(success);
    }

    // =======================================================================
    //  TIER 2: BOUNDARY & CORNER TESTS (30 tests)
    // =======================================================================

    // -- Feature 1: VHDL Simulation Flow Boundary/Corner --

    [FactIfNoCI]
    public async Task F1_GHDL_MissingFile_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "ghdl",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("-a", "non_existent_file_xyz.vhd")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F1_GHDL_CompileSyntaxError_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_SyntaxErr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "bad.vhd"), "entity bad is invalid syntax;");

            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "bad.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_MissingEntity_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_MissingEnt_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var cmdAnalyze = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd")
            };
            await strategy.ExecuteAsync(cmdAnalyze);

            var cmdElab = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-e", "NonExistentEntity")
            };
            var (success, _) = await strategy.ExecuteAsync(cmdElab);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_SimulateTimeout_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_SimTimeout_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue(ContainerExtensionModule.TimeoutSetting, 0.0001); // ultra short timeout
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var cmdAnalyze = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd", "VHDL_Blink_tb.vhd")
            };
            await strategy.ExecuteAsync(cmdAnalyze);

            var cmdElab = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-e", "VHDL_Blink_tb")
            };
            await strategy.ExecuteAsync(cmdElab);

            var cmdSim = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-r", "VHDL_Blink_tb", "--stop-time=1s")
            };
            var (success, output) = await strategy.ExecuteAsync(cmdSim);
            Assert.False(success);
            Assert.Equal("Cancelled", output);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F1_GHDL_EmptySource_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F1_Empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "empty.vhd"), "");

            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "empty.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 2: Verilog Simulation & Synthesis Boundary/Corner --

    [FactIfNoCI]
    public async Task F2_Verilog_MissingFile_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "iverilog",
            ToolName = "iverilog",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("-o", "out.vvp", "missing_file_abc.v")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F2_Verilog_CompileSyntaxError_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F2_VSyntaxErr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "bad.v"), "module bad invalid syntax;");

            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "iverilog",
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-o", "out.vvp", "bad.v")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F2_Verilog_ExecuteMissingVvp_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "iverilog/vvp", // force read-write mount via path trick
            ToolName = "vvp",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("missing_file.vvp")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F2_Verilator_InvalidFlag_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "verilator",
            ToolName = "verilator",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("--invalid-flag-abc")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F2_Yosys_InvalidCommand_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("-p", "invalid_command_abc")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    // -- Feature 3: Formal Verification Boundary/Corner --

    [FactIfNoCI]
    public async Task F3_Formal_MissingSby_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "yosys/sby", // force read-write mount via path trick
            ToolName = "sby",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("-f", "missing_config.sby")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F3_Formal_MalformedSby_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_MalformedSby_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "bad.sby"), "[options]\nmode bmc\n[engines]\n[script]\n");

            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "bad.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_MissingSource_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_MissingSrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "config.sby"), "[options]\nmode bmc\ndepth 5\n[engines]\nsmtbmc z3\n[script]\nread -formal missing.v\nprep -top top\n[files]\nmissing.v\n");

            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "config.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_Timeout_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_Timeout_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue(ContainerExtensionModule.TimeoutSetting, 0.0001); // 6ms timeout
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, output) = await strategy.ExecuteAsync(command);
            Assert.False(success);
            Assert.Equal("Cancelled", output);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F3_Formal_EmptyConfig_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F3_EmptySby_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "empty.sby"), "");

            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "empty.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // -- Feature 4: Physical/Bitstream Flow Boundary/Corner --

    [FactIfNoCI]
    public async Task F4_NextPNR_MissingNetlist_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "nextpnr-ice40",
            ToolName = "nextpnr-ice40",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("--json", "missing_netlist.json", "--asc", "out.asc")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F4_NextPNR_InvalidPackage_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_BadPackage_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "nextpnr-ice40",
                ToolName = "nextpnr-ice40",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ice40_blink.json", "--package", "invalid_package_option")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_IcePack_MissingAsc_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "icepack",
            ToolName = "icepack",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("missing.asc", "out.bin")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    [FactIfNoCI]
    public async Task F4_NextPNR_Ecp5_InvalidConstraints_Boundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F4_BadConstraints_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "ECP5_Flow"), tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "bad.lpf"), "invalid constraints syntax;");

            using var provider = new E2ETestServiceProvider();
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "nextpnr-ecp5",
                ToolName = "nextpnr-ecp5",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ecp5_blink.json", "--lpf", "bad.lpf", "--textcfg", "out.config")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.False(success);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F4_EcpPack_MissingConfig_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "ecppack",
            ToolName = "ecppack",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = BuildArgs("missing.config", "out.bit")
        };
        var (success, _) = await strategy.ExecuteAsync(command);
        Assert.False(success);
    }

    // -- Feature 5: Telemetry & Log Verification Boundary/Corner --

    [FactIfNoCI]
    public void F5_Telemetry_MaxRetention_Boundary()
    {
        ContainerTelemetry.ClearEntries();
        for (int i = 0; i < 70; i++)
        {
            ContainerTelemetry.LogExecution("img", $"tool_{i}", 1.0, 0, maxEntries: 20);
        }

        var entries = ContainerTelemetry.GetRecentEntries(100);
        Assert.True(entries.Count <= 20);
    }

    [FactIfNoCI]
    public void F5_Telemetry_OOMDetection_Boundary()
    {
        var buffer = new StringBuilder();
        buffer.Append(new string('a', 9 * 1024 * 1024)); // > 8MB

        ContainerTelemetry.ClearEntries();
        DockerExecutionStrategy.DrainLines(buffer, "a", _ => true); // textSpan must not be empty to trigger OOM shield

        Assert.Equal(0, buffer.Length); // should be cleared
    }

    [FactIfNoCI]
    public void F5_Telemetry_LogTimestamps_Boundary()
    {
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.LogLevelChecker = () => "Verbose";

        try
        {
            throw new InvalidOperationException("timestamp test");
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("comp", "act", ex);
        }

        System.Threading.Thread.Sleep(50);

        var file = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("\"ts\"", content);
    }

    [FactIfNoCI]
    public void F5_Telemetry_ClearReset_Boundary()
    {
        ContainerTelemetry.LogExecution("img", "tool", 1.0, 0);
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.ClearEntries(); // clear multiple times should be safe

        var (total, _, _) = ContainerTelemetry.GetStats();
        Assert.Equal(0, total);
    }

    [FactIfNoCI]
    public void F5_Telemetry_CommandTracing_Boundary()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "--label custom=val");

        using var strategy = new DockerExecutionStrategy(provider);
        var cmd = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "ghdl",
            WorkingDirectory = "/dummy",
            CommandArguments = BuildArgs("-a", "file.vhd")
        };

        var runCommand = strategy.GenerateDockerRunCommand();
        Assert.Contains("--label custom=val", runCommand);
    }

    // -- Feature 6: Diagnostics & Status Checks Boundary/Corner --

    [FactIfNoCI]
    public void F6_Diagnostics_InvalidImageFormat_Boundary()
    {
        var validator = new DockerImageFormatValidation();
        var result = validator.Validate("INVALID IMAGE NAME!", out var warning);
        Assert.False(result);
        Assert.NotNull(warning);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_InvalidSocketURI_Boundary()
    {
        var validator = new DaemonSocketValidation();
        var result = validator.Validate("ftp://invalid-scheme", out var warning);
        Assert.False(result);
        Assert.NotNull(warning);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_NamePrefixOverLength_Boundary()
    {
        var validator = new ContainerNameValidation();
        var longPrefix = new string('a', 65);
        var result = validator.Validate(longPrefix, out var warning);
        Assert.False(result);
        Assert.NotNull(warning);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_ResourceLimitWarning_Boundary()
    {
        // 13000 MB RAM is > 75% of 16384 MB total RAM, should produce warning
        var validator = new ResourceThresholdValidation(16384.0 * 0.75, 16384.0, "RAM (MB)");
        var result = validator.Validate(14000.0, out var warning);
        Assert.True(result);
        Assert.NotNull(warning);
    }

    [FactIfNoCI]
    public void F6_Diagnostics_ResourceLimitOutOfBounds_Boundary()
    {
        var validator = new ResourceThresholdValidation(8000.0, 16000.0, "RAM (MB)");

        // Negative limit
        var r1 = validator.Validate(-10.0, out var w1);
        Assert.False(r1);
        Assert.NotNull(w1);

        // Exceeds total
        var r2 = validator.Validate(20000.0, out var w2);
        Assert.False(r2);
        Assert.NotNull(w2);
    }

    // =======================================================================
    //  TIER 3: CROSS-FEATURE PAIRWISE TESTS (6 tests)
    // =======================================================================

    [FactIfNoCI]
    public async Task F7_Verilog_To_Physical_CrossFeature()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F7_VerilogToPhys_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_yosys", "hdlc/ghdl:yosys");
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ice40", "hdlc/nextpnr:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Step 1: Synthesis
            var cmdYosys = new ToolCommand
            {
                Executable = "yosys",
                ToolName = "yosys",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-p", "synth_ice40 -json test_synth.json", "ice40_blink.v")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdYosys);
            Assert.True(s1);

            // Step 2: Routing
            var cmdPnr = new ToolCommand
            {
                Executable = "nextpnr-ice40",
                ToolName = "nextpnr-ice40",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "test_synth.json", "--pcf", "ice40_blink.pcf", "--asc", "out.asc")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdPnr);
            Assert.True(s2);
            Assert.True(File.Exists(Path.Combine(tempDir, "out.asc")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F7_VHDL_To_Telemetry_CrossFeature()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F7_VhdlToTel_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            ContainerTelemetry.ClearEntries();
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);

            var entries = ContainerTelemetry.GetRecentEntries(10);
            Assert.Single(entries);
            Assert.Equal("ghdl", entries[0].Tool);
            Assert.Equal(0, entries[0].ExitCode);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F7_Formal_To_Telemetry_CrossFeature()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F7_FormalToTel_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            ContainerTelemetry.ClearEntries();
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);

            var entries = ContainerTelemetry.GetRecentEntries(10);
            Assert.Single(entries);
            Assert.Equal("sby", entries[0].Tool);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public void F7_Diagnostics_To_ExecutionParameters_CrossFeature()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.MemoryLimitSetting, 1024.0);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.CpuLimitSetting, 2.0);

        var cmd = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "ghdl",
            WorkingDirectory = "/dummy",
            CommandArguments = BuildArgs("-a", "file.vhd")
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "hdlc/ghdl:yosys", cmd, provider.SettingsService, null, null, (c, l) => { });

        Assert.Equal(1024 * 1024 * 1024L, param.HostConfig.Memory);
        Assert.Equal(2000000000L, param.HostConfig.NanoCPUs);
    }

    [FactIfNoCI]
    public async Task F7_Physical_To_Telemetry_CrossFeature()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var tempDir = Path.Combine(Path.GetTempPath(), "F7_PhysToTel_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            ContainerTelemetry.ClearEntries();
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_icepack", "fentwums/oss-cad-suite:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "icepack",
                ToolName = "icepack",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("ice40_blink.asc", "out.bin")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);

            var entries = ContainerTelemetry.GetRecentEntries(10);
            Assert.Single(entries);
            Assert.Equal("icepack", entries[0].Tool);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public void F7_Telemetry_With_DiagnosticsLimitPropagation_CrossFeature()
    {
        var validator = new ResourceThresholdValidation(8000.0, 16000.0, "memory");
        ContainerTelemetry.ClearEntries();

        // Validate an out-of-bounds limit, which triggers warning or error
        var valid = validator.Validate(14000.0, out var warning);
        Assert.True(valid);
        Assert.NotNull(warning);

        // Verify diagnostic limit warnings are routed to log
        ContainerTelemetry.TrackError("ResourceThresholdValidation", "MemoryWarning", null, warning);
        System.Threading.Thread.Sleep(50);

        var file = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("Memory", content);
    }

    // =======================================================================
    //  TIER 4: REAL-WORLD WORKLOADS (5 tests)
    // =======================================================================

    [FactIfNoCI]
    public async Task F8_Blinky_VHDL_Flow_Workload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F8_BlinkyVHDL_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "VHDL_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_ghdl", "hdlc/ghdl:yosys");
            using var strategy = new DockerExecutionStrategy(provider);

            // Step 1: Compile/Analyze Design
            var cmdAnalyzeDesign = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink.vhd")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdAnalyzeDesign);
            Assert.True(s1);

            // Step 2: Compile/Analyze Testbench
            var cmdAnalyzeTb = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-a", "VHDL_Blink_tb.vhd")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdAnalyzeTb);
            Assert.True(s2);

            // Step 3: Elaborate
            var cmdElab = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-e", "VHDL_Blink_tb")
            };
            var (s3, _) = await strategy.ExecuteAsync(cmdElab);
            Assert.True(s3);

            // Step 4: Simulate
            var cmdSim = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-r", "VHDL_Blink_tb", "--stop-time=100us")
            };
            var (s4, _) = await strategy.ExecuteAsync(cmdSim);
            Assert.True(s4);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F8_Blinky_Verilog_Flow_Workload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F8_BlinkyVerilog_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Verilog_Blink"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_iverilog", "hdlc/iverilog:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Step 1: Compile
            var cmdCompile = new ToolCommand
            {
                Executable = "iverilog",
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-o", "Blink.vvp", "Verilog_Blink.v", "Verilog_Blink_tb.v")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdCompile);
            Assert.True(s1);
            Assert.True(File.Exists(Path.Combine(tempDir, "Blink.vvp")));

            // Step 2: Simulate/Execute
            var cmdExec = new ToolCommand
            {
                Executable = "iverilog/vvp", // force read-write mount via path trick
                ToolName = "iverilog",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("Blink.vvp")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdExec);
            Assert.True(s2);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F8_Blinky_Formal_Verification_Workload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "F8_BlinkyFormal_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "Formal_Verification"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_sby", "hdlc/formal:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            var command = new ToolCommand
            {
                Executable = "yosys/sby", // force read-write mount via path trick
                ToolName = "sby",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-f", "Blink.sby")
            };
            var (success, _) = await strategy.ExecuteAsync(command);
            Assert.True(success);
            Assert.True(Directory.Exists(Path.Combine(tempDir, "Blink")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F8_Blinky_Physical_iCE40_Flow_Workload()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var tempDir = Path.Combine(Path.GetTempPath(), "F8_BlinkyiCE40_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "iCE40_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_yosys", "fentwums/oss-cad-suite:latest");
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ice40", "fentwums/oss-cad-suite:latest");
            provider.SettingsService.SetSettingValue("ContainerImage_icepack", "fentwums/oss-cad-suite:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Step 1: Synthesize design to json
            var cmdYosys = new ToolCommand
            {
                Executable = "yosys",
                ToolName = "yosys",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-p", "synth_ice40 -json ice40_blink.json", "ice40_blink.v")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdYosys);
            Assert.True(s1);

            // Step 2: Route using nextpnr-ice40
            var cmdPnr = new ToolCommand
            {
                Executable = "nextpnr-ice40",
                ToolName = "nextpnr-ice40",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ice40_blink.json", "--pcf", "ice40_blink.pcf", "--asc", "out.asc")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdPnr);
            Assert.True(s2);

            // Step 3: Pack bitstream using icepack
            var cmdPack = new ToolCommand
            {
                Executable = "icepack",
                ToolName = "icepack",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("out.asc", "out.bin")
            };
            var (s3, _) = await strategy.ExecuteAsync(cmdPack);
            Assert.True(s3);
            Assert.True(File.Exists(Path.Combine(tempDir, "out.bin")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [FactIfNoCI]
    public async Task F8_Blinky_Physical_ECP5_Flow_Workload()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        var tempDir = Path.Combine(Path.GetTempPath(), "F8_BlinkyECP5_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_localTestsDir, "ECP5_Flow"), tempDir);
        try
        {
            using var provider = new E2ETestServiceProvider();
            provider.SettingsService.SetSettingValue("ContainerImage_yosys", "fentwums/oss-cad-suite:latest");
            provider.SettingsService.SetSettingValue("ContainerImage_nextpnr-ecp5", "fentwums/oss-cad-suite:latest");
            provider.SettingsService.SetSettingValue("ContainerImage_ecppack", "fentwums/oss-cad-suite:latest");
            using var strategy = new DockerExecutionStrategy(provider);

            // Step 1: Synthesize design to json
            var cmdYosys = new ToolCommand
            {
                Executable = "yosys",
                ToolName = "yosys",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("-p", "synth_ecp5 -json ecp5_blink.json", "ecp5_blink.v")
            };
            var (s1, _) = await strategy.ExecuteAsync(cmdYosys);
            Assert.True(s1);

            // Step 2: Route using nextpnr-ecp5
            var cmdPnr = new ToolCommand
            {
                Executable = "nextpnr-ecp5",
                ToolName = "nextpnr-ecp5",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("--json", "ecp5_blink.json", "--lpf", "ecp5_blink.lpf", "--textcfg", "out.config")
            };
            var (s2, _) = await strategy.ExecuteAsync(cmdPnr);
            Assert.True(s2);

            // Step 3: Pack bitstream using ecppack
            var cmdPack = new ToolCommand
            {
                Executable = "ecppack",
                ToolName = "ecppack",
                WorkingDirectory = tempDir,
                CommandArguments = BuildArgs("out.config", "out.bit")
            };
            var (s3, _) = await strategy.ExecuteAsync(cmdPack);
            Assert.True(s3);
            Assert.True(File.Exists(Path.Combine(tempDir, "out.bit")));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }
}

// =======================================================================
//  E2E TEST SUPPORTING CLASSES
// =======================================================================

internal sealed class E2EMockSettingsService : ISettingsService
{
    private readonly Dictionary<string, object> _settings = new(StringComparer.Ordinal)
    {
        [ContainerExtensionModule.AutoRemoveSetting] = true,
        [ContainerExtensionModule.DefaultImageSetting] = ContainerExtensionModule.FallbackImage,
        [ContainerExtensionModule.DockerRuntimePathSetting] = "docker",
        [ContainerExtensionModule.MemoryLimitSetting] = 0.0,
        [ContainerExtensionModule.CpuLimitSetting] = 0.0,
        [ContainerExtensionModule.DaemonSocketSetting] = "",
        [ContainerExtensionModule.PlatformSetting] = "auto",
        [ContainerExtensionModule.TimeoutSetting] = 0.0,
        [ContainerExtensionModule.NetworkModeSetting] = "bridge",
        [ContainerExtensionModule.LogLevelSetting] = "Verbose",
        [ContainerExtensionModule.ShowTimestampsSetting] = true,
        [ContainerExtensionModule.PullPolicySetting] = "if-not-present",
        [ContainerExtensionModule.ExtraFlagsSetting] = "",
        [ContainerExtensionModule.DashboardRefreshSetting] = "Manual",
        [ContainerExtensionModule.ContainerNamePrefixSetting] = "containerextension-",
        [ContainerExtensionModule.TelemetryRetentionSetting] = "100",
        [ContainerExtensionModule.AllowNativeFallbackSetting] = false
    };

    public event EventHandler<SaveEventArgs>? Saved = delegate { };

    public bool HasSetting(string key) => _settings.ContainsKey(key);

    public T GetSettingValue<T>(string key)
    {
        if (_settings.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }
        return default!;
    }

    public void SetSettingValue(string key, object value)
    {
        _settings[key] = value;
    }

    public void RegisterSettingCategory(string category, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory) { }
    public void Register<T>(string key, T setting) { }
    public IObservable<T> Bind<T>(string key, IObservable<T> observable) => observable;
    public void RegisterTitled<T>(string category, string subCategory, string key, string title, string description, T defaultValue) { }
    public void RegisterTitledFolderPath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator) { }
    public void RegisterTitledFilePath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator, params Avalonia.Platform.Storage.FilePickerFileType[] fileTypes) { }
    public void RegisterTitledSlider(string category, string subCategory, string key, string title, string description, double defaultValue, double min, double max, double tick) { }
    public void RegisterTitledCombo<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledComboSearch<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledListBox(string category, string subCategory, string key, string title, string description, params string[] options) { }
    public void RegisterSetting(string category, string subCategory, string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterSetting(string category, string subCategory, string key, object settingModule) { }
    public void UpdateSetting(string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterCustom(string category, string subCategory, string key, OneWare.Essentials.Models.CustomSetting setting) { }
    public OneWare.Essentials.Models.Setting GetSetting(string key) => null!;
    public T[] GetComboOptions<T>(string key) => Array.Empty<T>();
    public IObservable<T> GetSettingObservable<T>(string key) => System.Reactive.Linq.Observable.Empty<T>();
    public void Load(string path) { }
    public void Save(string path, bool overrideExisting) { }
    public void WhenLoaded(Action action) { }
    public void Reset(string key) { }
    public void ResetAll() { }
}

internal sealed class E2ETestCommandArgument : ICommandArgument
{
    private readonly string _argument;
    public E2ETestCommandArgument(string argument)
    {
        _argument = argument;
    }
    public void Prepare(System.Runtime.InteropServices.OSPlatform osPlatform, Func<string, string>? pathMapper = null) { }
    public string GetArgument() => _argument;
}

internal sealed class E2ETestServiceProvider : IServiceProvider, IDisposable
{
    public E2EMockSettingsService SettingsService { get; } = new();

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ISettingsService))
        {
            return SettingsService;
        }
        return null;
    }

    public void Dispose() { }
}

#pragma warning disable CA1515, xUnit3003
public sealed class FactIfNoCI : FactAttribute
{
    public FactIfNoCI()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            Skip = "Skipped in GitHub Actions to prevent Docker Hub rate limits and image pulling flakiness.";
        }
    }
}
#pragma warning restore CA1515, xUnit3003
