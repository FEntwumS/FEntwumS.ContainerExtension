using Docker.DotNet.Models;

namespace ContainerExtension.Views;

/// <summary>
/// Pure change-detection fingerprints for the diagnostics dashboard's skip-if-unchanged guard.
/// Kept free of any Avalonia dependency so the logic is unit-testable in isolation from the UI.
/// </summary>
internal static class DashboardFingerprint
{
    /// <summary>
    /// Fingerprints the full container set (count plus each container's id and state). Hashing every
    /// element rather than a prefix ensures a count-stable state transition in any container — for
    /// example the sixth container exiting while a seventh starts — still changes the fingerprint and
    /// forces a repaint. The cost is negligible at the 250-container list cap.
    /// </summary>
    internal static int ForContainers(IList<ContainerListResponse> containers)
    {
        var fp = containers.Count;
        foreach (var c in containers)
        {
            fp = HashCode.Combine(fp, c.ID, c.State);
        }
        return fp;
    }
}
