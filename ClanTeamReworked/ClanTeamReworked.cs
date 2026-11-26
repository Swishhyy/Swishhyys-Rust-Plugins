// Requires: Clans

using Newtonsoft.Json.Linq;
using Oxide.Core.Plugins;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Clan Team Reworked", "deivismac", "1.0.8")]
    [Description("Adds clan members to the same team")]
    class ClanTeamReworked : CovalencePlugin
    {
        #region Definitions

        [PluginReference]
        private Plugin Clans;

        // Clan tag -> online members currently placed on that clan's team.
        private readonly Dictionary<string, List<ulong>> clans = new Dictionary<string, List<ulong>>();

        // Clan tag -> ID of the team this plugin created for that clan.
        private readonly Dictionary<string, ulong> clanTeams = new Dictionary<string, ulong>();

        #endregion Definitions

        #region Functions

        // True when every online member of the clan is already on the given team.
        private bool CompareTeams(List<ulong> currentIds, List<ulong> onlineClanIds)
        {
            foreach (ulong clanId in onlineClanIds)
            {
                if (!currentIds.Contains(clanId))
                {
                    return false;
                }
            }

            return true;
        }

        private void GenerateClanTeam(string tag, List<ulong> memberIds)
        {
            if (string.IsNullOrEmpty(tag) || memberIds == null || memberIds.Count == 0)
            {
                return;
            }

            // Remove the old team for this clan so teams don't pile up.
            DisbandClanTeam(tag);

            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.CreateTeam();
            if (team == null)
            {
                return;
            }

            string ownerId = ClanOwner(tag);
            var onlineMembers = new List<ulong>();

            foreach (ulong memberId in memberIds)
            {
                BasePlayer player = BasePlayer.FindByID(memberId);
                if (player == null)
                {
                    continue;
                }

                if (player.currentTeam != 0UL)
                {
                    RelationshipManager.PlayerTeam current = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                    current?.RemovePlayer(player.userID);
                }

                team.AddPlayer(player);
                onlineMembers.Add(player.userID);

                if (player.UserIDString == ownerId)
                {
                    team.SetTeamLeader(player.userID);
                }
            }

            clans[tag] = onlineMembers;
            clanTeams[tag] = team.teamID;
        }

        // Removes a clan's plugin-created team and its members from it.
        private void DisbandClanTeam(string tag)
        {
            if (string.IsNullOrEmpty(tag) || !clanTeams.TryGetValue(tag, out ulong teamId))
            {
                return;
            }

            clanTeams.Remove(tag);
            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(teamId);
            if (team != null)
            {
                foreach (ulong memberId in new List<ulong>(team.members))
                {
                    team.RemovePlayer(memberId);
                }
                RelationshipManager.ServerInstance.DisbandTeam(team);
            }

            clans.Remove(tag);
        }

        private JObject GetClanInfo(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }
            return Clans?.Call<JObject>("GetClan", tag);
        }

        private string ClanOwner(string tag)
        {
            return GetClanInfo(tag)?["owner"]?.ToString();
        }

        private string ClanTag(ulong memberId)
        {
            return Clans?.Call<string>("GetClanOf", memberId);
        }

        // All clan members (online or not), parsed from the Clans data.
        private List<ulong> ClanMembers(string tag)
        {
            var result = new List<ulong>();
            if (GetClanInfo(tag)?["members"] is JArray players)
            {
                foreach (JToken token in players)
                {
                    if (ulong.TryParse(token.ToString(), out ulong id) && id != 0UL)
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }

        #endregion Functions

        #region Hooks

        private void OnClanCreate(string tag)
        {
            // Clans may not have stored the members yet, so wait a moment.
            timer.Once(1f, () => GenerateClanTeam(tag, ClanMembers(tag)));
        }

        private void OnClanUpdate(string tag)
        {
            GenerateClanTeam(tag, ClanMembers(tag));
        }

        private void OnClanDestroy(string tag)
        {
            DisbandClanTeam(tag);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            string tag = ClanTag(player.userID);
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            List<ulong> members = ClanMembers(tag);

            // Only online members can be on the team, so compare against them.
            var onlineIds = new List<ulong>();
            foreach (ulong id in members)
            {
                if (BasePlayer.FindByID(id) != null)
                {
                    onlineIds.Add(id);
                }
            }

            if (player.currentTeam != 0UL)
            {
                RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && CompareTeams(team.members, onlineIds))
                {
                    return;
                }
            }

            GenerateClanTeam(tag, members);
        }

        // When someone enters the correct code on a lock, whitelist their clanmates
        // and the lock owner's clanmates so they can open it without the code.
        private void OnCodeEntered(CodeLock codeLock, BasePlayer player, string code)
        {
            if (codeLock == null || player == null || codeLock.code != code)
            {
                return;
            }

            string enteringTag = ClanTag(player.userID);
            string ownerTag = codeLock.OwnerID != 0UL ? ClanTag(codeLock.OwnerID) : null;

            WhitelistClanmates(codeLock, enteringTag);
            if (ownerTag != enteringTag)
            {
                WhitelistClanmates(codeLock, ownerTag);
            }
        }

        private void WhitelistClanmates(CodeLock codeLock, string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            foreach (ulong memberId in ClanMembers(tag))
            {
                if (!codeLock.whitelistPlayers.Contains(memberId))
                {
                    codeLock.whitelistPlayers.Add(memberId);
                }
            }
        }

        #endregion Hooks
    }
}
