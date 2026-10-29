/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory;

/// <summary>
/// AgentIsNowWearing must merge into the agent's existing wearables,
/// not rebuild them from an empty set. These tests drive the real event path through
/// <see cref="TestClient.TriggerAvatarNowWearing"/>.
/// </summary>
public class AvatarFactoryNowWearingTests : OpenSimTestCase
{
    private const int Shape = (int)WearableType.Shape;
    private const int Skin = (int)WearableType.Skin;
    private const int Hair = (int)WearableType.Hair;
    private const int Eyes = (int)WearableType.Eyes;
    private const int Universal = 16; // AvatarWearable.UNIVERSAL; the multi-layer type in the grid trace

    private static readonly UUID ShapeItem = TestHelpers.ParseTail(0x10);
    private static readonly UUID SkinItem = TestHelpers.ParseTail(0x11);
    private static readonly UUID HairItem = TestHelpers.ParseTail(0x12);
    private static readonly UUID EyesItem = TestHelpers.ParseTail(0x13);

    private static (AvatarFactoryModule afm, ScenePresence sp) SetupPresenceWearingBodyParts()
    {
        UUID userId = TestHelpers.ParseTail(0x1);

        TestsAssetCache assetCache = new TestsAssetCache();
        AvatarFactoryModule afm = new AvatarFactoryModule();
        TestScene scene = new SceneHelpers(assetCache).SetupScene();
        SceneHelpers.SetupSceneModules(scene, afm);
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, userId);

        AvatarWearable[] wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (int i = 0; i < wearables.Length; i++)
            wearables[i] = new AvatarWearable();
        wearables[Shape].Add(ShapeItem, TestHelpers.ParseTail(0x20));
        wearables[Skin].Add(SkinItem, TestHelpers.ParseTail(0x21));
        wearables[Hair].Add(HairItem, TestHelpers.ParseTail(0x22));
        wearables[Eyes].Add(EyesItem, TestHelpers.ParseTail(0x23));
        sp.Appearance.Wearables = wearables;

        return (afm, sp);
    }

    private static AvatarWearingArgs NowWearing(params (int type, UUID item)[] entries)
    {
        AvatarWearingArgs e = new AvatarWearingArgs();
        foreach ((int type, UUID item) in entries)
            e.NowWearing.Add(new AvatarWearingArgs.Wearable(item, (byte)type));
        return e;
    }

    private static UUID ItemIn(ScenePresence sp, int type)
        => sp.Appearance.Wearables[type].Count == 0 ? UUID.Zero : sp.Appearance.Wearables[type][0].ItemID;

    /// <summary>Body parts plus nine Universal layers (items 0x40.., assets 0x50..), as the grid avatar wears.</summary>
    private static (AvatarFactoryModule afm, ScenePresence sp) SetupPresenceWearingNineUniversals()
    {
        (AvatarFactoryModule afm, ScenePresence sp) = SetupPresenceWearingBodyParts();
        for (int n = 0; n < 9; n++)
            sp.Appearance.Wearables[Universal].Add(TestHelpers.ParseTail(0x40 + n), TestHelpers.ParseTail(0x50 + n));
        return (afm, sp);
    }

    /// <summary>(a) Partial list: slots the viewer did not mention are retained.</summary>
    [Fact]
    public void PartialNowWearing_RetainsUnlistedSlots()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingBodyParts();

        ((TestClient)sp.ControllingClient).TriggerAvatarNowWearing(
            NowWearing((Shape, ShapeItem), (Skin, SkinItem)));

        Assert.Equal(ShapeItem, ItemIn(sp, Shape));
        Assert.Equal(SkinItem, ItemIn(sp, Skin));
        Assert.Equal(HairItem, ItemIn(sp, Hair));
        Assert.Equal(EyesItem, ItemIn(sp, Eyes));
    }

    /// <summary>(b) A listed slot with a new item id updates; everything else is retained.</summary>
    [Fact]
    public void NewItemInListedSlot_UpdatesThatSlotOnly()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingBodyParts();
        UUID newShape = TestHelpers.ParseTail(0x30);

        ((TestClient)sp.ControllingClient).TriggerAvatarNowWearing(
            NowWearing((Shape, newShape)));

        Assert.Equal(newShape, ItemIn(sp, Shape));
        Assert.Equal(1, sp.Appearance.Wearables[Shape].Count);
        Assert.Equal(SkinItem, ItemIn(sp, Skin));
        Assert.Equal(HairItem, ItemIn(sp, Hair));
        Assert.Equal(EyesItem, ItemIn(sp, Eyes));
    }

    /// <summary>
    /// (c) Pre-fix semantics for an explicit UUID.Zero entry: the old code cleared every slot and then
    /// called <see cref="AvatarWearable.Add"/>, which ignores Zero, so a slot listed with Zero ended up
    /// empty. That is preserved: "type X, item Zero" clears slot X. Unlisted slots are still retained.
    /// </summary>
    [Fact]
    public void ZeroItemInListedSlot_ClearsThatSlot_AsPreFixCodeDid()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingBodyParts();

        ((TestClient)sp.ControllingClient).TriggerAvatarNowWearing(
            NowWearing((Hair, UUID.Zero)));

        Assert.Equal(0, sp.Appearance.Wearables[Hair].Count);
        Assert.Equal(ShapeItem, ItemIn(sp, Shape));
        Assert.Equal(SkinItem, ItemIn(sp, Skin));
        Assert.Equal(EyesItem, ItemIn(sp, Eyes));
    }

    /// <summary>Unchanged list reports no change, so no avatar-service write is queued.</summary>
    [Fact]
    public void MergeNowWearing_ReportsUnchanged_WhenListMatchesExisting()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingBodyParts();
        AvatarWearable[] before = sp.Appearance.Wearables;

        AvatarWearable[] merged = AvatarFactoryModule.MergeNowWearing(
            before,
            NowWearing((Shape, ShapeItem), (Skin, SkinItem), (Hair, HairItem), (Eyes, EyesItem)).NowWearing,
            out bool changed);

        Assert.False(changed);
        Assert.NotSame(before, merged);
        Assert.Equal(TestHelpers.ParseTail(0x20), merged[Shape].GetAsset(ShapeItem));

        // And via the event path the stored array must be left untouched.
        ((TestClient)sp.ControllingClient).TriggerAvatarNowWearing(
            NowWearing((Shape, ShapeItem), (Skin, SkinItem), (Hair, HairItem), (Eyes, EyesItem)));
        Assert.Same(before, sp.Appearance.Wearables);
    }

    /// <summary>
    /// (e) The legacy packet carries one item per type (Firestorm sends the first item of each), so a type
    /// listed with one item the agent already wears among several keeps every layer, in order, with its
    /// asset ids, and reports unchanged. Nine Universal layers collapsed to one on Nimbus Grid when this
    /// packet landed after the server-side-baking outfit-folder handshake.
    /// </summary>
    [Fact]
    public void OneWornItemInMultiLayerSlot_KeepsEveryLayer_AndReportsUnchanged()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingNineUniversals();
        AvatarWearable[] before = sp.Appearance.Wearables;

        AvatarWearable[] merged = AvatarFactoryModule.MergeNowWearing(
            before,
            NowWearing((Universal, TestHelpers.ParseTail(0x40))).NowWearing,
            out bool changed);

        Assert.False(changed);
        Assert.Equal(9, merged[Universal].Count);
        for (int n = 0; n < 9; n++)
        {
            Assert.Equal(TestHelpers.ParseTail(0x40 + n), merged[Universal][n].ItemID);
            Assert.Equal(TestHelpers.ParseTail(0x50 + n), merged[Universal][n].AssetID);
        }

        // And via the event path the stored array must be left untouched.
        ((TestClient)sp.ControllingClient).TriggerAvatarNowWearing(
            NowWearing((Universal, TestHelpers.ParseTail(0x40))));
        Assert.Same(before, sp.Appearance.Wearables);
    }

    /// <summary>(f) One listed item the agent does not wear yet still replaces the slot (pre-rule behaviour, kept).</summary>
    [Fact]
    public void OneNewItemInMultiLayerSlot_ReplacesTheSlot()
    {
        TestHelpers.InMethod();
        (AvatarFactoryModule _, ScenePresence sp) = SetupPresenceWearingNineUniversals();
        UUID tenth = TestHelpers.ParseTail(0x49);

        AvatarWearable[] merged = AvatarFactoryModule.MergeNowWearing(
            sp.Appearance.Wearables,
            NowWearing((Universal, tenth)).NowWearing,
            out bool changed);

        Assert.True(changed);
        Assert.Equal(1, merged[Universal].Count);
        Assert.Equal(tenth, merged[Universal][0].ItemID);
        Assert.Equal(UUID.Zero, merged[Universal][0].AssetID);
    }
}
