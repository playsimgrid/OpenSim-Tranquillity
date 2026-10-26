/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using System.Collections.Specialized;
using System.Net;

using Nini.Config;

using OpenSim.Framework;

// Housed in this project rather than OpenSim.Framework.Tests because that project does not
// currently compile (pre-existing breakage in AgentCircuitManagerTests.cs, tracked separately
// as the corrupted-test-suite issue). This project is the one that builds and already holds
// the other security tests.
namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// The control-plane gate decides whether a caller may reach an endpoint only this grid's own
/// servers should reach.
///
/// Both failure directions are quiet, which is why these exist. Too loose and the endpoints
/// stay open. Too tight and regions stop being able to hand agents to each other, which reads
/// as the grid being broken rather than as a short allowlist - so the Off and Observe modes
/// allowing everything is a tested property, not an accident.
///
/// The gate holds static state, so every test configures it explicitly rather than relying on
/// order.
/// </summary>
[TestFixture]
public class ControlPlaneGateTests
{
    private static IConfigSource Config(string mode, string sources)
    {
        IniConfigSource src = new();
        IConfig cnf = src.AddConfig("Security");
        cnf.Set("ControlPlaneMode", mode);
        cnf.Set("ControlPlaneTrustedSources", sources);
        return src;
    }

    private static IPEndPoint Peer(string addr)
    {
        return new IPEndPoint(IPAddress.Parse(addr), 9000);
    }

    private static void Configure(string mode, string sources)
    {
        ControlPlaneGate.ResetForTests();
        ControlPlaneGate.Initialise(Config(mode, sources));
    }

    [Test]
    public void DefaultIsOffAndAllowsEverything()
    {
        ControlPlaneGate.ResetForTests();
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Mode, Is.EqualTo(ControlPlaneGate.GateMode.Off));
            Assert.That(ControlPlaneGate.Allow(Peer("203.0.113.9"), "test"), Is.True);
        });
    }

    [Test]
    public void ObserveAllowsEvenAnUntrustedCaller()
    {
        // The whole point of Observe: it reports, it does not refuse.
        Configure("observe", "10.77.0.0/24");
        Assert.That(ControlPlaneGate.Allow(Peer("203.0.113.9"), "test"), Is.True);
    }

    [Test]
    public void EnforceRefusesAnUntrustedCaller()
    {
        Configure("enforce", "10.77.0.0/24");
        Assert.That(ControlPlaneGate.Allow(Peer("203.0.113.9"), "test"), Is.False);
    }

    [Test]
    public void EnforceAllowsATrustedCaller()
    {
        Configure("enforce", "10.77.0.0/24");
        Assert.That(ControlPlaneGate.Allow(Peer("10.77.0.2"), "test"), Is.True);
    }

    [Test]
    public void LoopbackIsAlwaysTrustedEvenWithAnEmptyList()
    {
        Configure("enforce", "");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("127.0.0.1"), "test"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("::1"), "test"), Is.True);
        });
    }

    [Test]
    public void PrefixBoundariesAreRespected()
    {
        // A /24 must not quietly behave like a /16.
        Configure("enforce", "10.77.0.0/24");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("10.77.0.255"), "in range"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("10.77.1.1"), "outside /24"), Is.False);
        });
    }

    [Test]
    public void NonByteAlignedPrefixWorks()
    {
        Configure("enforce", "217.77.0.0/21");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("217.77.7.246"), "inside /21"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("217.77.8.1"), "outside /21"), Is.False);
        });
    }

    [Test]
    public void BareAddressIsTreatedAsASingleHost()
    {
        Configure("enforce", "209.126.80.191");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("209.126.80.191"), "the host"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("209.126.80.192"), "neighbour"), Is.False);
        });
    }

    [Test]
    public void SeveralRangesAreAllHonoured()
    {
        Configure("enforce", "10.77.0.0/24, 217.77.7.246, 209.126.80.191");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("10.77.0.5"), "vpn"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("217.77.7.246"), "region host"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("209.126.80.191"), "grid host"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("198.51.100.1"), "stranger"), Is.False);
        });
    }

    [Test]
    public void UnparseableEntriesAreSkippedWithoutTrustingThem()
    {
        Configure("enforce", "not-an-address, 10.77.0.0/24");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Allow(Peer("10.77.0.9"), "good entry still works"), Is.True);
            Assert.That(ControlPlaneGate.Allow(Peer("203.0.113.9"), "bad entry trusts nothing"), Is.False);
        });
    }

    [Test]
    public void UnknownModeNameStaysOff()
    {
        Configure("paranoid", "10.77.0.0/24");
        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.Mode, Is.EqualTo(ControlPlaneGate.GateMode.Off));
            Assert.That(ControlPlaneGate.Allow(Peer("203.0.113.9"), "test"), Is.True);
        });
    }

    [Test]
    public void NullPeerIsRefusedUnderEnforce()
    {
        Configure("enforce", "10.77.0.0/24");
        Assert.That(ControlPlaneGate.Allow(null, "test"), Is.False);
    }

    [Test]
    public void ScriptMarkerHeadersAreDetected()
    {
        NameValueCollection withMarker = new() { { "X-SecondLife-Object-Key", "abc" } };
        NameValueCollection plain = new() { { "Content-Type", "application/json" } };

        Assert.Multiple(() =>
        {
            Assert.That(ControlPlaneGate.HasInWorldScriptMarker(withMarker), Is.True);
            Assert.That(ControlPlaneGate.HasInWorldScriptMarker(plain), Is.False);
            Assert.That(ControlPlaneGate.HasInWorldScriptMarker(null), Is.False);
        });
    }
}

/// <summary>
/// The profiles split (#154): which JSON-RPC methods are trusted-only.
///
/// BE CLEAR ABOUT WHAT THIS DOES AND DOES NOT CHECK. These arrays are a statement of intent
/// held next to the code, not a reading of it: nothing here reaches into UserProfilesConnector,
/// so an edit that moves a method to the wrong side in the SOURCE will not fail these tests.
/// What they catch is an inconsistent or incomplete intent - a method on both lists, a count
/// that no longer adds up to the 18 registered, a mutating method declared public, or someone
/// gating only the writes and forgetting the two private reads.
///
/// The binding between this intent and the source was verified separately, by diffing the set
/// of registered method names before and after the change (18 before, 18 after, identical set)
/// and counting the trusted-only registrations (12). Anyone changing the split should redo that
/// comparison; these tests will not do it for them.
/// </summary>
[TestFixture]
public class ProfilesMethodClassificationTests
{
    // Methods a foreign grid legitimately calls to DISPLAY a local user's profile.
    private static readonly string[] MustStayPublic =
    {
        "avatarclassifiedsrequest",
        "classifieds_info_query",
        "avatarpicksrequest",
        "pickinforequest",
        "avatar_properties_request",
        "image_assets_request",
    };

    // Private data, plus every mutation. Note two of these are READS: one returns the
    // user's private notes, the other returns their email address.
    private static readonly string[] MustBeTrustedOnly =
    {
        "avatarnotesrequest",
        "user_preferences_request",
        "user_data_request",
        "classified_update",
        "classified_delete",
        "picks_update",
        "picks_delete",
        "avatar_notes_update",
        "avatar_properties_update",
        "avatar_interests_update",
        "user_preferences_update",
        "user_data_update",
    };

    [Test]
    public void TheTwoSetsDoNotOverlap()
    {
        foreach (string name in MustStayPublic)
            Assert.That(MustBeTrustedOnly, Does.Not.Contain(name), $"{name} is on both lists");
    }

    [Test]
    public void EveryRegisteredMethodIsClassified()
    {
        // 18 methods are registered; every one must be deliberately on one side or the other.
        Assert.That(MustStayPublic.Length + MustBeTrustedOnly.Length, Is.EqualTo(18));
    }

    [Test]
    public void PrivateReadsAreGatedNotJustWrites()
    {
        // The easy mistake is to gate only the _update and _delete methods. These two are
        // reads, and they are the ones that leak an email address and private notes.
        Assert.Multiple(() =>
        {
            Assert.That(MustBeTrustedOnly, Does.Contain("user_preferences_request"));
            Assert.That(MustBeTrustedOnly, Does.Contain("avatarnotesrequest"));
        });
    }

    [Test]
    public void NoMutatingMethodIsPublic()
    {
        foreach (string name in MustStayPublic)
        {
            Assert.That(name, Does.Not.Contain("_update"), $"{name} mutates and must not be public");
            Assert.That(name, Does.Not.Contain("_delete"), $"{name} mutates and must not be public");
        }
    }
}
