using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace AdoPrView;

public sealed partial class PrService
{
    sealed record MergeTarget(string RepoKey, string Repo, string Api, string RepoId, string Ref)
    {
        public string Key => $"{Api}{RepoId}/{Ref}";
    }

    sealed class MergeCheck
    {
        public DateTimeOffset? RequestedAt;
        public DateTimeOffset NextAttempt;
        public bool Awaiting;
        public bool DetectionFailed;
        public string? Error;
    }

    readonly ConcurrentDictionary<string, MergeTarget> _mergeTargets = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _observedMergeHeads = new(StringComparer.Ordinal);
    readonly Dictionary<string, MergeCheck> _mergeChecks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _mergeSweepHandled = new(StringComparer.OrdinalIgnoreCase);
    long _mergeSweep;

    static bool MergeInFlight(string status) => status is "queued" or "notSet";
    static bool MergeConflicted(string status) => status == "conflicts";
    static bool AutomaticMergeCheckEligible(PrView pr) => !MergeConflicted(pr.MergeStatus) && !MergeInFlight(pr.MergeStatus);
    TimeSpan MergeCadence => TimeSpan.FromMinutes(Math.Max(1, _store.Current.MergeCheckMinutes));
    bool Monitored(PrView pr) => pr.IsActive && !pr.IsHidden &&
        !_store.Current.Hidden.Contains(pr.Key, StringComparer.OrdinalIgnoreCase) &&
        _store.Current.Repos.Any(r => r.Key == $"{pr.Org}/{pr.Project}/{pr.Repo}".ToLowerInvariant());

    static void SetMergeStaleness(PrView pr) =>
        pr.MergeStale = pr.CurrentTargetCommit is { Length: > 0 } head &&
            !head.Equals(pr.LastMergeTargetCommit, StringComparison.OrdinalIgnoreCase);

    Task<string> TargetHeadAsync(RepoRef repo, string api, string repoId, string targetRef,
        ConcurrentDictionary<string, Lazy<Task<string>>> heads)
    {
        var target = new MergeTarget(repo.Key, repo.Repo, api, repoId, targetRef);
        _mergeTargets[target.Key] = target;
        return CachedHead(target, heads);
    }

    Task<string> CachedHead(MergeTarget target, ConcurrentDictionary<string, Lazy<Task<string>>> heads) =>
        heads.GetOrAdd(target.Key, _ => new(() => GetHeadAsync(target.Api, target.RepoId, target.Ref))).Value;

    MergeCheck CheckFor(PrView pr)
    {
        if (!_mergeChecks.TryGetValue(pr.Key, out var check)) _mergeChecks[pr.Key] = check = new();
        return check;
    }

    void ApplyMergeCheckState(PrView pr, bool fresh = false)
    {
        var check = CheckFor(pr);
        if (pr.MergeCheckError is not null) check.DetectionFailed = true;
        if (MergeConflicted(pr.MergeStatus))
        {
            check.Awaiting = false;
            _mergeSweepHandled[pr.Key] = _mergeSweep;
        }
        else if (fresh && check.Awaiting && !MergeInFlight(pr.MergeStatus))
        {
            if (pr.CurrentTargetCommit is not null && !pr.MergeStale) check.Awaiting = false;
            else if (_time.GetUtcNow() >= check.NextAttempt)
            {
                check.Awaiting = false;
                check.Error = "ADO has not returned a preview for the current target.";
            }
        }
        pr.MergeRecheckRequestedAt = check.RequestedAt;
        pr.MergeRecheckPending = check.Awaiting;
        pr.MergeRecheckError = check.Error is null ? null : check.Error + (MergeConflicted(pr.MergeStatus)
            ? " Automatic re-checks are paused while ADO reports conflicts."
            : $" Retrying on a later refresh (not before {check.NextAttempt:u}).");
    }

    async Task<string> RecheckMergesAsync(List<PrView> prs, ConcurrentDictionary<string, Lazy<Task<string>>> heads,
        ConcurrentBag<string> errors, bool force)
    {
        var repoKeys = _store.Current.Repos.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = false;
        // Retain known target refs even when the last watched PR disappears: its
        // merge (including one by an unwatched author) still advances the ref.
        var observations = await Task.WhenAll(_mergeTargets.Values.Select(async target =>
        {
            if (!repoKeys.Contains(target.RepoKey))
            {
                _mergeTargets.TryRemove(target.Key, out _);
                return (target.Key, Head: (string?)null, Removed: true);
            }
            try { return (target.Key, Head: (string?)await CachedHead(target, heads), Removed: false); }
            catch (Exception ex)
            {
                errors.Add($"Merge target {target.Repo}/{Branch(target.Ref)}: {ex.Message}");
                return (target.Key, Head: (string?)null, Removed: false);
            }
        }));
        foreach (var observation in observations)
        {
            if (observation.Removed) { _observedMergeHeads.Remove(observation.Key); continue; }
            if (observation.Head is null) continue;
            if (_observedMergeHeads.TryGetValue(observation.Key, out var old) &&
                !old.Equals(observation.Head, StringComparison.OrdinalIgnoreCase)) moved = true;
            _observedMergeHeads[observation.Key] = observation.Head;
        }
        if (force || moved) _mergeSweep++;

        var now = _time.GetUtcNow();
        var periodic = now - _lastMergeCheck >= MergeCadence;
        var requested = 0;
        var deferredSweep = false;
        var retried = false;
        // Sequential writes plus AdoClient's shared throttle bound the sweep.
        foreach (var pr in prs.Where(p => p.Error is null && Monitored(p)))
        {
            ApplyMergeCheckState(pr, fresh: true);
            var check = CheckFor(pr);
            var sweep = _mergeSweepHandled.GetValueOrDefault(pr.Key) < _mergeSweep;
            var retry = check.Error is not null && now >= check.NextAttempt;
            var recovered = check.DetectionFailed && pr.MergeCheckError is null;
            if (AutomaticMergeCheckEligible(pr) && !pr.MergeRecheckPending &&
                (check.Error is null || now >= check.NextAttempt) &&
                (sweep || retry || pr.MergeStale && (periodic || recovered)))
            {
                try
                {
                    if ((await RequestMergeCheckAsync(pr, automatic: true)).Requested)
                    {
                        requested++;
                        deferredSweep |= sweep;
                        retried |= retry;
                    }
                }
                catch (Exception ex) { RecordMergeFailure(pr, ex); }
            }
            if (pr.MergeCheckError is null) check.DetectionFailed = false;
            ApplyMergeCheckState(pr);
            Classify(pr);
        }
        if (periodic) _lastMergeCheck = now;
        var visible = prs.Where(p => p.Error is null && Monitored(p)).ToList();
        var pending = visible.Count(p => p.MergeRecheckPending || MergeInFlight(p.MergeStatus));
        var failed = visible.Count(p => p.MergeRecheckError is not null || p.MergeCheckError is not null);
        var reason = moved ? "target branch advanced" : force ? "forced sweep" : deferredSweep ? "deferred sweep" : retried ? "retry" : "scheduled stale checks";
        return $"Merge re-check: {requested} requested ({reason}), {pending} checking, {failed} with re-check/target errors";
    }

    void RecordMergeFailure(PrView pr, Exception ex)
    {
        var check = CheckFor(pr);
        check.Awaiting = false;
        check.NextAttempt = _time.GetUtcNow().AddSeconds(Math.Max(30, _store.Current.RefreshSeconds));
        check.Error = $"Merge re-check failed: {ex.Message}.";
    }

    static void ReadMergeFields(PrView pr, JsonNode json)
    {
        if (json["mergeStatus"] is not null) pr.MergeStatus = S(json["mergeStatus"]);
        if (json["status"] is not null) pr.IsActive = S(json["status"]) == "active";
        if (json.AsObject().ContainsKey("lastMergeTargetCommit")) pr.LastMergeTargetCommit = S(json["lastMergeTargetCommit"]?["commitId"]);
        pr.MergeFailure = json["mergeFailureMessage"] is null ? null : S(json["mergeFailureMessage"]);
        SetMergeStaleness(pr);
    }

    async Task<MergeRecheckResult> RequestMergeCheckAsync(PrView pr, bool automatic = false)
    {
        if (!Monitored(pr)) return new(false, $"!{pr.Id} is hidden or no longer monitored; no merge re-check requested.");
        var cur = await _ado.GetAsync(PrApi(pr));
        if (string.IsNullOrEmpty(S(cur["status"]))) throw new AdoException("ADO did not return the PR's active status.");
        ReadMergeFields(pr, cur);
        if (!pr.IsActive) return new(false, $"!{pr.Id} is no longer active; no merge re-check requested.");
        if (MergeInFlight(pr.MergeStatus)) return new(false, $"!{pr.Id} already has a merge evaluation in progress; it was not restarted.");
        if (automatic && !AutomaticMergeCheckEligible(pr))
        {
            ApplyMergeCheckState(pr, fresh: true);
            return new(false, $"!{pr.Id} has merge conflicts; automatic re-checks are paused.");
        }
        // Do not retry a write inside HttpClient: the next refresh revalidates
        // active/in-flight state before an explicit, observable retry.
        var mergeOptions = cur["mergeOptions"]?.DeepClone() ?? new JsonObject();
        var result = await _ado.PatchOnceAsync(PrApi(pr), new JsonObject { ["mergeOptions"] = mergeOptions });
        ReadMergeFields(pr, result);
        var check = CheckFor(pr);
        check.RequestedAt = _time.GetUtcNow();
        check.NextAttempt = check.RequestedAt.Value + MergeCadence;
        check.Awaiting = true;
        check.Error = null;
        _mergeSweepHandled[pr.Key] = _mergeSweep;
        return new(true, $"Merge re-check requested for !{pr.Id}; awaiting ADO's result.");
    }

    public async Task<MergeRecheckResult> RestartMergeAsync(string key)
    {
        await _refreshGate.WaitAsync();
        try
        {
            var existing = Find(key);
            if (!Monitored(existing)) return new(false, $"!{existing.Id} is hidden or no longer monitored; no merge re-check requested.");
            var repo = _store.Current.Repos.First(r => r.Key == $"{existing.Org}/{existing.Project}/{existing.Repo}".ToLowerInvariant());
            var pr = await BuildAsync(repo, existing.Id, (await GetMeAsync(repo.Org)).Id, new());
            ApplyMergeCheckState(pr, fresh: true);
            MergeRecheckResult result;
            try
            {
                result = pr.MergeRecheckPending || MergeInFlight(pr.MergeStatus)
                    ? new(false, $"!{pr.Id} already has a merge evaluation in progress; it was not restarted.")
                    : await RequestMergeCheckAsync(pr);
            }
            catch (Exception ex)
            {
                RecordMergeFailure(pr, ex);
                ApplyMergeCheckState(pr);
                Classify(pr);
                ReplaceOne(pr);
                throw new InvalidOperationException(pr.MergeRecheckError, ex);
            }
            ApplyMergeCheckState(pr);
            Classify(pr);
            ReplaceOne(pr);
            return result;
        }
        finally { _refreshGate.Release(); }
    }
}
