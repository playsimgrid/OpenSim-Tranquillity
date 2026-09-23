/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using System;
using System.Net;
using System.Net.Sockets;

namespace OpenSim.Services.HypergridService
{

    /// <summary>
    /// Whether this service may make an outbound HTTP call to a URL a CALLER supplied.
    ///
    /// Two unauthenticated hypergrid entry points hand us a target and we then fetch it:
    /// /homeagent takes a gatekeeper URL and posts to it, and /foreignagent fires a callback
    /// to a "home" URL - the latter before the ban checks even run. Without a filter that is a
    /// server-side request forgery primitive: the caller aims this process at loopback, at the
    /// private network, or at a cloud metadata endpoint, and reflected error text turns it into
    /// a semi-blind internal port scanner.
    ///
    /// The rule is deliberately about ADDRESSES, not host names. A name is resolved and EVERY
    /// address it resolves to must be acceptable, so an attacker cannot slip past with an
    /// IP literal, a name that resolves to loopback, or a name with one public and one private
    /// address. The only carve-out is this grid's own gateway, which is a legitimate target.
    ///
    /// KNOWN RESIDUALS, accepted knowingly rather than papered over:
    ///   - DNS rebinding. The resolution checked here is not pinned for the later connect, so a
    ///     name that answers publicly now and privately a moment later still gets through.
    ///   - HTTP redirects. A permitted target can redirect the client to somewhere refused here.
    /// Closing either needs connect-by-pinned-IP at the HTTP client layer, which is a larger
    /// change than this filter and does not belong in it.
    /// </summary>
    public static class HypergridEgressFilter
    {
        /// <summary>This grid's own gateway, which is always an allowed target. Set at startup.</summary>
        public static string LocalGatewayURL = string.Empty;

        public static bool IsAllowedTarget(string url, out string reason)
        {
            reason = string.Empty;

            if (string.IsNullOrEmpty(url))
            {
                reason = "empty target";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                reason = "unparseable target";
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                reason = "scheme " + uri.Scheme + " is not permitted";
                return false;
            }

            // Our own gateway is a legitimate destination even though it may resolve privately.
            if (!string.IsNullOrEmpty(LocalGatewayURL) &&
                    Uri.TryCreate(LocalGatewayURL, UriKind.Absolute, out Uri local) &&
                    uri.Host.Equals(local.Host, StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
            }
            catch
            {
                reason = "target does not resolve";
                return false;
            }

            if (addresses is null || addresses.Length == 0)
            {
                reason = "target does not resolve";
                return false;
            }

            // EVERY resolved address must be acceptable. One bad answer is enough to refuse:
            // a name with a public and a private address must not be usable to reach the latter.
            foreach (IPAddress addr in addresses)
            {
                if (IsInternal(addr))
                {
                    reason = "target resolves to a non-routable or internal address";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Loopback, private, link-local (which covers cloud metadata at 169.254.169.254),
        /// carrier-grade NAT, and the IPv6 equivalents.
        /// </summary>
        private static bool IsInternal(IPAddress ip)
        {
            if (ip is null)
                return true;

            if (IPAddress.IsLoopback(ip))
                return true;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                    return true;

                // Unique local addresses, fc00::/7.
                byte first = ip.GetAddressBytes()[0];
                if ((first & 0xFE) == 0xFC)
                    return true;

                if (ip.IsIPv4MappedToIPv6)
                    return IsInternal(ip.MapToIPv4());

                return false;
            }

            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return true;

            byte[] b = ip.GetAddressBytes();

            if (b[0] == 10)                                     return true;    // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)        return true;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168)                     return true;    // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254)                     return true;    // link-local + metadata
            if (b[0] == 127)                                    return true;    // loopback
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       return true;    // CGNAT 100.64.0.0/10
            if (b[0] == 0)                                      return true;    // 0.0.0.0/8
            if (b[0] >= 224)                                    return true;    // multicast + reserved

            return false;
        }
    }
}
