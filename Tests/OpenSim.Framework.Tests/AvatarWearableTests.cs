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
using Xunit;

namespace OpenSim.Framework.Tests;

public class AvatarWearableTests
{
    /// <summary>
    /// A wearable type keeps every item added to it, in the order they were added. Nine is
    /// the count that a bound of five silently cut to five in a server-side bake.
    /// </summary>
    [Fact]
    public void TestNineItemsAreKeptInOrder()
    {
        UUID[] items = new UUID[9];
        UUID[] assets = new UUID[9];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = UUID.Random();
            assets[i] = UUID.Random();
        }

        AvatarWearable wearable = new AvatarWearable();
        for (int i = 0; i < items.Length; i++)
            wearable.Add(items[i], assets[i]);

        Assert.True(wearable.Count == items.Length, $"Expected {items.Length} items, got {wearable.Count}");
        for (int i = 0; i < items.Length; i++)
        {
            Assert.True(wearable[i].ItemID == items[i], $"Item {i} is not the one added {i}th");
            Assert.True(wearable[i].AssetID == assets[i], $"Item {i} does not carry its own asset");
        }
    }

    [Fact]
    public void TestKnownItemUpdatesAssetInPlace()
    {
        UUID[] items = { UUID.Random(), UUID.Random(), UUID.Random() };
        AvatarWearable wearable = new AvatarWearable();
        foreach (UUID item in items)
            wearable.Add(item, UUID.Random());

        UUID newAsset = UUID.Random();
        wearable.Add(items[1], newAsset);

        Assert.True(wearable.Count == 3, "Adding a known item changed the count");
        Assert.True(wearable[1].ItemID == items[1], "Adding a known item moved it");
        Assert.True(wearable[1].AssetID == newAsset, "Adding a known item did not update its asset");
        Assert.True(wearable.GetAsset(items[1]) == newAsset);
    }

    [Fact]
    public void TestBoundIsMaxItemsPerType()
    {
        AvatarWearable wearable = new AvatarWearable();
        for (int i = 0; i < AvatarWearable.MAX_ITEMS_PER_TYPE; i++)
            wearable.Add(UUID.Random(), UUID.Random());

        Assert.True(wearable.Count == AvatarWearable.MAX_ITEMS_PER_TYPE,
            $"Expected {AvatarWearable.MAX_ITEMS_PER_TYPE} items, got {wearable.Count}");

        UUID extra = UUID.Random();
        wearable.Add(extra, UUID.Random());

        Assert.True(wearable.Count == AvatarWearable.MAX_ITEMS_PER_TYPE, "An item past the bound was added");
        Assert.True(wearable.GetAsset(extra).IsZero(), "An item past the bound has an asset");
        Assert.True(wearable[AvatarWearable.MAX_ITEMS_PER_TYPE].ItemID.IsZero());
    }

    [Fact]
    public void TestZeroItemIdIsIgnored()
    {
        AvatarWearable wearable = new AvatarWearable();
        wearable.Add(UUID.Zero, UUID.Random());

        Assert.True(wearable.Count == 0, "A zero item id was added");
        Assert.True(wearable[0].ItemID.IsZero());
    }
}
