using log4net;
using System.Reflection;
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

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.ScriptEngine.Interfaces;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins;

public class HttpRequest
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    public AsyncCommandManager m_CmdManager;

    public HttpRequest(AsyncCommandManager CmdManager)
    {
        m_CmdManager = CmdManager;
    }

    // DIAGNOSTIC (#96): AsyncCommandManager wraps CheckHttpRequests in `catch { }`,
    // so an InvalidCastException here would lose the result AFTER dequeuing it,
    // silently and with no trace. Two HttpRequestClass types in different
    // AssemblyLoadContexts would do exactly that.
    private static HttpRequestClass Dequeue(IHttpRequestModule m)
    {
        IServiceRequest raw = m.GetNextCompletedRequest();
        if (raw is null)
            return null;
        m_log.Info($"[HTTP POLL]: dequeued type={raw.GetType().FullName}");
        m_log.Info($"[HTTP POLL]:   got asm={raw.GetType().Assembly.FullName}");
        m_log.Info($"[HTTP POLL]:   exp asm={typeof(HttpRequestClass).Assembly.FullName}");
        m_log.Info($"[HTTP POLL]:   castable={raw is HttpRequestClass}");
        return raw as HttpRequestClass;
    }

    public void CheckHttpRequests()
    {
        if (m_CmdManager.m_ScriptEngine.World == null)
            return;

        IHttpRequestModule iHttpReq = m_CmdManager.m_ScriptEngine.World.RequestModuleInterface<IHttpRequestModule>();
        if(iHttpReq == null)
            return;

        HttpRequestClass httpInfo = Dequeue(iHttpReq);
        while (httpInfo != null)
        {
            //m_log.LogDebug("[AsyncLSL]:" + httpInfo.response_body + httpInfo.status);

            // Deliver data to prim's remote_data handler
            //
            // TODO: Returning null for metadata, since the lsl function
            // only returns the byte for HTTP_BODY_TRUNCATED, which is not
            // implemented here yet anyway.  Should be fixed if/when maxsize
            // is supported

            object[] resobj = new object[]
            {
                new LSL_Types.LSLString(httpInfo.ReqID.ToString()),
                new LSL_Types.LSLInteger(httpInfo.Status),
                new LSL_Types.list(),
                new LSL_Types.LSLString(httpInfo.ResponseBody)
            };

            m_log.Info($"[HTTP POLL]: delivering reqID={httpInfo.ReqID} status={httpInfo.Status} localID={httpInfo.LocalID}");
            foreach (IScriptEngine e in m_CmdManager.ScriptEngines)
            {
                if (e.PostObjectEvent(httpInfo.LocalID,
                        new EventParams("http_response",
                        resobj, new DetectParams[0])))
                    break;
            }
            httpInfo = Dequeue(iHttpReq);
        }
    }
}
