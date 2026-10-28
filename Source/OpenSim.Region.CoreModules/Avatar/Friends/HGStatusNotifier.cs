using System.Reflection;
using OpenSim.Framework;
using OpenSim.Services.Connectors.Hypergrid;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

using OpenMetaverse;

using log4net;

namespace OpenSim.Region.CoreModules.Avatar.Friends;

public class HGStatusNotifier
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private HGFriendsModule m_FriendsModule;

    /// <summary>
    /// Ceiling on users whose foreign-grid status fan-out is in flight at once, and the
    /// per-grid timeout while it is.
    /// </summary>
    /// <remarks>
    /// Notify runs on a .NET thread pool worker (FriendsModule.StatusChange hands it to
    /// Util.FireAndForget) and walks the user's friend domains one at a time, blocking on each
    /// grid in turn. At the stock 15s per grid a user with friends on three unreachable grids
    /// held a pool worker for three quarters of a minute, and nothing capped how many users
    /// could be doing that at once. A hub region losing a few well-travelled people at the
    /// same time could therefore put more blocking work items on the pool than it had workers,
    /// and logins, teleports and QUERYACCESS queue behind them.
    ///
    /// Two separate bounds because they bound different things: the timeout bounds one grid,
    /// the gate bounds how much of the pool this can own. Kept apart from the local-sim
    /// numbers in FriendsSimConnector on purpose - a foreign grid across the internet is a
    /// different population from a sim on our own LAN, and it is the foreign one that times
    /// out. Configurable as MaxConcurrentHGStatusNotifies and HGStatusNotifyTimeout in
    /// [HGFriendsModule].
    /// </remarks>
    private static int m_MaxConcurrent = 8;

    private static int m_TimeoutSecs = 4;

    private static SemaphoreSlim m_Gate = new SemaphoreSlim(8, 8);

    private static int m_Shed;
    private static long m_LastShedLogMS;

    public static void Configure(int timeoutSecs, int maxConcurrent)
    {
        if (timeoutSecs > 0)
            m_TimeoutSecs = timeoutSecs;

        if (maxConcurrent > 0 && maxConcurrent != m_MaxConcurrent)
        {
            m_MaxConcurrent = maxConcurrent;
            m_Gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        m_log.InfoFormat(
            "[HG STATUS NOTIFIER]: foreign-grid status fan-out capped at {0} in flight, {1}s per grid",
            m_MaxConcurrent, m_TimeoutSecs);
    }

    public static int Shed => Volatile.Read(ref m_Shed);

    public static int MaxConcurrent => m_MaxConcurrent;

    public static int TimeoutSecs => m_TimeoutSecs;

    public HGStatusNotifier(HGFriendsModule friendsModule)
    {
        m_FriendsModule = friendsModule;
    }

    public void Notify(UUID userID, Dictionary<string, List<FriendInfo>> friendsPerDomain, bool online)
    {
        if(m_FriendsModule is null)
            return;

        foreach (KeyValuePair<string, List<FriendInfo>> kvp in friendsPerDomain)
        {
            // For the others, call the user agent service
            List<string> ids = new(kvp.Value.Count);
            foreach (FriendInfo f in kvp.Value)
                ids.Add(f.Friend);

            if (ids.Count == 0)
                continue; // no one to notify. caller don't do this

            //m_log.DebugFormat("[HG STATUS NOTIFIER]: Notifying {0} friends in {1}", ids.Count, kvp.Key);
            // ASSUMPTION: we assume that all users for one home domain
            // have exactly the same set of service URLs.
            // If this is ever not true, we need to change this.
            if (Util.ParseUniversalUserIdentifier(ids[0], out UUID friendID))
            {
                string friendsServerURI = m_FriendsModule.UserManagementModule.GetUserServerURL(friendID, "FriendsServerURI");
                if (!string.IsNullOrEmpty(friendsServerURI))
                {
                    HGFriendsServicesConnector fConn = new(friendsServerURI);

                    // Wait(0) and skip, never a blocking wait: queueing here would hold this
                    // worker on top of the one already holding the permit, which is the
                    // behaviour the cap exists to prevent. Still one permit at a time per
                    // user, so the loop stays serial - the online cache this writes back into
                    // (FriendsModule.m_OnlineFriendsCache) is an unlocked Dictionary of
                    // HashSets, and making one user's domains concurrent would be a new
                    // writer race for the sake of a few seconds.
                    if (!m_Gate.Wait(0))
                    {
                        int shed = Interlocked.Increment(ref m_Shed);

                        long nowMS = Environment.TickCount64;
                        if (nowMS - Volatile.Read(ref m_LastShedLogMS) > 60000)
                        {
                            Volatile.Write(ref m_LastShedLogMS, nowMS);
                            m_log.WarnFormat(
                                "[HG STATUS NOTIFIER]: skipped notifying {0} friends at {1}: already {2} foreign-grid fan-outs in flight ({3} skipped in total).",
                                ids.Count, kvp.Key, m_MaxConcurrent, shed);
                        }

                        continue;
                    }

                    List<UUID> friendsOnline;
                    try
                    {
                        friendsOnline = fConn.StatusNotification(ids, userID, online, m_TimeoutSecs);
                    }
                    finally
                    {
                        m_Gate.Release();
                    }

                    if (friendsOnline.Count > 0)
                    {
                        IClientAPI client = m_FriendsModule.LocateClientObject(userID);
                        if(client is not null)
                        {
                            m_FriendsModule.CacheFriendsOnline(userID, friendsOnline, online);
                            if(online)
                                client?.SendAgentOnline(friendsOnline.ToArray());
                            else
                                client?.SendAgentOffline(friendsOnline.ToArray());
                        }
                    }
                }
            }
        }
    }
}
