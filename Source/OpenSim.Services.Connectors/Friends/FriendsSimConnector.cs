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

using System.Collections.Concurrent;
using System.Reflection;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;
using OpenSim.Framework;

using OpenMetaverse;
using log4net;

namespace OpenSim.Services.Connectors.Friends;

public class FriendsSimConnector
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// Timeout, in seconds, for one friends-status notification POST.
    /// </summary>
    /// <remarks>
    /// Deliberately much shorter than the 15s the friendship calls keep, and deliberately a
    /// separate number from theirs. A status notification is advisory - a friend's dot turning
    /// grey a few seconds late costs nothing - whereas a friendship offer dropped because the
    /// far sim was slow to answer is a user-visible failure. So this one is allowed to give up
    /// early and the others are not. Configurable as StatusNotifyTimeout in [Friends].
    /// </remarks>
    private static int m_StatusNotifyTimeoutSecs = 3;

    /// <summary>
    /// How many status notification POSTs may be in flight at once, and how deep the queue in
    /// front of them is allowed to get.
    /// </summary>
    /// <remarks>
    /// The problem being fixed: every one of these POSTs is a BLOCKING request, and the fan-out
    /// is one POST per online friend per arrival or departure. Handing each of them to
    /// Util.FireAndForget - which is ThreadPool.UnsafeQueueUserWorkItem - meant the work was
    /// already asynchronous and already unbounded, so a hub region with a few well-connected
    /// people could put more blocking items on the .NET pool than the pool had workers. SimFPS
    /// stays healthy while that happens, because the scene loop has its own thread; what stops
    /// is everything that needs a pool worker - logins, teleports, QUERYACCESS, the status
    /// page, even the watchdog tick. That is why it reads as a network or Robust fault.
    ///
    /// Why a dedicated queue and dedicated threads rather than a semaphore around the
    /// FireAndForget: a semaphore acquired BEFORE queueing bounds the rate of ISSUE, not the
    /// number in flight, and those are not the same thing. FriendsModule.StatusNotify issues
    /// the whole fan-out in a tight loop in a few milliseconds, long before the pool has
    /// started any of the work items, so every permit is still held and almost the entire
    /// fan-out gets dropped - measured at 184 of 200 dropped against a server replying in
    /// 10ms, which is not a cap, it is a leak. A semaphore acquired AFTER queueing, inside the
    /// work item, bounds the right number but makes each waiting item sit on a pool worker,
    /// which is the disease.
    ///
    /// So these calls come off the .NET pool altogether. m_Workers threads own the sockets,
    /// the queue in front of them absorbs the burst, and issuing is a non-blocking enqueue.
    /// Nothing is dropped until the queue itself is full, and a full queue means thousands of
    /// notifications already waiting, at which point dropping is the only honest option left.
    /// Configurable as MaxConcurrentStatusNotifies and StatusNotifyQueueDepth in [Friends].
    /// </remarks>
    private static int m_MaxConcurrentStatusNotifies = 16;

    private static int m_StatusNotifyQueueDepth = 2000;

    private static BlockingCollection<(string uri, string body)> m_StatusNotifyQueue;

    private static readonly object m_StatusNotifyStartLock = new object();

    private static int m_StatusNotifiesShed;
    private static long m_LastShedLogMS;

    /// <summary>
    /// Set the status-notification limits. Called once from the friends module at region
    /// start; the Robust-side callers (UserAgentService, HGFriendsService) never call it and
    /// run on the defaults above, which is intended - they fan out through this same code and
    /// would starve the same way.
    /// </summary>
    public static void ConfigureStatusNotify(int timeoutSecs, int maxConcurrent, int queueDepth = 0)
    {
        lock (m_StatusNotifyStartLock)
        {
            if (timeoutSecs > 0)
                m_StatusNotifyTimeoutSecs = timeoutSecs;

            if (queueDepth > 0)
                m_StatusNotifyQueueDepth = queueDepth;

            if (maxConcurrent > 0)
                m_MaxConcurrentStatusNotifies = maxConcurrent;

            // Only ever started once. Reconfiguring after the threads are up changes the
            // timeout, which they read per request, but not their number - there is one
            // process-wide dispatcher and restarting it mid-flight would abandon whatever is
            // queued for no benefit.
            if (m_StatusNotifyQueue is null)
                StartStatusNotifyWorkers();
        }

        m_log.InfoFormat(
            "[FRIENDS SIM CONNECTOR]: status notifications: {0} senders, queue depth {1}, {2}s timeout each",
            m_MaxConcurrentStatusNotifies, m_StatusNotifyQueueDepth, m_StatusNotifyTimeoutSecs);
    }

    private static void StartStatusNotifyWorkers()
    {
        m_StatusNotifyQueue = new BlockingCollection<(string uri, string body)>(m_StatusNotifyQueueDepth);

        for (int i = 0; i < m_MaxConcurrentStatusNotifies; i++)
        {
            Thread t = new Thread(StatusNotifyWorker)
            {
                // Background, so these never hold up a shutdown: a notification in flight is
                // not worth delaying a region stop for.
                IsBackground = true,
                Name = "FriendsStatusNotify" + i,
                Priority = ThreadPriority.BelowNormal
            };
            t.Start();
        }
    }

    private static void StatusNotifyWorker()
    {
        foreach ((string uri, string body) in m_StatusNotifyQueue.GetConsumingEnumerable())
        {
            // Read per request on purpose, so a console or config change to the timeout takes
            // effect without restarting the senders.
            Post(uri, body, m_StatusNotifyTimeoutSecs);
        }
    }

    /// <summary>
    /// Number of status notifications dropped because the queue was full. Never reset.
    /// </summary>
    public static int StatusNotifiesShed => Volatile.Read(ref m_StatusNotifiesShed);

    public static int MaxConcurrentStatusNotifies => m_MaxConcurrentStatusNotifies;

    public static int StatusNotifyQueueDepth => m_StatusNotifyQueueDepth;

    public static int StatusNotifyTimeoutSecs => m_StatusNotifyTimeoutSecs;

    /// <summary>How many notifications are waiting for a sender. For the status page.</summary>
    public static int StatusNotifyQueued => m_StatusNotifyQueue?.Count ?? 0;

    protected virtual string ServicePath()
    {
        return "friends";
    }

    public bool FriendshipOffered(GridRegion region, UUID userID, UUID friendID, string message)
    {
        return FriendshipOffered(region, userID, friendID, message, String.Empty);
    }

    public virtual bool FriendshipOffered(GridRegion region, UUID userID, UUID friendID, string message, string userName)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "friendship_offered";

        sendData["FromID"] = userID.ToString();
        sendData["ToID"] = friendID.ToString();
        sendData["Message"] = message;
        if (userName != String.Empty)
            sendData["FromName"] = userName;

        return Call(region, sendData);
    }

    public bool FriendshipApproved(GridRegion region, UUID userID, string userName, UUID friendID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "friendship_approved";

        sendData["FromID"] = userID.ToString();
        sendData["FromName"] = userName;
        sendData["ToID"] = friendID.ToString();

        return Call(region, sendData);
    }

    public bool FriendshipDenied(GridRegion region, UUID userID, string userName, UUID friendID)
    {
        if (region == null)
            return false;

        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "friendship_denied";

        sendData["FromID"] = userID.ToString();
        sendData["FromName"] = userName;
        sendData["ToID"] = friendID.ToString();

        return Call(region, sendData);
    }

    public bool FriendshipTerminated(GridRegion region, UUID userID, UUID friendID)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "friendship_terminated";

        sendData["FromID"] = userID.ToString();
        sendData["ToID"] = friendID.ToString();

        return Call(region, sendData);
    }

    public bool GrantRights(GridRegion region, UUID userID, UUID friendID, int userFlags, int rights)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "grant_rights";

        sendData["FromID"] = userID.ToString();
        sendData["ToID"] = friendID.ToString();
        sendData["UserFlags"] = userFlags.ToString();
        sendData["Rights"] = rights.ToString();

        return Call(region, sendData);
    }

    public bool StatusNotify(GridRegion region, UUID userID, string friendID, bool online)
    {
        Dictionary<string, object> sendData = new Dictionary<string, object>();
        //sendData["VERSIONMIN"] = ProtocolVersions.ClientProtocolVersionMin.ToString();
        //sendData["VERSIONMAX"] = ProtocolVersions.ClientProtocolVersionMax.ToString();
        sendData["METHOD"] = "status";

        sendData["FromID"] = userID.ToString();
        sendData["ToID"] = friendID;
        sendData["Online"] = online.ToString();

        // CallStatus, not Call: bounded and short-timeout. See m_MaxConcurrentStatusNotifies.
        return CallStatus(region, sendData);
    }

    /// <summary>
    /// The user-initiated calls: friendship offered, approved, denied, terminated, rights
    /// granted. Unbounded, and keeping the 15s timeout they have always had.
    /// </summary>
    /// <remarks>
    /// Left alone on purpose. These are one call per user action, not a fan-out, so they
    /// cannot flood the pool the way the status notifications could; and dropping one of them
    /// to save a thread would trade an invisible cost for a visible one.
    /// </remarks>
    private bool Call(GridRegion region, Dictionary<string, object> sendData)
    {
        string uri = BuildUri(region);
        if (uri is null)
            return false;

        string reqString = ServerUtils.BuildQueryString(sendData);
        Util.FireAndForget(x => Post(uri, reqString, 15), null, "FriendsSimConnector.Call");

        return true;
    }

    /// <summary>
    /// The fan-out call: one status notification. Enqueued for the dedicated senders, never
    /// put on the .NET thread pool, and dropped only if the queue is full.
    /// </summary>
    private bool CallStatus(GridRegion region, Dictionary<string, object> sendData)
    {
        string uri = BuildUri(region);
        if (uri is null)
            return false;

        // Lazily start the senders for the callers that never configure them - the two Robust
        // services construct a FriendsSimConnector directly and fan out through it.
        if (m_StatusNotifyQueue is null)
        {
            lock (m_StatusNotifyStartLock)
            {
                if (m_StatusNotifyQueue is null)
                    StartStatusNotifyWorkers();
            }
        }

        string reqString = ServerUtils.BuildQueryString(sendData);

        // TryAdd with no wait. Whichever thread we are on - a region thread on agent close, or
        // a pool worker under FriendsModule.StatusChange - it must not end up waiting on a
        // far sim, and with a queue this deep a refusal means something is badly wrong rather
        // than merely busy.
        if (m_StatusNotifyQueue.TryAdd((uri, reqString)))
            return true;

        int shed = Interlocked.Increment(ref m_StatusNotifiesShed);

        // Throttled to once a minute: one line says the queue is full as well as a thousand
        // do, and a region in this state does not need its log hammered as well.
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref m_LastShedLogMS) > 60000)
        {
            Volatile.Write(ref m_LastShedLogMS, now);
            m_log.WarnFormat(
                "[FRIENDS SIM CONNECTOR]: dropped a friends status notification to {0}: {1} already queued for {2} senders ({3} dropped in total). Online status elsewhere will lag until viewers refresh.",
                uri, m_StatusNotifyQueueDepth, m_MaxConcurrentStatusNotifies, shed);
        }

        return false;
    }

    private string BuildUri(GridRegion region)
    {
        // Hoisted out of the queued work item. It used to be checked after the hand-off, so a
        // null region still cost a trip through the pool to discover there was nothing to do.
        if (region is null || string.IsNullOrEmpty(region.ServerURI))
            return null;

        string path = ServicePath();
        if (!region.ServerURI.EndsWith("/"))
            path = "/" + path;

        return region.ServerURI + path;
    }

    private static void Post(string uri, string reqString, int timeoutsecs)
    {
        // m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: calling {0}", uri);
        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, timeoutsecs, null, false);
            if (reply != string.Empty)
            {
                Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                if (replyData.ContainsKey("RESULT"))
                {
//                            if (replyData["RESULT"].ToString().ToLower() == "true")
//                                return;
//                            else
                    return;
                }
                else
                    m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: reply data does not contain result field");

            }
            else
                m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: received empty reply");
        }
        catch (Exception e)
        {
            m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: Exception when contacting remote sim at {0}: {1}", uri, e.Message);
        }
    }
}
