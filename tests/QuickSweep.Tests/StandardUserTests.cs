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

/// <summary>The state-folder check sees entries that children inherit (stored with generic rights), not only entries on the folder itself.</summary>
public class InheritedEntryTests
{
    static List<string> Flagged(string sddl)
    {
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(sddl);
        return StateFolder.PermissiveEntries(sec);
    }

    [Theory]
    [InlineData("D:(A;OICIIO;GA;;;BU)")]            // users: everything, for new children
    [InlineData("D:(A;OICIIO;GW;;;BU)")]
    [InlineData("D:(A;OICIIO;GA;;;AU)")]
    [InlineData("D:(A;OICI;0x1301bf;;;BU)")]
    public void AnEntryThatLetsUsersWriteIsFlaggedWhetherOrNotItAppliesToTheFolderItself(string sddl) => Assert.NotEmpty(Flagged(sddl));

    [Theory]
    [InlineData("D:(A;OICI;0x1200a9;;;BU)")]
    [InlineData("D:(A;OICIIO;GRGX;;;BU)")]
    [InlineData("D:(A;;FA;;;SY)(A;OICIIO;GA;;;SY)(A;;FA;;;BA)(A;OICIIO;GA;;;BA)")]
    public void ReadOnlyEntriesAndAdministrativeAccountsAreNot(string sddl) => Assert.Empty(Flagged(sddl));
}
