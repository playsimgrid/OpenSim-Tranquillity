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

using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.Profiles;

public class UserProfilesConnector: ServiceConnector
{
//        static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    // Our Local Module
    public IUserProfilesService ServiceModule
    {
        get; private set;
    }

    // The HTTP server.
    public IHttpServer Server
    {
        get; private set;
    }

    public bool Enabled
    {
        get; private set;
    }

    public UserProfilesConnector(IConfigSource config, IHttpServer server, string configName) :
        base(config, server, configName)
    {
        ConfigName = "UserProfilesService";
        if(!string.IsNullOrEmpty(configName))
            ConfigName = configName;

        IConfig serverConfig = config.Configs[ConfigName];
        if (serverConfig == null)
            throw new Exception(String.Format("No section {0} in config file", ConfigName));

        if(!serverConfig.GetBoolean("Enabled",false))
        {
            Enabled = false;
            return;
        }

        Enabled = true;

        Server = server;

        string service = serverConfig.GetString("LocalServiceModule", String.Empty);

        Object[] args = new Object[] { config, ConfigName };
        ServiceModule = ServerUtils.LoadPlugin<IUserProfilesService>(service, args);

        JsonRpcProfileHandlers handler = new JsonRpcProfileHandlers(ServiceModule);

        // SECURITY: this dispatch has no notion of an authenticated caller, so the sensitive
        // methods sat wide open next to the legitimately public ones. A blanket gate is WRONG:
        // foreign grids genuinely read a local user's public profile, picks and classifieds
        // over the hypergrid, and refusing those breaks cross-grid profile viewing.
        //
        // So the split below is by what the method exposes, not by read vs write:
        //
        //   public        - the profile, picks and classifieds a foreign grid may display
        //   trusted-only  - the email address, the user's PRIVATE notes, the app-data store,
        //                   and every write or delete of profile content
        //
        // Note avatarnotesrequest and user_preferences_request are READS and are still gated:
        // one returns private notes, the other returns the email address. The owner's own
        // viewer reaches these from inside a trusted region, so that keeps working.

        // Public: cross-grid profile viewing.
        Server.AddJsonRPCHandler("avatarclassifiedsrequest", handler.AvatarClassifiedsRequest);
        Server.AddJsonRPCHandler("classifieds_info_query", handler.ClassifiedInfoRequest);
        Server.AddJsonRPCHandler("avatarpicksrequest", handler.AvatarPicksRequest);
        Server.AddJsonRPCHandler("pickinforequest", handler.PickInfoRequest);
        Server.AddJsonRPCHandler("avatar_properties_request", handler.AvatarPropertiesRequest);
        Server.AddJsonRPCHandler("image_assets_request", handler.AvatarImageAssetsRequest);

        // Trusted-only: private data, and every mutation.
        Server.AddJsonRPCHandler("classified_update", handler.ClassifiedUpdate, true);
        Server.AddJsonRPCHandler("classified_delete", handler.ClassifiedDelete, true);
        Server.AddJsonRPCHandler("picks_update", handler.PicksUpdate, true);
        Server.AddJsonRPCHandler("picks_delete", handler.PicksDelete, true);
        Server.AddJsonRPCHandler("avatarnotesrequest", handler.AvatarNotesRequest, true);
        Server.AddJsonRPCHandler("avatar_notes_update", handler.NotesUpdate, true);
        Server.AddJsonRPCHandler("avatar_properties_update", handler.AvatarPropertiesUpdate, true);
        Server.AddJsonRPCHandler("avatar_interests_update", handler.AvatarInterestsUpdate, true);
        Server.AddJsonRPCHandler("user_preferences_update", handler.UserPreferenecesUpdate, true);
        Server.AddJsonRPCHandler("user_preferences_request", handler.UserPreferencesRequest, true);
        Server.AddJsonRPCHandler("user_data_request", handler.RequestUserAppData, true);
        Server.AddJsonRPCHandler("user_data_update", handler.UpdateUserAppData, true);
    }
}