using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using ContainerExtension.Validations;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Daemon-free coverage for the SSRF/identifier guards, release-tag parsing, disk-usage accounting,
/// settings resolution, and bind/image validation reached through InternalsVisibleTo and reflection.
/// </summary>
public sealed class SecurityAndParsingTests
{
    private const BindingFlags StaticNonPublic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags InstanceNonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly Type RegistryClientType =
        typeof(ContainerExtension.Registry.RegistryClient);

    private static MethodInfo RegistryMethod(string name)
    {
        var m = RegistryClientType.GetMethod(name, StaticNonPublic);
        Assert.NotNull(m);
        return m!;
    }

    // -- RegistryClient.IsLoopbackRegistry -------------------------------

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:5000", true)]   // Port is stripped before the check.
    // A bare "::1" is truncated at its leading colon by the host:port split (the literal "::1" arm is
    // therefore only reachable for inputs that never contain a colon), so it is not classified loopback.
    [InlineData("::1", false)]
    [InlineData("127.0.0.1.evil.com", false)] // Documented prefix-bypass guard.
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsLoopbackRegistry_ClassifiesHostsCorrectly(string host, bool expected)
    {
        var result = (bool)RegistryMethod("IsLoopbackRegistry").Invoke(null, new object[] { host })!;
        Assert.Equal(expected, result);
    }

    // -- RegistryClient.IsDisallowedAddress ------------------------------

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.0.1", false)]      // Just below the RFC1918 /12 block.
    [InlineData("172.32.0.1", false)]      // Just above the RFC1918 /12 block.
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.10.20", true)]    // Link-local.
    [InlineData("100.64.0.1", true)]       // CGNAT.
    [InlineData("100.127.255.255", true)]  // CGNAT upper bound.
    [InlineData("100.63.0.1", false)]      // Just below CGNAT.
    [InlineData("100.128.0.1", false)]     // Just above CGNAT.
    [InlineData("127.0.0.1", true)]        // Loopback.
    [InlineData("8.8.8.8", false)]         // Public.
    [InlineData("fe80::1", true)]          // IPv6 link-local.
    [InlineData("fc00::1", true)]          // IPv6 unique-local.
    [InlineData("2606:4700:4700::1111", false)] // Public IPv6.
    public void IsDisallowedAddress_FlagsInternalRanges(string address, bool expected)
    {
        var ip = IPAddress.Parse(address);
        var result = (bool)RegistryMethod("IsDisallowedAddress").Invoke(null, new object[] { ip })!;
        Assert.Equal(expected, result);
    }

    // -- RegistryClient.IsValidRegistryIdentifier ------------------------

    [Theory]
    [InlineData("", true)]                 // Empty is permitted per contract.
    [InlineData("ns/repo", true)]
    [InlineData("library", true)]
    [InlineData("My.Repo-1_0", true)]
    [InlineData("a/b/c", true)]
    [InlineData("ns//repo", false)]        // Empty segment.
    [InlineData("./repo", false)]          // "." segment.
    [InlineData("../repo", false)]         // ".." segment.
    [InlineData("ns/repo:tag", false)]     // ':' is outside the allowed set.
    [InlineData("ns repo", false)]         // Whitespace is outside the allowed set.
    [InlineData("ns@repo", false)]
    public void IsValidRegistryIdentifier_EnforcesSegmentAndCharsetRules(string input, bool expected)
    {
        var result = (bool)RegistryMethod("IsValidRegistryIdentifier").Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValidRegistryIdentifier_RejectsOver255Characters()
    {
        var tooLong = new string('a', 256);
        var result = (bool)RegistryMethod("IsValidRegistryIdentifier").Invoke(null, new object[] { tooLong })!;
        Assert.False(result);
    }

    // -- GitHubReleaseClient.IsValidReleaseTag ---------------------------

    [Theory]
    [InlineData("2025-06-27", true)]
    [InlineData("2024-13-45", false)]      // Structurally valid but impossible date.
    [InlineData("2025-02-30", false)]      // Impossible day.
    [InlineData("2025-6-27", false)]       // Wrong length.
    [InlineData("2025-06-27-", false)]     // Wrong length.
    [InlineData("2025/06/27", false)]      // Missing dashes at the separator positions.
    [InlineData("20a5-06-27", false)]      // Non-digit in a digit position.
    [InlineData("latest", false)]
    public void IsValidReleaseTag_ValidatesDateShape(string tag, bool expected)
    {
        var type = typeof(ContainerExtensionModule).Assembly.GetType("ContainerExtension.Services.GitHubReleaseClient");
        Assert.NotNull(type);
        var method = type!.GetMethod("IsValidReleaseTag", StaticNonPublic);
        Assert.NotNull(method);
        var result = (bool)method!.Invoke(null, new object[] { tag })!;
        Assert.Equal(expected, result);
    }

    // -- DockerImageManager.ComputeDiskUsage -----------------------------

    private static ImagesListResponse Image(string id, long size, params string[] repoTags) =>
        new() { ID = id, Size = size, RepoTags = new List<string>(repoTags) };

    [Fact]
    public void ComputeDiskUsage_NullOrEmpty_ReturnsZeros()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal((0, 0L, 0L), DockerImageManager.ComputeDiskUsage(null, ct));
        Assert.Equal((0, 0L, 0L), DockerImageManager.ComputeDiskUsage(Array.Empty<ImagesListResponse>(), ct));
    }

    [Fact]
    public void ComputeDiskUsage_SingleTagged_NotReclaimable()
    {
        var result = DockerImageManager.ComputeDiskUsage(
            new[] { Image("sha256:a", 1000, "repo:tag") }, TestContext.Current.CancellationToken);
        Assert.Equal((1, 1000L, 0L), result);
    }

    [Fact]
    public void ComputeDiskUsage_SingleUntagged_IsReclaimable()
    {
        var result = DockerImageManager.ComputeDiskUsage(
            new[] { Image("sha256:a", 1000) }, TestContext.Current.CancellationToken);
        Assert.Equal((1, 1000L, 1000L), result);
    }

    [Fact]
    public void ComputeDiskUsage_DedupesById_SumsAndCountsReclaimable()
    {
        var images = new[]
        {
            Image("sha256:a", 1000, "repo:tag"),
            Image("sha256:a", 1000, "repo:tag"),   // Duplicate ID, counted once.
            Image("sha256:b", 500, "<none>:<none>"), // Dangling, reclaimable.
            Image("sha256:c", 250),                  // No RepoTags, reclaimable.
        };

        var (count, total, reclaimable) = DockerImageManager.ComputeDiskUsage(images, TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal(1750L, total);
        Assert.Equal(750L, reclaimable);
    }

    // -- SettingsExtensions.SafeGetSetting -------------------------------

    [Fact]
    public void SafeGetSetting_NullService_ReturnsFallback()
    {
        ISettingsService? svc = null;
        Assert.Equal("fb", svc.SafeGetSetting("key", "fb"));
    }

    [Fact]
    public void SafeGetSetting_MissingKey_ReturnsFallback()
    {
        var svc = new MockSettingsService();
        Assert.Equal("fb", svc.SafeGetSetting("ContainerExtension_NoSuchKey", "fb"));
    }

    [Fact]
    public void SafeGetSetting_ThrowingService_ReturnsFallbackWithoutEscaping()
    {
        var svc = new ThrowingSettingsService();
        var ex = Record.Exception(() => svc.SafeGetSetting("any", "fb"));
        Assert.Null(ex);
        Assert.Equal("fb", svc.SafeGetSetting("any", "fb"));
    }

    // -- ResourceThresholdValidation advisory branch ---------------------

    [Fact]
    public void ResourceThreshold_BetweenThresholdAndTotal_PassesWithAdvisory()
    {
        // "GPU" yields the Custom resource kind, sidestepping the memory/CPU floors so the advisory
        // branch (value above threshold, at or below total) is exercised in isolation.
        var validator = new ResourceThresholdValidation(threshold: 100.0, total: 200.0, "GPU");

        var advisoryAccepted = validator.Validate(150.0, out var advisoryWarning);
        Assert.True(advisoryAccepted);
        Assert.NotNull(advisoryWarning);

        var rejected = validator.Validate(250.0, out var rejectWarning);
        Assert.False(rejected);
        Assert.NotNull(rejectWarning);

        // The advisory and the over-capacity rejection must surface distinct messages.
        Assert.NotEqual(advisoryWarning, rejectWarning);
    }

    // -- DockerContainerManager.ValidateContainerName --------------------

    private static Exception? InvokeValidateContainerName(string? name)
    {
        var method = typeof(DockerContainerManager).GetMethod("ValidateContainerName", StaticNonPublic);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(null, new object?[] { name });
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException;
        }
    }

    [Theory]
    [InlineData("safe-container_name.1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateContainerName_AcceptsSafeOrBlankNames(string? name)
    {
        Assert.Null(InvokeValidateContainerName(name));
    }

    [Theory]
    [InlineData("name;rm")]
    [InlineData("name&whoami")]
    [InlineData("name|cat")]
    [InlineData("name$VAR")]
    [InlineData("name`id`")]
    [InlineData("nameé")]   // Non-ASCII.
    [InlineData("name\nrest")]   // Control character.
    public void ValidateContainerName_RejectsShellAndNonAsciiChars(string name)
    {
        var ex = InvokeValidateContainerName(name);
        Assert.IsType<ArgumentException>(ex);
    }

    // -- DockerExecutionStrategy.ResolveImage ----------------------------

    private const string EnvImageKey = "ONEWARE_DOCKER_IMAGE";

    private static string InvokeResolveImage(DockerExecutionStrategy strategy, string toolName)
    {
        var method = typeof(DockerExecutionStrategy).GetMethod("ResolveImage", InstanceNonPublic);
        Assert.NotNull(method);
        return (string)method!.Invoke(strategy, new object[] { toolName })!;
    }

    [Fact]
    public void ResolveImage_EnvironmentVariable_TakesPrecedenceAndTrimsCarriageReturn()
    {
        var original = Environment.GetEnvironmentVariable(EnvImageKey);
        try
        {
            using var provider = new TestServiceProvider();
            var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
            settings.SetSettingValue("ContainerImage_ghdl", "from/setting:tag");
            settings.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "from/default:tag");
            using var strategy = new DockerExecutionStrategy(provider);

            Environment.SetEnvironmentVariable(EnvImageKey, "env/image:tag\r");
            Assert.Equal("env/image:tag", InvokeResolveImage(strategy, "ghdl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvImageKey, original);
        }
    }

    [Fact]
    public void ResolveImage_PerToolSetting_OverridesDefaultSetting()
    {
        var original = Environment.GetEnvironmentVariable(EnvImageKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvImageKey, null);
            using var provider = new TestServiceProvider();
            var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
            settings.SetSettingValue("ContainerImage_ghdl", "pertool/image:tag");
            settings.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "from/default:tag");
            using var strategy = new DockerExecutionStrategy(provider);

            Assert.Equal("pertool/image:tag", InvokeResolveImage(strategy, "ghdl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvImageKey, original);
        }
    }

    [Fact]
    public void ResolveImage_DefaultSetting_UsedWhenNoEnvOrPerTool()
    {
        var original = Environment.GetEnvironmentVariable(EnvImageKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvImageKey, null);
            using var provider = new TestServiceProvider();
            var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
            settings.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "from/default:tag");
            using var strategy = new DockerExecutionStrategy(provider);

            Assert.Equal("from/default:tag", InvokeResolveImage(strategy, "ghdl"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvImageKey, original);
        }
    }

    [Fact]
    public void ResolveImage_DefaultToolImageMap_UsedWhenSettingsEmpty()
    {
        var original = Environment.GetEnvironmentVariable(EnvImageKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvImageKey, null);
            using var provider = new TestServiceProvider();
            var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
            settings.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "");
            using var strategy = new DockerExecutionStrategy(provider);

            // "nvc" maps to a value distinct from FallbackImage, isolating the map branch.
            Assert.Equal(ContainerExtensionModule.DefaultToolImages["nvc"], InvokeResolveImage(strategy, "nvc"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvImageKey, original);
        }
    }

    [Fact]
    public void ResolveImage_FallbackImage_UsedWhenNothingResolves()
    {
        var original = Environment.GetEnvironmentVariable(EnvImageKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvImageKey, null);
            using var provider = new TestServiceProvider();
            var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
            settings.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "");
            using var strategy = new DockerExecutionStrategy(provider);

            Assert.Equal(ContainerExtensionModule.FallbackImage, InvokeResolveImage(strategy, "unknown-tool"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvImageKey, original);
        }
    }

    // -- DockerExecutionStrategy.ValidateBinds ---------------------------

    private static Exception? InvokeValidateBinds(IList<string>? binds)
    {
        var method = typeof(DockerExecutionStrategy).GetMethod("ValidateBinds", StaticNonPublic);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(null, new object?[] { binds });
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException;
        }
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/proc")]
    [InlineData("/sys")]
    public void ValidateBinds_RejectsCriticalHostMounts(string hostPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The blocked-path set differs on Windows; these POSIX roots do not apply.
        }
        var binds = new List<string> { $"{hostPath}:/workspace:ro" };
        var ex = InvokeValidateBinds(binds);
        Assert.IsType<DockerExecutionException>(ex);
    }

    [Fact]
    public void ValidateBinds_RewritesBenignBindToCanonicalForm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BindTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var canonicalMethod = typeof(DockerExecutionStrategy).GetMethod("GetCanonicalPath", StaticNonPublic);
            Assert.NotNull(canonicalMethod);
            var canonical = (string)canonicalMethod!.Invoke(null, new object[] { tempDir })!;

            var binds = new List<string> { $"{tempDir}:/workspace:rw" };
            var ex = InvokeValidateBinds(binds);
            Assert.Null(ex);
            Assert.Equal($"{canonical}:/workspace:rw", binds[0]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort teardown */ }
        }
    }

    [Fact]
    public void ValidateBinds_NullList_NoOp()
    {
        Assert.Null(InvokeValidateBinds(null));
    }
}

// ISettingsService stub whose value lookups throw, exercising the SafeGetSetting failure path.
// All non-essential members delegate to a real MockSettingsService to keep the surface minimal.
#pragma warning disable CA1812
internal sealed class ThrowingSettingsService : ISettingsService
{
    private readonly MockSettingsService _inner = new();

    public event EventHandler<SaveEventArgs>? Saved
    {
        add { _inner.Saved += value; }
        remove { _inner.Saved -= value; }
    }

    public bool HasSetting(string key) => true;
    public T GetSettingValue<T>(string key) => throw new InvalidOperationException("setting resolution failed");
    public void SetSettingValue(string key, object value) => _inner.SetSettingValue(key, value);

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
#pragma warning restore CA1812
