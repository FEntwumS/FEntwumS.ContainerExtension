using System;
using System.Diagnostics.CodeAnalysis;
using OneWare.Essentials.Services;

namespace ContainerExtension;

internal static class SettingsExtensions
{
    /// <summary>
    /// Safely reads a setting value, returning <paramref name="fallback"/> if the key
    /// is unregistered, missing, or throws an exception during resolution.
    /// </summary>
    public static T SafeGetSetting<T>(this ISettingsService? settingsService, string key, T fallback)
    {
        if (settingsService == null)
        {
            return fallback;
        }
        try
        {
            if (settingsService.HasSetting(key))
            {
                var value = settingsService.GetSettingValue<T>(key);
                return value is null ? fallback : value;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("SettingsExtensions", $"Setting '{key}' read failed", ex);
        }
        return fallback;
    }

    /// <summary>
    /// Safely truncates a container or image ID to 12 characters without throwing
    /// ArgumentOutOfRange exceptions if the string is malformed or shorter than expected.
    /// </summary>
    [return: NotNullIfNotNull(nameof(id))]
    public static string? ShortId(this string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return id;
        }
        var span = id.AsSpan().Trim();
        bool changed = span.Length != id.Length;
        if (span.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            span = span.Slice(7);
            changed = true;
        }
        if (span.Length > 12)
        {
            span = span.Slice(0, 12);
            changed = true;
        }
        return changed ? span.ToString() : id;
    }
}

internal sealed class EmptyProgress<T> : IProgress<T>
{
    public static readonly EmptyProgress<T> Instance = new();
    public void Report(T value) { }
}
