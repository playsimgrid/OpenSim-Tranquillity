/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using log4net;
using Nini.Config;

namespace OpenSim.Framework;

/// <summary>
/// May this caller reach a control-plane endpoint?
///
/// A number of endpoints on the public ports are only ever meant to be called by this grid's
/// own servers - region agent-create and object-create, neighbour-hello, the region friends
/// handler, the privileged cross-grid IM dialogs, the profile write methods, the hypergrid
/// group writes. Upstream ships them on the same ports it serves foreign hypergrid traffic
/// on, with no caller authentication at all. A source-address allowlist in front of them
/// closes most of that surface.
///
/// WHY THIS DEFAULTS TO OFF, AND WHY OBSERVE EXISTS
///
/// A gate that refuses the wrong address does not look like a bad allowlist. It looks like
/// the grid being broken: regions cannot hand agents to each other, neighbours stop
/// appearing, friendship events stop arriving. Whoever debugs that next will reasonably
/// suspect the security change rather than its configuration.
///
/// And the correct list is NOT guessable from the outside. On this estate, for example,
/// regions reach ROBUST over private ports by container name, the two hosts sit on different
/// public networks joined by a VPN, and region-to-region traffic leaves via the public host
/// name and comes back. Any of those paths can present an address a reasonable person would
/// not have listed.
///
/// So the mode ladder is deliberate:
///
///   Off      - the default. No decision is made, nothing is refused, nothing is logged.
///              A grid that pulls a new build does not silently start enforcing.
///   Observe  - decide and LOG what would have been refused, but allow it. This is how the
///              allowlist gets proven against real traffic instead of assumed.
///   Enforce  - refuse. Only worth turning on once Observe has been quiet for a while.
///
/// Turning it up is a config line somebody can point at afterwards. That is the whole point.
/// </summary>
public static class ControlPlaneGate
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public enum GateMode
    {
        Off,
        Observe,
        Enforce
    }

    /// <summary>Headers a request carries when it came from an in-world script.</summary>
    private static readonly string[] ScriptMarkerHeaders =
    {
        "X-SecondLife-Shard",
        "X-SecondLife-Object-Name",
        "X-SecondLife-Object-Key",
        "X-SecondLife-Region",
        "X-SecondLife-Local-Position",
        "X-SecondLife-Owner-Name",
        "X-SecondLife-Owner-Key"
    };

    private static GateMode m_mode = GateMode.Off;
    private static List<(IPAddress Network, int Prefix)> m_trusted = new();
    private static bool m_initialised;
    private static readonly object m_initLock = new();

    public static GateMode Mode => m_mode;

    /// <summary>
    /// Read configuration. Idempotent and safe to call from every site that has an
    /// IConfigSource - the first caller wins, later ones are no-ops. Done this way on purpose:
    /// there is no single startup hook shared by the region process and the ROBUST process, and
    /// a gate that silently never initialised would be worse than one configured twice.
    /// </summary>
    public static void Initialise(IConfigSource source)
    {
        if (m_initialised || source is null)
            return;

        lock (m_initLock)
        {
            if (m_initialised)
                return;

            m_initialised = true;

            IConfig cnf = source.Configs["Security"];
            if (cnf is null)
                return;

            string modeName = cnf.GetString("ControlPlaneMode", "off").Trim();
            if (!Enum.TryParse(modeName, true, out GateMode parsed))
            {
                m_log.WarnFormat("[CONTROL PLANE GATE]: unknown ControlPlaneMode '{0}'; staying off", modeName);
                return;
            }

            string sources = cnf.GetString("ControlPlaneTrustedSources", string.Empty);
            List<(IPAddress, int)> trusted = new();
            foreach (string entry in sources.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string item = entry.Trim();
                if (item.Length == 0)
                    continue;

                int prefix = -1;
                string addrPart = item;
                int slash = item.IndexOf('/');
                if (slash > 0)
                {
                    addrPart = item[..slash];
                    if (!int.TryParse(item[(slash + 1)..], out prefix))
                        prefix = -1;
                }

                if (!IPAddress.TryParse(addrPart, out IPAddress addr))
                {
                    m_log.WarnFormat("[CONTROL PLANE GATE]: ignoring unparseable trusted source '{0}'", item);
                    continue;
                }

                if (prefix < 0)
                    prefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

                trusted.Add((addr, prefix));
            }

            m_trusted = trusted;
            m_mode = parsed;

            m_log.InfoFormat("[CONTROL PLANE GATE]: mode {0}, {1} trusted source range(s) configured (loopback is always trusted)",
                    m_mode, m_trusted.Count);
        }
    }

    /// <summary>
    /// Clear the configured state so a test can configure it again. Only exists because the
    /// gate holds process-wide static state; without it tests would depend on running order,
    /// which is a worse trade than one clearly-labelled seam.
    /// </summary>
    public static void ResetForTests()
    {
        lock (m_initLock)
        {
            m_initialised = false;
            m_mode = GateMode.Off;
            m_trusted = new();
        }
    }

    /// <summary>
    /// True when the caller may proceed. In Off and Observe this always returns true; in
    /// Observe a refusal-that-would-have-happened is logged so the allowlist can be completed
    /// from real traffic before anything is actually refused.
    /// </summary>
    public static bool Allow(IPEndPoint peer, string endpoint)
    {
        if (m_mode == GateMode.Off)
            return true;

        if (IsTrusted(peer))
            return true;

        if (m_mode == GateMode.Observe)
        {
            m_log.WarnFormat("[CONTROL PLANE GATE]: OBSERVE - would refuse {0} from {1}; add it to ControlPlaneTrustedSources if it is ours",
                    endpoint, peer is null ? "(unknown)" : peer.Address.ToString());
            return true;
        }

        m_log.WarnFormat("[CONTROL PLANE GATE]: refusing {0} from {1}",
                endpoint, peer is null ? "(unknown)" : peer.Address.ToString());
        return false;
    }

    /// <summary>
    /// True when the request carries in-world-script marker headers. Such a request never
    /// legitimately reaches a control-plane endpoint, and refusing it stops a script running
    /// on one of our OWN regions - and therefore arriving from a trusted address - being used
    /// as a relay past the allowlist.
    /// </summary>
    public static bool HasInWorldScriptMarker(NameValueCollection headers)
    {
        if (headers is null)
            return false;

        foreach (string name in ScriptMarkerHeaders)
        {
            if (!string.IsNullOrEmpty(headers[name]))
                return true;
        }

        return false;
    }

    private static bool IsTrusted(IPEndPoint peer)
    {
        if (peer is null)
            return false;

        IPAddress addr = peer.Address;

        // Loopback is implicitly trusted: it is this host talking to itself.
        if (IPAddress.IsLoopback(addr))
            return true;

        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        foreach ((IPAddress network, int prefix) in m_trusted)
        {
            if (InNetwork(addr, network, prefix))
                return true;
        }

        return false;
    }

    private static bool InNetwork(IPAddress addr, IPAddress network, int prefix)
    {
        IPAddress net = network.IsIPv4MappedToIPv6 ? network.MapToIPv4() : network;

        if (addr.AddressFamily != net.AddressFamily)
            return false;

        byte[] a = addr.GetAddressBytes();
        byte[] n = net.GetAddressBytes();

        if (a.Length != n.Length)
            return false;

        if (prefix < 0 || prefix > a.Length * 8)
            return false;

        int fullBytes = prefix / 8;
        for (int i = 0; i < fullBytes; ++i)
        {
            if (a[i] != n[i])
                return false;
        }

        int remainder = prefix % 8;
        if (remainder != 0)
        {
            int mask = 0xFF << (8 - remainder);
            if ((a[fullBytes] & mask) != (n[fullBytes] & mask))
                return false;
        }

        return true;
    }
}
