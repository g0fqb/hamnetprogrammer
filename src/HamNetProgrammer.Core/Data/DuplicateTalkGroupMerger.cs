using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Data;

public sealed record DuplicateTalkGroupGroup(long DmrId, IReadOnlyList<(long Id, string Name, string? Network)> Contacts);

public sealed record MergeResult(long DmrId, long KeptId, string KeptName, IReadOnlyList<long> RemovedIds, int ChannelsRepointed, int GroupListEntriesRepointed);

/// <summary>
/// Finds and merges duplicate Group-call Contacts sharing the same DmrId - the "two SOARC entries"
/// class of problem. NOT something <see cref="Online.TalkGroupNetworkImporter"/> should silently
/// fix itself: that importer deliberately never touches a manually-created contact (Network NULL),
/// by design, to avoid silently overwriting something a user set up by hand - see its own remarks.
/// This is a separate, explicit, user-triggered cleanup instead, for the case that protection
/// creates: a legacy/manual contact and a later network-tagged import both existing for the same
/// real-world talkgroup, splitting search results and (more importantly) splitting which channels
/// reference which row.
/// </summary>
public static class DuplicateTalkGroupMerger
{
    /// <summary>Every DmrId with more than one Group-call Contact row, most-duplicated first.</summary>
    public static List<DuplicateTalkGroupGroup> FindDuplicates(SqliteConnection db)
    {
        var groups = new List<DuplicateTalkGroupGroup>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DmrId, Id, Name, Network FROM Contacts
            WHERE CallType = 'Group' AND DmrId IS NOT NULL
              AND DmrId IN (SELECT DmrId FROM Contacts WHERE CallType = 'Group' AND DmrId IS NOT NULL GROUP BY DmrId HAVING COUNT(*) > 1)
            ORDER BY DmrId, Id;
            """;
        using var reader = cmd.ExecuteReader();
        var current = new List<(long, string, string?)>();
        long currentDmrId = -1;
        void Flush()
        {
            if (current.Count > 0) groups.Add(new DuplicateTalkGroupGroup(currentDmrId, [.. current]));
            current = [];
        }
        while (reader.Read())
        {
            var dmrId = reader.GetInt64(0);
            if (dmrId != currentDmrId) { Flush(); currentDmrId = dmrId; }
            current.Add((reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        Flush();
        return groups;
    }

    /// <summary>Merges one DmrId's duplicate rows into one, repointing every reference first.
    /// Keeps whichever row has a Network tag (it stays fresh via future syncs; a manual/legacy row
    /// with Network NULL does not) - if more than one has a Network tag (two different real
    /// networks sharing this number, a legitimate case the importer itself handles deliberately),
    /// keeps the one with the lowest Id and leaves the rest alone rather than guessing which
    /// network's data is "more right." Runs in a transaction - either the whole merge for this
    /// DmrId applies or none of it does.</summary>
    public static MergeResult? Merge(SqliteConnection db, DuplicateTalkGroupGroup group)
    {
        var networked = group.Contacts.Where(c => c.Network is not null).OrderBy(c => c.Id).ToList();
        if (networked.Count != 1) return null; // ambiguous or nothing to prefer - skip, don't guess

        var keep = networked[0];
        var remove = group.Contacts.Where(c => c.Id != keep.Id).Select(c => c.Id).ToList();
        if (remove.Count == 0) return null;

        using var tx = db.BeginTransaction();
        var channelsRepointed = 0;
        var groupListRepointed = 0;

        foreach (var removeId in remove)
        {
            using (var chCmd = db.CreateCommand())
            {
                chCmd.Transaction = tx;
                chCmd.CommandText = "UPDATE Channels SET ContactId = $keep WHERE ContactId = $remove;";
                chCmd.Parameters.AddWithValue("$keep", keep.Id);
                chCmd.Parameters.AddWithValue("$remove", removeId);
                channelsRepointed += chCmd.ExecuteNonQuery();
            }

            // GroupListContacts has PRIMARY KEY (GroupListId, Position), not a unique constraint on
            // (GroupListId, ContactId) - a plain UPDATE could produce two rows for the same contact
            // in one list if it already had both duplicates as separate members. Delete outright,
            // and only insert a repointed row back if that list doesn't already contain the keeper.
            using (var dupeCheckCmd = db.CreateCommand())
            {
                dupeCheckCmd.Transaction = tx;
                dupeCheckCmd.CommandText = """
                    DELETE FROM GroupListContacts
                    WHERE ContactId = $remove
                      AND GroupListId IN (SELECT GroupListId FROM GroupListContacts WHERE ContactId = $keep);
                    """;
                dupeCheckCmd.Parameters.AddWithValue("$remove", removeId);
                dupeCheckCmd.Parameters.AddWithValue("$keep", keep.Id);
                dupeCheckCmd.ExecuteNonQuery();
            }
            using (var glCmd = db.CreateCommand())
            {
                glCmd.Transaction = tx;
                glCmd.CommandText = "UPDATE GroupListContacts SET ContactId = $keep WHERE ContactId = $remove;";
                glCmd.Parameters.AddWithValue("$keep", keep.Id);
                glCmd.Parameters.AddWithValue("$remove", removeId);
                groupListRepointed += glCmd.ExecuteNonQuery();
            }

            using (var aprsCmd = db.CreateCommand())
            {
                aprsCmd.Transaction = tx;
                aprsCmd.CommandText = "UPDATE RadioSettings SET AprsTalkGroupId = $keep WHERE AprsTalkGroupId = $remove;";
                aprsCmd.Parameters.AddWithValue("$keep", keep.Id);
                aprsCmd.Parameters.AddWithValue("$remove", removeId);
                aprsCmd.ExecuteNonQuery();
            }

            using (var deleteCmd = db.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM Contacts WHERE Id = $id;";
                deleteCmd.Parameters.AddWithValue("$id", removeId);
                deleteCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return new MergeResult(group.DmrId, keep.Id, keep.Name, remove, channelsRepointed, groupListRepointed);
    }
}
