using System.Security.Principal;
using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>
/// Run only by the standard-user job of the workflow (and by anybody who runs the tests from a normal prompt). If the job's account were
/// an administrator by mistake, this is the test that would say so, instead of the Needs=NonAdmin tests passing for the wrong reason.
/// </summary>
[Trait("Needs", "NonAdmin")]
public class StandardUserCanary
{
    [Fact]
    public void ThisRunIsAnOrdinaryNonElevatedAccount()
    {
        Assert.False(Elevation.IsTokenElevated);
        Assert.False(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
    }
}
