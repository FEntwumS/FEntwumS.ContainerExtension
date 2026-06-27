#pragma warning disable IDE0031
#pragma warning disable CA1416

using System;
using System.IO;
using System.Reflection;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

public sealed class HardeningChallengeTests
{
    private static string InvokeGetCanonicalPath(string path)
    {
        var method = typeof(DockerExecutionStrategy).GetMethod("GetCanonicalPath", BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) throw new InvalidOperationException("GetCanonicalPath method not found");
        return (string)method.Invoke(null, new object[] { path })!;
    }

    [Fact]
    public void GetCanonicalPath_NestedSymlinks_ResolvesCorrectly()
    {
        if (OperatingSystem.IsWindows()) return; // Symlinks require admin privileges on Windows by default

        var tempDir = Path.Combine(Path.GetTempPath(), "Challenge_Nested_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var targetDir = Path.Combine(tempDir, "target");
            Directory.CreateDirectory(targetDir);

            var linkB = Path.Combine(tempDir, "linkB");
            var linkA = Path.Combine(tempDir, "linkA");

            File.CreateSymbolicLink(linkB, targetDir);
            File.CreateSymbolicLink(linkA, linkB);

            var resolved = InvokeGetCanonicalPath(linkA);
            var expected = InvokeGetCanonicalPath(targetDir);

            Assert.Equal(expected, resolved);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetCanonicalPath_CircularSymlink_Deep_ThrowsDockerExecutionException()
    {
        if (OperatingSystem.IsWindows()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "Challenge_CircularDeep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var linkA = Path.Combine(tempDir, "linkA");
            var linkB = Path.Combine(tempDir, "linkB");
            var linkC = Path.Combine(tempDir, "linkC");

            File.CreateSymbolicLink(linkA, linkB);
            File.CreateSymbolicLink(linkB, linkC);
            File.CreateSymbolicLink(linkC, linkA);

            var ex = Assert.Throws<TargetInvocationException>(() => InvokeGetCanonicalPath(linkA));
            Assert.IsType<DockerExecutionException>(ex.InnerException);
            Assert.Contains("Circular", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetCanonicalPath_NonexistentDirectoryWithTraversal_ResolvesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Challenge_Nonexistent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Non-existent target inside tempDir
            var path = Path.Combine(tempDir, "nonexistent", "..", "another_nonexistent");
            var resolved = InvokeGetCanonicalPath(path);
            var expected = InvokeGetCanonicalPath(Path.Combine(tempDir, "another_nonexistent"));

            Assert.Equal(expected, resolved);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task NamedPipe_ImpersonationLevel_VerifiedAsIdentification()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = "ChallengePipe_" + Guid.NewGuid().ToString("N");
        var serverTask = Task.Run(() =>
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
            server.WaitForConnection();

            TokenImpersonationLevel? level = null;
            WindowsIdentity? identity = null;

            var impersonateMethod = typeof(NamedPipeServerStream).GetMethod("Impersonate", new Type[] { typeof(Action) });
            if (impersonateMethod != null)
            {
                impersonateMethod.Invoke(server, new object[] { (Action)(() =>
                {
                    identity = WindowsIdentity.GetCurrent();
                    var levelProp = typeof(WindowsIdentity).GetProperty("ImpersonationLevel");
                    if (levelProp != null)
                    {
                        level = (TokenImpersonationLevel)levelProp.GetValue(identity)!;
                    }
                }) });
            }

            return new Tuple<TokenImpersonationLevel?, string?>(level, identity?.Name);
        });

        // Use the client stream using the same parameters as SecureStreamOpenerAsync
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        var result = await serverTask;
        var impersonationLevel = result.Item1;

        Assert.NotNull(impersonationLevel);
        // Identification level denies the server the ability to impersonate the client.
        Assert.Equal(TokenImpersonationLevel.Identification, impersonationLevel);
    }
}
