/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// The legacy wear note. On a flag-on region a legacy <c>AgentIsNowWearing</c> packet notes the agent on the
/// region state; the change bake that follows takes the note once and re-reads the Current Outfit folder before
/// it composites, so the packet's one-item-or-null merge cannot stand in for the folder on a region that bakes
/// server-side. The note and the take are tested here; the subscription, the take before the bake, the read and
/// the save are read against the source in <see cref="ChangeTriggerTests.TheShippedWiringMatchesWhatIsTestedHere"/>,
/// as the change trigger's wiring is (there is no Scene or ScenePresence harness in this project).
/// </summary>
public class LegacyWearNoteTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    private static ServerSideBakingRegion On() => new(true, new CofHandshake());
    private static ServerSideBakingRegion Off() => new(false, new CofHandshake());

    [Fact]
    public void ANotedAgentIsTakenOnceAndNotTwice()
    {
        var region = On();

        region.NoteLegacyWear(Agent);

        Assert.True(region.TakeLegacyWear(Agent));
        Assert.False(region.TakeLegacyWear(Agent));
    }

    [Fact]
    public void AnUnnotedAgentIsNotTaken()
    {
        var region = On();

        Assert.False(region.TakeLegacyWear(Agent));
    }

    /// <summary>
    /// The 2026-10-28 shape: three packets within one millisecond, then one change bake. However many packets
    /// arrive before the bake, the folder is read once.
    /// </summary>
    [Fact]
    public void SeveralPacketsBeforeOneBakeAreOneNote()
    {
        var region = On();

        region.NoteLegacyWear(Agent);
        region.NoteLegacyWear(Agent);
        region.NoteLegacyWear(Agent);

        Assert.True(region.TakeLegacyWear(Agent));
        Assert.False(region.TakeLegacyWear(Agent));
    }

    /// <summary>A packet that arrives after a bake took the note is taken by the next bake.</summary>
    [Fact]
    public void APacketAfterTheTakeIsNotedAgain()
    {
        var region = On();

        region.NoteLegacyWear(Agent);
        Assert.True(region.TakeLegacyWear(Agent));

        region.NoteLegacyWear(Agent);
        Assert.True(region.TakeLegacyWear(Agent));
        Assert.False(region.TakeLegacyWear(Agent));
    }

    [Fact]
    public void TheNoteIsPerAgent()
    {
        var region = On();
        var other = UUID.Random();

        region.NoteLegacyWear(Agent);

        Assert.False(region.TakeLegacyWear(other));
        Assert.True(region.TakeLegacyWear(Agent));
    }

    [Fact]
    public void ForgetClearsTheNote()
    {
        var region = On();

        region.NoteLegacyWear(Agent);
        region.Forget(Agent);

        Assert.False(region.TakeLegacyWear(Agent));
    }

    // ------------------------------------------------------------------ flag-off regions

    /// <summary>
    /// The module does not subscribe the packet on a flag-off region (read against the source in the wiring
    /// test); should anything note there regardless, nothing is noted, so no bake there can read the folder
    /// on this account. The wire is unchanged where the flag is off (ADR-001).
    /// </summary>
    [Fact]
    public void AFlagOffRegionNotesNothing()
    {
        var region = Off();

        region.NoteLegacyWear(Agent);

        Assert.False(region.TakeLegacyWear(Agent));
        Assert.False(region.ServerSideBakingEnabled);
    }
}
