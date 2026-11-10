using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdoPrView;

public sealed partial class PrService
{
    readonly ConcurrentDictionary<string, byte> _reviewVoteRequests = new(StringComparer.OrdinalIgnoreCase);
    const string MinimumReviewersTypeId = "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd";

    static JsonNode? LatestReviewIteration(JsonNode iterations) =>
        Arr(iterations["value"]).OrderByDescending(i => I(i["id"]) ?? 0).FirstOrDefault();

    void SetReviewVote(PrView view, JsonNode pr, JsonNode iterations, JsonNode evaluations, string meId)
    {
        var me = view.Reviewers.FirstOrDefault(r => r.Id?.Equals(meId, StringComparison.OrdinalIgnoreCase) == true);
        var name = _me.TryGetValue(view.Org, out var identity) && identity.Id.Equals(meId, StringComparison.OrdinalIgnoreCase)
            ? identity.Name : me?.Name ?? meId;
        var iteration = LatestReviewIteration(iterations);
        var commit = S(iteration?["sourceRefCommit"]?["commitId"]);
        var source = S(pr["sourceRefName"]);
        var target = S(pr["targetRefName"]);
        var ownReviewer = Arr(pr["reviewers"]).FirstOrDefault(r => S(r["id"]).Equals(meId, StringComparison.OrdinalIgnoreCase));
        var vote = pr["reviewers"] is JsonArray ? ownReviewer is null ? 0 : I(ownReviewer["vote"]) : null;
        string? unavailable = !Guid.TryParse(meId, out var userId) || userId == Guid.Empty || me?.IsContainer == true
            ? "ADO did not identify an individual signed-in reviewer."
            : !ReviewMonitored(view) ? "This PR is inactive, hidden or no longer monitored."
            : vote is null ? "ADO did not return the current review state. Refresh before approving."
            : !Regex.IsMatch(commit, @"\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\z") ||
                !Regex.IsMatch(view.CurrentTargetCommit ?? "", @"\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\z") ||
                I(iteration?["id"]) is not > 0 || !source.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                !target.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                !commit.Equals(S(pr["lastMergeSourceCommit"]?["commitId"]), StringComparison.OrdinalIgnoreCase)
                ? "ADO has not confirmed the current PR revision. Refresh before approving." : null;
        var notes = new List<string>();
        foreach (var ev in Arr(evaluations["value"]))
        {
            var cfg = ev["configuration"];
            if (cfg is null || !B(cfg["isBlocking"]) || cfg["isEnabled"]?.GetValue<bool>() == false ||
                !S(cfg["type"]?["id"]).Equals(MinimumReviewersTypeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (view.Author.Id?.Equals(meId, StringComparison.OrdinalIgnoreCase) == true &&
                cfg["settings"]?["creatorVoteCounts"]?.GetValue<bool>() == false)
                notes.Add("Your author vote does not count toward the minimum required reviewers.");
            if (B(cfg["settings"]?["blockLastPusherVote"]))
                notes.Add("ADO does not count the most recent pusher's vote toward required approvals.");
        }
        notes.Add("This is only your ordinary ADO reviewer vote. Other reviews, policies and PoP may still be required; ADO enforces your permissions.");
        view.ReviewVote = new(meId, name, vote, unavailable is null && vote != 10,
            unavailable ?? string.Join(" ", notes.Distinct()),
            unavailable is null ? new(meId, commit, view.CurrentTargetCommit!, source, target, I(iteration!["id"])!.Value, vote!.Value) : null);
    }

    static bool SameReviewRevision(ReviewVoteTarget actual, ReviewVoteTarget expected) =>
        actual with { Vote = expected.Vote } == expected;

    static void CheckReviewIdentity(PrView scope, JsonNode pr)
    {
        if (I(pr["pullRequestId"]) != scope.Id ||
            !S(pr["repository"]?["id"]).Equals(scope.RepoId, StringComparison.OrdinalIgnoreCase) ||
            !S(pr["repository"]?["project"]?["id"]).Equals(scope.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ADO did not confirm the selected PR identity. No approval was confirmed.");
    }

    bool ReviewMonitored(PrView scope) =>
        Monitored(scope) && scope.Error is null &&
        (scope.IsMine || MatchesWhitelist(scope.Author.Name, scope.Author.UniqueName ?? "", _store.Current.Whitelist)) &&
        _store.Current.Repos.Any(r => r.Key == $"{scope.Org}/{scope.Project}/{scope.Repo}".ToLowerInvariant() &&
                string.Equals(r.RepoId, scope.RepoId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ProjectId, scope.ProjectId, StringComparison.OrdinalIgnoreCase));

    void CheckReviewMonitored(PrView scope)
    {
        if (!ReviewMonitored(scope))
            throw new InvalidOperationException("This PR is inactive, hidden or no longer monitored. Refresh before approving.");
    }

    async Task VerifyReviewRevisionAsync(PrView scope, ReviewVoteTarget expected)
    {
        var prTask = _ado.GetAsync(PrApi(scope));
        var iterationsTask = _ado.GetAsync($"{Api(scope.Org, scope.Project)}git/repositories/{Esc(scope.RepoId)}/pullrequests/{scope.Id}/iterations?api-version=7.1");
        var refsTask = _ado.GetAsync($"{Api(scope.Org, scope.Project)}git/repositories/{Esc(scope.RepoId)}/refs?filter={Esc(expected.SourceRef["refs/".Length..])}&api-version=7.1");
        var targetTask = _ado.GetAsync($"{Api(scope.Org, scope.Project)}git/repositories/{Esc(scope.RepoId)}/refs?filter={Esc(expected.TargetRef["refs/".Length..])}&api-version=7.1");
        await Task.WhenAll(prTask, iterationsTask, refsTask, targetTask);
        var pr = await prTask;
        CheckReviewIdentity(scope, pr);
        if (S(pr["status"]) != "active")
            throw new InvalidOperationException("This PR is inactive. Refresh before approving.");
        var latest = LatestReviewIteration(await iterationsTask);
        var reviewer = Arr(pr["reviewers"]).FirstOrDefault(r => S(r["id"]).Equals(expected.ReviewerId, StringComparison.OrdinalIgnoreCase));
        if (pr["reviewers"] is not JsonArray || reviewer is not null && (B(reviewer["isContainer"]) || I(reviewer["vote"]) is null) ||
            (reviewer is null ? 0 : I(reviewer["vote"])) != expected.Vote)
            throw new InvalidOperationException("Your recorded review vote changed or could not be verified. Refresh before approving.");
        var head = Arr((await refsTask)["value"]).SingleOrDefault(r => S(r["name"]) == expected.SourceRef);
        var target = Arr((await targetTask)["value"]).SingleOrDefault(r => S(r["name"]) == expected.TargetRef);
        if (S(pr["sourceRefName"]) != expected.SourceRef || S(pr["targetRefName"]) != expected.TargetRef ||
            I(latest?["id"]) != expected.IterationId ||
            !S(latest?["sourceRefCommit"]?["commitId"]).Equals(expected.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !S(pr["lastMergeSourceCommit"]?["commitId"]).Equals(expected.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !S(head?["objectId"]).Equals(expected.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !S(target?["objectId"]).Equals(expected.TargetCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The PR source revision, iteration or target changed. Refresh and review the current changes before approving.");
        CheckReviewMonitored(scope);
    }

    public async Task<ReviewVoteResult> ApproveAsync(string key, ReviewVoteTarget expected)
    {
        if (!_reviewVoteRequests.TryAdd(key, 0))
            throw new InvalidOperationException("An approval vote is already pending for this PR.");
        try
        {
            await _refreshGate.WaitAsync();
            try
            {
                var captured = Find(key);
                CheckReviewMonitored(captured);
                if (captured.ReviewVote?.Target != expected)
                    throw new InvalidOperationException("The captured PR revision or your vote changed. Refresh and review before approving.");
                var identity = (await _ado.GetAsync($"https://dev.azure.com/{Esc(captured.Org)}/_apis/connectionData"))["authenticatedUser"];
                var meId = S(identity?["id"]);
                if (!Guid.TryParse(meId, out var userId) || userId == Guid.Empty || B(identity?["isContainer"]) ||
                    !meId.Equals(expected.ReviewerId, StringComparison.OrdinalIgnoreCase))
                {
                    _me.TryRemove(captured.Org, out _);
                    throw new InvalidOperationException("The signed-in ADO reviewer changed or could not be verified. Refresh before approving.");
                }
                _me[captured.Org] = (meId, S(identity?["providerDisplayName"]));
                var repo = _store.Current.Repos.Single(r => r.Key == $"{captured.Org}/{captured.Project}/{captured.Repo}".ToLowerInvariant());
                var target = new PopTarget(captured.Key, captured.Id, captured.Title, captured.Org, captured.Project,
                    captured.ProjectId, captured.Repo, captured.RepoId);
                var fresh = await BuildAsync(repo, captured.Id, meId, new(), target);
                CheckReviewMonitored(fresh);
                var current = fresh.ReviewVote?.Target;
                if (current is null || !SameReviewRevision(current, expected))
                    throw new InvalidOperationException("The PR revision or review identity changed. Refresh and review before approving.");
                if (current.Vote == 10)
                {
                    await VerifyReviewRevisionAsync(fresh, current);
                    ApplyMergeCheckState(fresh, fresh: true);
                    Classify(fresh);
                    ReplaceOne(fresh);
                    return new(true, true, "Your ADO vote is already Approved. Other review requirements may remain.", fresh);
                }
                if (current.Vote != expected.Vote || fresh.ReviewVote?.CanApprove != true)
                    throw new InvalidOperationException("Your review state changed. Refresh before approving.");
                await VerifyReviewRevisionAsync(fresh, expected);
                var reviewerUrl = $"{Api(fresh.Org, fresh.Project)}git/repositories/{Esc(fresh.RepoId)}/pullrequests/{fresh.Id}/reviewers/{userId:D}?api-version=7.1";
                try
                {
                    // This API has no revision compare-and-set. Fence both sides of the
                    // one deliberate write, and never retry or roll back an ambiguous vote.
                    await _ado.PutOnceAsync(reviewerUrl, new JsonObject { ["id"] = meId, ["vote"] = 10 });
                    var recorded = await _ado.GetAsync(reviewerUrl);
                    if (!S(recorded["id"]).Equals(meId, StringComparison.OrdinalIgnoreCase) ||
                        B(recorded["isContainer"]) || I(recorded["vote"]) != 10)
                        throw new InvalidOperationException("ADO did not read back your Approved vote.");
                    var updated = await BuildAsync(repo, captured.Id, meId, new(), target);
                    CheckReviewMonitored(updated);
                    if (updated.ReviewVote?.Target is not { Vote: 10 } confirmed || !SameReviewRevision(confirmed, expected))
                        throw new InvalidOperationException("The PR revision or recorded vote changed during approval.");
                    await VerifyReviewRevisionAsync(updated, expected with { Vote = 10 });
                    ApplyMergeCheckState(updated, fresh: true);
                    Classify(updated);
                    ReplaceOne(updated);
                    return new(true, false, "Your ADO vote is Approved. This does not confirm other reviews, policy completion, merge permission or PoP.", updated);
                }
                catch (Exception ex) when (ex is AdoException or HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw new InvalidOperationException("Approval not confirmed. ADO may have recorded your vote; refresh and check the PR before retrying. " + ex.Message, ex);
                }
            }
            finally { _refreshGate.Release(); }
        }
        finally { _reviewVoteRequests.TryRemove(key, out _); }
    }
}
