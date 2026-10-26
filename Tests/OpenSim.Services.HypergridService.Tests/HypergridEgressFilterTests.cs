/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using OpenSim.Services.HypergridService;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// The egress filter decides whether this service may fetch a URL a CALLER handed it.
///
/// Worth testing because it fails in both directions and both are quiet: too loose and the
/// hypergrid entry points stay an internal port scanner, too tight and legitimate hypergrid
/// travel to a real foreign grid stops working with a message about reachability.
///
/// IP literals are used throughout rather than host names: these must pass or fail on the
/// address, and a test that depended on DNS would be testing the network, not the filter.
/// </summary>
[TestFixture]
public class HypergridEgressFilterTests
{
    private static bool Allowed(string url)
    {
        return HypergridEgressFilter.IsAllowedTarget(url, out _);
    }

    [SetUp]
    public void ClearCarveOut()
    {
        HypergridEgressFilter.LocalGatewayURL = string.Empty;
    }

    [Test]
    public void PublicAddressIsAllowed()
    {
        // A real foreign grid. If this fails, hypergrid travel is broken.
        Assert.That(Allowed("http://203.0.113.10:8002/"), Is.True);
    }

    [Test]
    public void LoopbackIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Allowed("http://127.0.0.1:8003/"), Is.False);
            Assert.That(Allowed("http://[::1]:8003/"), Is.False);
        });
    }

    [Test]
    public void PrivateRangesAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Allowed("http://10.77.0.1:8003/"), Is.False);
            Assert.That(Allowed("http://172.18.0.10:8002/"), Is.False);
            Assert.That(Allowed("http://192.168.1.10:8002/"), Is.False);
        });
    }

    [Test]
    public void CloudMetadataIsRefused()
    {
        Assert.That(Allowed("http://169.254.169.254/latest/meta-data/"), Is.False);
    }

    [Test]
    public void CarrierGradeNatIsRefused()
    {
        Assert.That(Allowed("http://100.100.0.1:8002/"), Is.False);
    }

    [Test]
    public void UniqueLocalIPv6IsRefused()
    {
        Assert.That(Allowed("http://[fd00::1]:8002/"), Is.False);
    }

    [Test]
    public void NonHttpSchemesAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Allowed("file:///etc/passwd"), Is.False);
            Assert.That(Allowed("gopher://203.0.113.10/"), Is.False);
        });
    }

    [Test]
    public void EmptyAndUnparseableAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Allowed(null), Is.False);
            Assert.That(Allowed(""), Is.False);
            Assert.That(Allowed("not a url"), Is.False);
        });
    }

    [Test]
    public void OwnGatewayIsAllowedEvenWhenPrivate()
    {
        // The local grid is a legitimate target; without this carve-out an intra-grid
        // hypergrid login to a privately-addressed gateway would be refused.
        HypergridEgressFilter.LocalGatewayURL = "http://10.77.0.1:8002/";
        Assert.That(Allowed("http://10.77.0.1:8002/"), Is.True);
    }

    [Test]
    public void CarveOutDoesNotWhitelistOtherPrivateHosts()
    {
        HypergridEgressFilter.LocalGatewayURL = "http://10.77.0.1:8002/";
        Assert.That(Allowed("http://10.77.0.2:8003/"), Is.False);
    }

    [Test]
    public void RefusalCarriesAReason()
    {
        HypergridEgressFilter.IsAllowedTarget("http://127.0.0.1/", out string reason);
        Assert.That(reason, Is.Not.Empty);
    }
}
