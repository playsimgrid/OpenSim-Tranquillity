/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using System;
using System.Collections.Generic;

using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// Where inventory thumbnails come from, if anything provides them.
    ///
    /// OpenSimulator has no thumbnails of its own. Viewers do: Firestorm reads a
    /// "thumbnail_id" off every item and folder in an inventory reply and draws
    /// it in the inventory gallery and the Outfit Gallery. This is the one seam
    /// where an addon can supply that id; with no addon installed both delegates
    /// are null and every inventory reply is byte-for-byte what it was before.
    ///
    /// BATCH, NOT PER ITEM. The lookup takes every id in one reply, because a
    /// provider will be asking another service across the network: a folder of
    /// 300 items must cost one question, not 300.
    ///
    /// A provider answers null when it could not find out, which is deliberately
    /// different from an empty map meaning "none of these have one". Callers
    /// write no thumbnail_id in either case, but the provider logs the
    /// difference - a lookup that quietly reads as "no thumbnails" is how every
    /// resident's pictures disappear at once with nothing in the log.
    /// </summary>
    public static class InventoryThumbnails
    {
        /// <summary>Item id -> texture id. Set by the thumbnail addon, if installed.</summary>
        public static Func<UUID[], Dictionary<UUID, UUID>> LookupItems;

        /// <summary>Folder id -> texture id. Set by the thumbnail addon, if installed.</summary>
        public static Func<UUID[], Dictionary<UUID, UUID>> LookupFolders;

        public static bool Installed
        {
            get { return LookupItems != null || LookupFolders != null; }
        }

        public static Dictionary<UUID, UUID> ForItems(UUID[] ids)
        {
            return Ask(LookupItems, ids);
        }

        public static Dictionary<UUID, UUID> ForFolders(UUID[] ids)
        {
            return Ask(LookupFolders, ids);
        }

        /// <summary>
        /// An addon fault must never cost a resident their inventory window, so
        /// the call is guarded: a throw here means no thumbnails in this reply,
        /// not a failed inventory fetch.
        /// </summary>
        private static Dictionary<UUID, UUID> Ask(Func<UUID[], Dictionary<UUID, UUID>> lookup, UUID[] ids)
        {
            if (lookup == null || ids == null || ids.Length == 0)
                return null;

            try
            {
                return lookup(ids);
            }
            catch
            {
                return null;
            }
        }
    }
}
