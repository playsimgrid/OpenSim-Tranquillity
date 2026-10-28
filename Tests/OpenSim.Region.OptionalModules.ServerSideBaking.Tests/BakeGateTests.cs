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
/// The per-agent bake gate. <c>BakeOrchestrator.Run</c> reads the bake index at the start of a run and writes it
/// at the end, so two bakes for one agent running side by side on a region make the second compare its hashes
/// with the index the first is about to replace: every channel composites again and the first bake's assets are
/// named by nothing. <c>BakeAsync</c> waits on the agent's gate before it reads the appearance and releases it in
/// a <c>finally</c>, so a bake that arrives during another starts when that one has written its index. The gate
/// itself is tested here; that the wait precedes the orchestrator and the release is in a <c>finally</c> is read
/// against the source in <see cref="ChangeTriggerTests.TheShippedWiringMatchesWhatIsTestedHere"/>, as the change
/// trigger's wiring is (there is no Scene or ScenePresence harness in this project).
/// </summary>
public class BakeGateTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private static ServerSideBakingRegion On() => new(true, new CofHandshake());
    private static ServerSideBakingRegion Off() => new(false, new CofHandshake());

    /// <summary>
    /// The 2026-10-28 shape: the catch-up save's bake arrives five seconds into a sixteen-second composite for
    /// the same agent. It does not start until the first bake releases, and then it does.
    /// </summary>
    [Fact]
    public async Task TheSameAgentsSecondBakeWaitsForTheFirstToRelease()
    {
        var region = On();
        var first = region.BakeGate(Agent);
        Assert.Same(first, region.BakeGate(Agent));

        await first.WaitAsync(Soon);
        Assert.Equal(0, first.CurrentCount);

        var second = region.BakeGate(Agent).WaitAsync(Soon);
        Assert.False(second.IsCompleted, "the second bake started while the first held the gate");

        first.Release();
        Assert.True(await second.WaitAsync(Soon), "the second bake did not start after the first released");
        Assert.Equal(0, first.CurrentCount);

        first.Release();
        Assert.Equal(1, first.CurrentCount);
    }

    [Fact]
    public async Task TwoAgentsDoNotWaitOnEachOther()
    {
        var region = On();
        var other = UUID.Random();

        await region.BakeGate(Agent).WaitAsync(Soon);

        var otherBake = region.BakeGate(other).WaitAsync(Soon);
        Assert.True(otherBake.IsCompletedSuccessfully, "another agent's bake waited on this agent's gate");
        Assert.True(await otherBake);
        Assert.NotSame(region.BakeGate(Agent), region.BakeGate(other));
    }

    /// <summary>
    /// The presence closes while its bake runs: <c>Forget</c> drops the gate so a returning agent gets a fresh one
    /// that is free at once, and the bake still holding the old gate releases it without an error. The old gate
    /// is never disposed, which is what makes that release safe.
    /// </summary>
    [Fact]
    public async Task ForgetHandsOutANewGateAndTheOldHolderStillReleases()
    {
        var region = On();
        var held = region.BakeGate(Agent);
        await held.WaitAsync(Soon);

        region.Forget(Agent);

        var fresh = region.BakeGate(Agent);
        Assert.NotSame(held, fresh);
        Assert.Equal(1, fresh.CurrentCount);
        Assert.True(fresh.WaitAsync(Soon).IsCompletedSuccessfully, "a returning agent's bake waited on the gate the closed one holds");

        Assert.Null(Record.Exception(() => held.Release()));
        Assert.Equal(1, held.CurrentCount);
    }

    // ------------------------------------------------------------------ flag-off regions

    /// <summary>
    /// The console command bakes on a flag-off region too (it writes faces and sends the appearance there without
    /// touching the wire), so the gate exists there as well: unlike the change-bake claim and the legacy wear
    /// note, it has no flag guard.
    /// </summary>
    [Fact]
    public async Task TheGateWorksOnAFlagOffRegion()
    {
        var region = Off();
        var gate = region.BakeGate(Agent);
        Assert.NotNull(gate);
        Assert.Equal(1, gate.CurrentCount);

        await gate.WaitAsync(Soon);
        var second = region.BakeGate(Agent).WaitAsync(Soon);
        Assert.False(second.IsCompleted, "the second bake started while the first held the gate on a flag-off region");

        gate.Release();
        Assert.True(await second.WaitAsync(Soon));
        Assert.False(region.ServerSideBakingEnabled);
    }
}
