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
using Nini.Config;
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
    /// How many status notification POSTs may be in flight at once, and how much undelivered
    /// state is allowed to pile up behind them.
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
    /// Why dedicated threads rather than a semaphore around the FireAndForget: a semaphore
    /// acquired BEFORE queueing bounds the rate of ISSUE, not the number in flight, and those
    /// are not the same thing. FriendsModule.StatusNotify issues the whole fan-out in a tight
    /// loop in a few milliseconds, long before the pool has started any of the work items, so
    /// every permit is still held and almost the entire fan-out gets dropped - measured at 184
    /// of 200 dropped against a server replying in 10ms, which is not a cap, it is a leak. A
    /// semaphore acquired AFTER queueing, inside the work item, bounds the right number but
    /// makes each waiting item sit on a pool worker, which is the disease.
    ///
    /// So these calls come off the .NET pool altogether. m_MaxConcurrentStatusNotifies threads
    /// own the sockets and issuing is a non-blocking hand-off.
    /// Configurable as MaxConcurrentStatusNotifies and StatusNotifyQueueDepth in [Friends].
    /// </remarks>
    private static int m_MaxConcurrentStatusNotifies = 16;

    private static int m_StatusNotifyQueueDepth = 2000;

    /// <summary>
    /// How many times one notification is attempted before it is abandoned.
    /// </summary>
    /// <remarks>
    /// The short timeout above means a sim that is merely busy now fails a notification that
    /// would have succeeded at 15s. Without a retry that is a permanently wrong friends list
    /// for everyone who was looking, and it is a worse failure than the freeze it replaced:
    /// silent, and persisting until the viewer relogs. Every loss path here used to be a
    /// silent drop logged at Debug.
    ///
    /// Four attempts with the backoff below spans about 42 seconds. That number is chosen to
    /// cover a region restart - roughly 35s on this estate - so a notification aimed at a sim
    /// that is on its way back up survives the gap instead of disappearing into it.
    /// Configurable as StatusNotifyMaxAttempts in [Friends].
    /// </remarks>
    private static int m_StatusNotifyMaxAttempts = 4;

    private const int RETRY_BASE_MS = 2000;
    private const int RETRY_FACTOR = 4;
    private const int RETRY_CAP_MS = 60000;
    private const int RETRY_SCAN_MS = 1000;

    /// <summary>
    /// The latest status we still owe one friend about one user.
    /// </summary>
    /// <remarks>
    /// Keyed by (who changed, who is being told) and deliberately NOT by region or by message,
    /// because what is being delivered is a fact about a person rather than a request. Two
    /// consequences, both of them the point:
    ///
    /// A newer notification REPLACES an older undelivered one instead of queueing behind it.
    /// Without that, adding a retry would make things worse, not better: someone who logs out
    /// and straight back in would have the "offline" retried after the "online" had already
    /// landed, leaving the friend looking at exactly the wrong answer with nothing further
    /// coming to correct it. Ordering between independent senders was never guaranteed, so the
    /// only thing it is safe to retry is the CURRENT STATE, never the message.
    ///
    /// And if the friend has changed region since the notification was raised, the replacement
    /// carries the new URI, so a retry follows them instead of hammering the sim they left.
    /// </remarks>
    private sealed class PendingStatus
    {
        public string Uri;
        public string Body;
        public long Seq;
        public int Attempts;
        public long NextDueMS;
        public bool Queued;
    }

    private static ConcurrentDictionary<string, PendingStatus> m_Pending;

    /// <summary>Keys with state a sender should pick up now.</summary>
    private static BlockingCollection<string> m_Ready;

    private static long m_SeqCounter;
    private static int m_PendingCount;

    private static readonly object m_StatusNotifyStartLock = new object();

    private static int m_StatusNotifiesShed;
    private static int m_StatusNotifiesDelivered;
    private static int m_StatusNotifiesRetried;
    private static int m_StatusNotifiesAbandoned;
    private static int m_StatusNotifiesSuperseded;
    private static long m_LastShedLogMS;
    private static long m_LastAbandonLogMS;

    /// <summary>
    /// Apply the [Friends] section, wherever it lives.
    /// </summary>
    /// <remarks>
    /// Robust fans out status notifications through this same connector - UserAgentService and
    /// HGFriendsService both construct one and call StatusNotify on the inbound hypergrid path
    /// - but neither is a region module, so neither ever reached the region's configuration
    /// call and both ran on whatever the defaults happened to be, with no way to change them
    /// short of a rebuild. Both now call this with their own IConfigSource, so one [Friends]
    /// section configures the senders wherever they are running.
    /// </remarks>
    public static void ConfigureStatusNotify(IConfigSource config)
    {
        ConfigureStatusNotify(config?.Configs["Friends"]);
    }

    public static void ConfigureStatusNotify(IConfig friendsConfig)
    {
        if (friendsConfig is null)
        {
            ConfigureStatusNotify(0, 0, 0, 0);
            return;
        }

        ConfigureStatusNotify(
            friendsConfig.GetInt("StatusNotifyTimeout", m_StatusNotifyTimeoutSecs),
            friendsConfig.GetInt("MaxConcurrentStatusNotifies", m_MaxConcurrentStatusNotifies),
            friendsConfig.GetInt("StatusNotifyQueueDepth", m_StatusNotifyQueueDepth),
            friendsConfig.GetInt("StatusNotifyMaxAttempts", m_StatusNotifyMaxAttempts));
    }

    public static void ConfigureStatusNotify(int timeoutSecs, int maxConcurrent, int queueDepth = 0, int maxAttempts = 0)
    {
        lock (m_StatusNotifyStartLock)
        {
            if (timeoutSecs > 0)
                m_StatusNotifyTimeoutSecs = timeoutSecs;

            if (queueDepth > 0)
                m_StatusNotifyQueueDepth = queueDepth;

            if (maxConcurrent > 0)
                m_MaxConcurrentStatusNotifies = maxConcurrent;

            if (maxAttempts > 0)
                m_StatusNotifyMaxAttempts = maxAttempts;

            // Only ever started once. Reconfiguring after the threads are up changes the
            // timeout and the attempt limit, which they read per request, but not their
            // number - there is one process-wide dispatcher and restarting it mid-flight
            // would abandon whatever is waiting for no benefit.
            if (m_Ready is null)
                StartStatusNotifyWorkers();
        }

        m_log.InfoFormat(
            "[FRIENDS SIM CONNECTOR]: status notifications: {0} senders, queue depth {1}, {2}s timeout, up to {3} attempts each",
            m_MaxConcurrentStatusNotifies, m_StatusNotifyQueueDepth, m_StatusNotifyTimeoutSecs, m_StatusNotifyMaxAttempts);
    }

    private static void StartStatusNotifyWorkers()
    {
        m_Pending = new ConcurrentDictionary<string, PendingStatus>();
        m_Ready = new BlockingCollection<string>(m_StatusNotifyQueueDepth);

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

        Thread scanner = new Thread(StatusNotifyRetryScanner)
        {
            IsBackground = true,
            Name = "FriendsStatusRetry",
            Priority = ThreadPriority.BelowNormal
        };
        scanner.Start();
    }

    private static void StatusNotifyWorker()
    {
        foreach (string key in m_Ready.GetConsumingEnumerable())
        {
            if (!m_Pending.TryGetValue(key, out PendingStatus p))
                continue;

            string uri, body;
            long seq;
            lock (p)
            {
                p.Queued = false;
                uri = p.Uri;
                body = p.Body;
                seq = p.Seq;
            }

            // Read per request on purpose, so a config change to the timeout takes effect
            // without restarting the senders.
            bool ok = Post(uri, body, m_StatusNotifyTimeoutSecs);

            lock (p)
            {
                // Something newer arrived while this was in flight. Whatever this POST said
                // is no longer the truth, and the newer state has already been queued, so
                // leave the entry alone - success or failure both say nothing about it.
                if (p.Seq != seq)
                {
                    Interlocked.Increment(ref m_StatusNotifiesSuperseded);
                    continue;
                }

                if (ok)
                {
                    RemovePending(key);
                    Interlocked.Increment(ref m_StatusNotifiesDelivered);
                    continue;
                }

                p.Attempts++;
                p.NextDueMS = Environment.TickCount64 + RetryDelayMS(p.Attempts);

                if (p.Attempts < m_StatusNotifyMaxAttempts)
                {
                    // The scanner re-queues it when it falls due. Deliberately not retried
                    // on this thread: a sender that sleeps through a backoff is a sender not
                    // sending, exactly when the queue most needs draining.
                    Interlocked.Increment(ref m_StatusNotifiesRetried);
                    continue;
                }

                RemovePending(key);
            }

            int abandoned = Interlocked.Increment(ref m_StatusNotifiesAbandoned);
            long nowMS = Environment.TickCount64;
            if (nowMS - Volatile.Read(ref m_LastAbandonLogMS) > 60000)
            {
                Volatile.Write(ref m_LastAbandonLogMS, nowMS);
                m_log.WarnFormat(
                    "[FRIENDS SIM CONNECTOR]: gave up on a friends status notification to {0} after {1} attempts ({2} abandoned in total). That friend's list is now wrong until they relog.",
                    uri, m_StatusNotifyMaxAttempts, abandoned);
            }
        }
    }

    private static void StatusNotifyRetryScanner()
    {
        while (true)
        {
            Thread.Sleep(RETRY_SCAN_MS);

            try
            {
                long now = Environment.TickCount64;

                foreach (KeyValuePair<string, PendingStatus> kv in m_Pending)
                {
                    PendingStatus p = kv.Value;

                    lock (p)
                    {
                        if (p.Queued || p.Attempts == 0 || p.NextDueMS > now)
                            continue;

                        p.Queued = true;
                    }

                    if (!m_Ready.TryAdd(kv.Key))
                    {
                        // No room right now; it stays due and the next scan tries again.
                        lock (p)
                        {
                            p.Queued = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // This thread must not die: if it does, every failed notification stays
                // pending forever and nothing retries.
                m_log.WarnFormat("[FRIENDS SIM CONNECTOR]: status retry scan failed: {0}", e.Message);
            }
        }
    }

    private static int RetryDelayMS(int attempts)
    {
        long delay = RETRY_BASE_MS;

        for (int i = 1; i < attempts; i++)
        {
            delay *= RETRY_FACTOR;
            if (delay >= RETRY_CAP_MS)
                return RETRY_CAP_MS;
        }

        return (int)delay;
    }

    private static void RemovePending(string key)
    {
        if (m_Pending.TryRemove(key, out _))
            Interlocked.Decrement(ref m_PendingCount);
    }

    /// <summary>Status notifications refused outright because too much was already pending.</summary>
    public static int StatusNotifiesShed => Volatile.Read(ref m_StatusNotifiesShed);

    /// <summary>Status notifications a far sim accepted.</summary>
    public static int StatusNotifiesDelivered => Volatile.Read(ref m_StatusNotifiesDelivered);

    /// <summary>Attempts that failed and were scheduled for another try.</summary>
    public static int StatusNotifiesRetried => Volatile.Read(ref m_StatusNotifiesRetried);

    /// <summary>Notifications abandoned after the last attempt failed. These are wrong friends lists.</summary>
    public static int StatusNotifiesAbandoned => Volatile.Read(ref m_StatusNotifiesAbandoned);

    /// <summary>Deliveries whose state was replaced before they finished. Not a failure.</summary>
    public static int StatusNotifiesSuperseded => Volatile.Read(ref m_StatusNotifiesSuperseded);

    public static int MaxConcurrentStatusNotifies => m_MaxConcurrentStatusNotifies;

    public static int StatusNotifyQueueDepth => m_StatusNotifyQueueDepth;

    public static int StatusNotifyTimeoutSecs => m_StatusNotifyTimeoutSecs;

    public static int StatusNotifyMaxAttempts => m_StatusNotifyMaxAttempts;

    /// <summary>How much state is still undelivered. For the status page.</summary>
    public static int StatusNotifyQueued => Volatile.Read(ref m_PendingCount);

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

        // Lazily start the senders for the callers that never configure them.
        if (m_Ready is null)
        {
            lock (m_StatusNotifyStartLock)
            {
                if (m_Ready is null)
                    StartStatusNotifyWorkers();
            }
        }

        string body = ServerUtils.BuildQueryString(sendData);
        string key = sendData["FromID"] + "|" + sendData["ToID"];

        if (!m_Pending.TryGetValue(key, out PendingStatus p))
        {
            // Soft cap, read without a lock on purpose: a couple of entries either side of
            // the limit under a race costs nothing, and taking every bucket lock of a
            // ConcurrentDictionary on each notification during a fan-out storm would cost
            // rather a lot.
            if (Volatile.Read(ref m_PendingCount) >= m_StatusNotifyQueueDepth)
            {
                Shed(uri);
                return false;
            }

            p = new PendingStatus();
            if (m_Pending.TryAdd(key, p))
            {
                Interlocked.Increment(ref m_PendingCount);
            }
            else if (!m_Pending.TryGetValue(key, out p))
            {
                // Lost the race and the winner has already been delivered and removed.
                // Nothing to do: that delivery carried at least as new a state as this one.
                return true;
            }
        }

        lock (p)
        {
            p.Uri = uri;
            p.Body = body;
            p.Seq = Interlocked.Increment(ref m_SeqCounter);
            p.Attempts = 0;
            p.NextDueMS = 0;

            // Already waiting for a sender. The sender reads the entry when it picks it up,
            // so it will carry this state rather than the one it was queued for - which is
            // the whole reason the queue holds keys and not messages.
            if (p.Queued)
                return true;

            p.Queued = true;
        }

        if (m_Ready.TryAdd(key))
            return true;

        lock (p)
        {
            p.Queued = false;
        }

        Shed(uri);
        return false;
    }

    private static void Shed(string uri)
    {
        int shed = Interlocked.Increment(ref m_StatusNotifiesShed);

        // Throttled to once a minute: one line says the queue is full as well as a thousand
        // do, and a region in this state does not need its log hammered as well.
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref m_LastShedLogMS) > 60000)
        {
            Volatile.Write(ref m_LastShedLogMS, now);
            m_log.WarnFormat(
                "[FRIENDS SIM CONNECTOR]: dropped a friends status notification to {0}: {1} already pending for {2} senders ({3} dropped in total). Online status elsewhere will lag until viewers refresh.",
                uri, m_StatusNotifyQueueDepth, m_MaxConcurrentStatusNotifies, shed);
        }
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

    /// <summary>
    /// One POST. True when the far sim gave a well-formed answer, false when it gave none.
    /// </summary>
    /// <remarks>
    /// The RESULT value is deliberately not examined. "false" is a definitive answer - that
    /// friend is not on this sim - and retrying a definitive answer only burns the queue.
    /// Only the ABSENCE of an answer is worth another attempt.
    ///
    /// This used to return void and swallow every failure at Debug level, which is how a
    /// timed-out notification became an invisible event: the friends list was simply wrong
    /// afterwards and nothing above Debug said why.
    /// </remarks>
    private static bool Post(string uri, string reqString, int timeoutsecs)
    {
        try
        {
            string reply = SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, timeoutsecs, null, false);

            if (string.IsNullOrEmpty(reply))
            {
                m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: received empty reply from {0}", uri);
                return false;
            }

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            if (replyData.ContainsKey("RESULT"))
                return true;

            m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: reply from {0} has no result field", uri);
            return false;
        }
        catch (Exception e)
        {
            m_log.DebugFormat("[FRIENDS SIM CONNECTOR]: Exception when contacting remote sim at {0}: {1}", uri, e.Message);
            return false;
        }
    }
}
