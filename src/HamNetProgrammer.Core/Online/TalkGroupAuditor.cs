using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Online;

public sealed record TalkGroupAuditFinding(string Kind, long DmrId, string LocalName, IReadOnlyList<string> Details);

/// <summary>
/// Read-only sanity check for suspected miscoded/stale talkgroups - answers "if a talkgroup was
/// carried over wrong (e.g. from a Brandmeister-era label that doesn't mean the same thing on
/// FreeDMR), how would we know?" Flags a Contact whose own name doesn't match what any of the
/// three networks call that DmrId (e.g. locally "Ireland", but every network calls TG2354
/// "Chat 4") - confirmed against real data, 2026-07-22.
///
/// A second check was tried and dropped the same day: comparing each CHANNEL's own name against
/// its linked contact's name (meant to catch a channel wired to the wrong-but-validly-named
/// contact, e.g. "nljstar-astro" pointing at "TG1 Local" - a pattern this check can't see since
/// "TG1 Local" isn't itself mislabeled). Too noisy against this project's zone-suffix abbreviation
/// convention ("Eastmids" for "East Midlands", "NEng" for "North East") to be useful - 453
/// findings, mostly false positives, no substring-based heuristic recognized the abbreviations.
///
/// Advisory only - fuzzy name matching can't be authoritative, and a real mismatch might still be
/// intentional. Meant to be reviewed by a human, not auto-fixed - unlike DuplicateTalkGroupMerger,
/// there's no safe automatic resolution here.
/// </summary>
public static class TalkGroupAuditor
{
    private static readonly Regex LeadingTgId = new(@"^TG\d+\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingTgId = new(@"\s*\(TG\d+\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<TalkGroupAuditFinding> AuditAgainstNetwork(SqliteConnection db, IReadOnlyList<NetworkTalkGroup> networkData)
    {
        var byDmrId = networkData.GroupBy(t => t.DmrId).ToDictionary(g => g.Key, g => g.ToList());
        var findings = new List<TalkGroupAuditFinding>();

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DmrId FROM Contacts WHERE CallType = 'Group' AND DmrId IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var dmrId = reader.GetInt64(2);

            if (!byDmrId.TryGetValue(dmrId, out var matches))
            {
                findings.Add(new TalkGroupAuditFinding("NotFoundUpstream", dmrId, name,
                    ["This DmrId doesn't appear in Brandmeister, TGIF, or FreeDMR's published lists - could be a private/regional reflector, or a typo'd number."]));
                continue;
            }

            var normalizedLocal = Normalize(name);
            var anyMatch = matches.Any(m =>
            {
                var normalizedNetwork = Normalize(m.Name);
                return normalizedNetwork.Contains(normalizedLocal) || normalizedLocal.Contains(normalizedNetwork);
            });
            if (!anyMatch)
            {
                findings.Add(new TalkGroupAuditFinding("NameMismatch", dmrId, name,
                    matches.Select(m => $"{m.Network} calls TG{dmrId} '{m.Name}'").ToList()));
            }
        }

        return findings;
    }

    private static string Normalize(string s)
    {
        s = LeadingTgId.Replace(s, "");
        s = TrailingTgId.Replace(s, "");
        return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
