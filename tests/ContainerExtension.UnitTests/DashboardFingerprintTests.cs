using System.Collections.Generic;
using ContainerExtension.Views;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression coverage for the dashboard skip-if-unchanged fingerprint. The prior implementation
/// hashed only the first five containers (<c>Take(5)</c>), so a count-stable state change in any
/// later container left the fingerprint unchanged and the dashboard failed to repaint.
/// </summary>
public sealed class DashboardFingerprintTests
{
    private static List<ContainerListResponse> Make(int n)
    {
        var list = new List<ContainerListResponse>(n);
        for (var i = 0; i < n; i++)
        {
            list.Add(new ContainerListResponse { ID = $"id{i}", State = "running" });
        }
        return list;
    }

    [Fact]
    public void ForContainers_IdenticalState_ProducesEqualFingerprint()
    {
        Assert.Equal(DashboardFingerprint.ForContainers(Make(8)), DashboardFingerprint.ForContainers(Make(8)));
    }

    [Fact]
    public void ForContainers_StateChangeBeyondFifthContainer_ChangesFingerprint()
    {
        var baseline = Make(8);
        var changed = Make(8);
        changed[5].State = "exited"; // the sixth container — ignored by the old Take(5) prefix
        Assert.NotEqual(DashboardFingerprint.ForContainers(baseline), DashboardFingerprint.ForContainers(changed));
    }

    [Fact]
    public void ForContainers_CountChange_ChangesFingerprint()
    {
        Assert.NotEqual(DashboardFingerprint.ForContainers(Make(8)), DashboardFingerprint.ForContainers(Make(9)));
    }
}
