using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AdoPrView;

const string BotId = "00000000-0000-4000-8000-0000000000b0";
var bot = new ReviewBotSettings { Name = "Review bot", AuthorId = BotId };
var tests = 0;

PrView Pr(string repo = "sample-repo", string org = "contoso", string project = "Sample", int id = 123,
    string mergeStatus = "succeeded", string? autoCompleteBy = "Offline", bool draft = false, bool hidden = false, bool active = true) => new()
{
    Key = $"repo-id:{id}", Id = id, Title = "Offline PR", Url = "https://dev.azure.com/offline",
    Org = org, Project = project, ProjectId = "project-id", Repo = repo, RepoId = "repo-id",
    Author = new("Offline", null, null),
    MergeStatus = mergeStatus, AutoCompleteBy = autoCompleteBy, IsDraft = draft, IsHidden = hidden, IsActive = active,
    Agency = new("clean", 1, 1, "Clean offline review"),
};
PolicyView Policy(string kind, string status, bool automaticReview = false, string? description = null, bool blocking = true, bool requiresLatestApproval = false) =>
    new("offline", kind, kind, status, blocking, null, null, false, automaticReview, description, requiresLatestApproval);
void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task Test(string name, Func<Task> run)
{
    var filter = Environment.GetEnvironmentVariable("ADOPRVIEW_TEST_FILTER");
    if (!string.IsNullOrEmpty(filter) && !filter.Split('|').Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return;
    await run();
    Console.WriteLine("PASS " + name);
    tests++;
}
for (var mask = 1; mask < 8; mask++)
{
    var combination = mask;
    await Test("review / PoP / auto-complete human-only combination " + combination, () =>
    {
        var pr = Pr(autoCompleteBy: (combination & 4) != 0 ? null : "Offline");
        if ((combination & 1) != 0) pr.Policies.Add(Policy("review", "queued"));
        if ((combination & 2) != 0) pr.Policies.Add(Policy("pop", "rejected"));
        PrService.Classify(pr);
        Check(pr.Lane == Lane.NeedsHuman && pr.Waiting.Count == 0 && pr.Blockers.Count == 0, "Human-only PR did not enter Needs Human");
        Check(pr.HumanActions.Contains("reviews") == ((combination & 1) != 0), "Wrong reviewer action");
        Check(pr.HumanActions.Contains("pop") == ((combination & 2) != 0), "Wrong PoP action");
        Check(pr.HumanActions.Contains("autocomplete") == ((combination & 4) != 0), "Wrong auto-complete action");
        if ((combination & 1) != 0) Check(pr.Gates["reviews"].State == "human", "Reviewer gate is not human");
        return Task.CompletedTask;
    });
}

foreach (var status in new[] { "queued", "running", "rejected" })
    await Test("native reviewer policy " + status + " is a human approval requirement", () =>
    {
        var pr = Pr();
        pr.Policies.Add(Policy("review", status));
        PrService.Classify(pr);
        Check(pr.Lane == Lane.NeedsHuman, "Reviewer approval incorrectly treated as automation");
        return Task.CompletedTask;
    });

foreach (var description in new[] { "Owner approval: Missing Approvals", "Latest iteration sign-off - Waiting on reviewer(s) to approve on latest iteration.", "Latest iteration sign-off - Additional human sign-off is required on the latest iteration." })
    foreach (var status in new[] { "queued", "running", "rejected" })
        await Test("review enforcement " + status + " awaiting approval is human: " + description, () =>
        {
            var pr = Pr();
            pr.Policies.Add(Policy("review", status, automaticReview: true, description));
            PrService.Classify(pr);
            Check(pr.Lane == Lane.NeedsHuman && pr.Gates["reviews"].State == "human" && pr.Waiting.Count == 0, "Known human wait treated as running automation");
            return Task.CompletedTask;
        });

foreach (var (kind, status) in new[] { ("build", "queued"), ("build", "running"), ("status", "running"), ("review", "queued"), ("review", "running") })
    await Test(kind + " automation " + status + " keeps human actions in Waiting", () =>
    {
        var pr = Pr(autoCompleteBy: null);
        pr.Policies.Add(Policy("review", "queued"));
        pr.Policies.Add(Policy(kind, status, automaticReview: kind == "review", description: "Processing"));
        PrService.Classify(pr);
        Check(pr.Lane == Lane.Waiting && pr.HumanActions.Contains("reviews"), "Automated wait lost precedence");
        return Task.CompletedTask;
    });

await Test("pending review bot remains Waiting with human approval outstanding", () =>
{
    var pr = Pr();
    pr.Policies.Add(Policy("review", "queued"));
    pr.Agency = new("pending", 1, 1, "review bot reviewing iteration #1");
    PrService.Classify(pr);
    Check(pr.Lane == Lane.Waiting && pr.HumanActions.Contains("reviews"), "In-progress review bot lost its wait");
    return Task.CompletedTask;
});

foreach (var needsApproval in new[] { false, true })
    await Test("completed partial review bot is a coverage warning, human approval needed: " + needsApproval, () =>
    {
        var pr = Pr();
        if (needsApproval) pr.Policies.Add(Policy("review", "queued"));
        pr.Agency = new("partial", 1, 1, "review bot completed with partial coverage");
        PrService.Classify(pr);
        Check(pr.Lane == (needsApproval ? Lane.NeedsHuman : Lane.Ready), "Completed partial review kept PR waiting");
        Check(pr.Waiting.Count == 0 && pr.Gates["agency"].State == "warn" && pr.Gates["agency"].Label == "Partial", "Coverage warning was lost or treated as running");
        return Task.CompletedTask;
    });

await Test("partial review bot does not hide a running build", () =>
{
    var pr = Pr();
    pr.Policies.Add(Policy("build", "running"));
    pr.Agency = new("partial", 1, 1, "review bot completed with partial coverage");
    PrService.Classify(pr);
    Check(pr.Lane == Lane.Waiting && pr.Waiting.Count == 1 && pr.Waiting[0].StartsWith("Build"), "Partial review changed genuine automation waits");
    return Task.CompletedTask;
});

JsonNode BotThreads(params string[] comments) => new JsonObject
{
    ["value"] = new JsonArray(comments.Select((content, i) => (JsonNode)new JsonObject
    {
        ["id"] = i + 1, ["status"] = "active",
        ["comments"] = new JsonArray(new JsonObject
        {
            ["commentType"] = "text", ["content"] = content,
            ["author"] = new JsonObject { ["id"] = BotId, ["displayName"] = "Review bot" },
        }),
    }).ToArray()),
};
JsonNode BotIterations(int current) => new JsonObject
{
    ["value"] = new JsonArray(new JsonObject { ["id"] = current, ["sourceRefCommit"] = new JsonObject { ["commitId"] = "current" } }),
};

await Test("current-iteration partial marker parses as completed and moves review-only PR to Needs Human", () =>
{
    var pr = Pr();
    pr.Policies.Add(Policy("review", "queued"));
    PrService.ParseThreads(pr, BotThreads("Iteration #1 **Partial review**"), BotIterations(1), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "partial" && pr.Agency.Detail.Contains("completed with partial coverage"), "Partial marker not parsed as terminal coverage warning");
    Check(pr.Lane == Lane.NeedsHuman && pr.Waiting.Count == 0, "Parsed partial review still waits");
    return Task.CompletedTask;
});

await Test("initial partial review remains completed across later ADO updates", () =>
{
    var pr = Pr();
    pr.Policies.Add(Policy("review", "queued"));
    var threads = BotThreads("Iteration #1 **Partial review**");
    threads["value"]![0]!["status"] = "closed";
    PrService.ParseThreads(pr, threads, BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "partial" && pr.Lane == Lane.NeedsHuman && pr.Waiting.Count == 0, "ADO updates incorrectly started a new review bot wait");
    Check(pr.Agency.Iteration == 1 && pr.Agency.CurrentIteration == 7 && pr.Gates["agency"].State == "warn", "Review iteration or coverage warning was lost");
    return Task.CompletedTask;
});

foreach (var (marker, expected) in new[] { ("No issues found", "clean"), ("Blocking", "addressed"), ("Suggestion", "addressed") })
    await Test("initial review bot " + marker + " outcome persists through iteration seven", () =>
    {
        var pr = Pr();
        var threads = BotThreads("Iteration #1 **" + marker + "**");
        threads["value"]![0]!["status"] = "fixed";
        PrService.ParseThreads(pr, threads, BotIterations(7), "current", bot);
        PrService.Classify(pr);
        Check(pr.Agency?.State == expected && pr.Lane == Lane.Ready && pr.Waiting.Count == 0, "Recorded review bot result became an imaginary in-progress run");
        Check(pr.Agency.Detail.Contains("updates do not trigger another Review bot review"), "Later updates are not explained");
        return Task.CompletedTask;
    });

await Test("observed PR needs only PoP and human sign-off after its initial finding was resolved", () =>
{
    var pr = Pr(id: 4242);
    pr.Policies.Add(Policy("build", "approved"));
    pr.Policies.Add(Policy("review", "approved"));
    pr.Policies.Add(Policy("review", "queued", requiresLatestApproval: true));
    pr.Policies.Add(Policy("review", "queued", true, "Owner approval: Missing Approvals"));
    pr.Policies.Add(Policy("review", "rejected", true, "Latest iteration sign-off - Additional human sign-off is required on the latest iteration."));
    pr.Policies.Add(Policy("pop", "rejected"));
    pr.Reviewers.Add(new("Earlier Reviewer", 10, false, false, "00000000-0000-4000-8000-0000000000c1"));
    var threads = BotThreads("Iteration #1 **Blocking** Initial finding");
    threads["value"]![0]!["status"] = "fixed";
    PrService.ParseThreads(pr, threads, BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Lane == Lane.NeedsHuman && pr.Blockers.Count == 0 && pr.Waiting.Count == 0, "Observed PR remains misclassified");
    Check(pr.HumanActions.Order().SequenceEqual(new[] { "pop", "reviews" }), "Observed PR human actions do not match ADO");
    Check(pr.Agency?.State == "addressed" && pr.Gates["agency"].Label == "Addressed", "Resolved initial review bot finding still appears Reviewing");
    return Task.CompletedTask;
});

await Test("open initial review bot finding still blocks even with a newer clean review", () =>
{
    var pr = Pr();
    PrService.ParseThreads(pr, BotThreads("Iteration #1 **Blocking** Initial finding", "Iteration #7 **No issues found**"), BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "findings" && pr.Lane == Lane.Blocked && pr.Gates["comments"].State == "ok", "Older review bot finding was hidden or mislabeled as an ordinary comment");
    Check(pr.Agency.Detail.StartsWith("1 open Review bot finding"), "Non-finding summary was counted as an open finding");
    return Task.CompletedTask;
});

await Test("partial coverage warning survives resolution of the run's findings", () =>
{
    var pr = Pr();
    var threads = BotThreads("Iteration #1 **Partial review**", "Iteration #1 **Blocking** Initial finding");
    threads["value"]![1]!["status"] = "fixed";
    PrService.ParseThreads(pr, threads, BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "partial" && pr.Gates["agency"].State == "warn" && pr.Lane == Lane.Ready, "Resolved findings erased the partial-coverage warning");
    return Task.CompletedTask;
});

foreach (var report in new[] { "", "Iteration #1 An unrecognized review bot message" })
    await Test("unreported review bot coverage warns without inventing a running job: " + report, () =>
    {
        var pr = Pr();
        pr.Policies.Add(Policy("pop", "rejected"));
        PrService.ParseThreads(pr, report.Length == 0 ? BotThreads() : BotThreads(report), BotIterations(7), "current", bot);
        PrService.Classify(pr);
        Check(pr.Agency?.State == (report.Length == 0 ? "none" : "unknown") && pr.Gates["agency"].State == "warn", "Unreported coverage is not visible as a warning");
        Check(pr.Lane == Lane.NeedsHuman && pr.Waiting.Count == 0, "Absence of a report was treated as active automation");
        return Task.CompletedTask;
    });

await Test("a newer observed clean review replaces an older partial result", () =>
{
    var pr = Pr();
    PrService.ParseThreads(pr, BotThreads("Iteration #1 **Partial review**", "Iteration #3 **No issues found**"), BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "clean" && pr.Agency.Iteration == 3 && pr.Lane == Lane.Ready, "A later actual review outcome was ignored");
    return Task.CompletedTask;
});

await Test("a review bot reply does not hide an unresolved human thread", () =>
{
    var pr = Pr();
    var threads = BotThreads("A human question");
    threads["value"]![0]!["comments"]![0]!["author"] = new JsonObject { ["id"] = "human", ["displayName"] = "Human reviewer" };
    threads["value"]![0]!["comments"]!.AsArray().Add(new JsonObject
    {
        ["author"] = new JsonObject { ["id"] = BotId },
        ["content"] = "Iteration #1 **No issues found**", ["commentType"] = "text",
    });
    PrService.ParseThreads(pr, threads, BotIterations(7), "current", bot);
    PrService.Classify(pr);
    Check(pr.Threads.Count == 1 && !pr.Threads[0].IsAgency && pr.Gates["comments"].State == "fail" && pr.Lane == Lane.Blocked, "review bot reply hid the human's thread");
    return Task.CompletedTask;
});

foreach (var configured in new ReviewBotSettings?[] { null, new() { Name = "Unused", AuthorId = "  " } })
    await Test("unconfigured review bot creates no gate, warning, wait or hidden thread: " + (configured is null ? "absent" : "blank"), () =>
    {
        var pr = Pr();
        pr.Policies.Add(Policy("pop", "rejected"));
        PrService.ParseThreads(pr, BotThreads("Iteration #1 **Blocking** Finding from an unconfigured author"), BotIterations(1), "current", configured);
        PrService.Classify(pr);
        Check(pr.Agency?.State == "na" && pr.Gates["agency"].State == "na", "Unconfigured bot manufactured a gate");
        Check(pr.Waiting.Count == 0 && pr.Threads.Count == 1 && !pr.Threads[0].IsAgency, "Unconfigured bot changed waits or thread ownership");
        Check(pr.Lane == Lane.Blocked && pr.Gates["comments"].State == "fail", "Ordinary unresolved thread was not counted");
        return Task.CompletedTask;
    });

await Test("open findings still block when current review bot run is partial", () =>
{
    var pr = Pr();
    PrService.ParseThreads(pr, BotThreads("Iteration #1 **Partial review**", "Iteration #1 **Blocking** Fix this issue"), BotIterations(1), "current", bot);
    PrService.Classify(pr);
    Check(pr.Agency?.State == "findings" && pr.Lane == Lane.Blocked, "Partial coverage hid open findings");
    return Task.CompletedTask;
});

foreach (var vote in new[] { -10, -5 })
    await Test("reviewer vote " + vote + " remains Blocked, not a missing +1", () =>
    {
        var pr = Pr();
        pr.Reviewers.Add(new("Reviewer", vote, true, false));
        pr.Policies.Add(Policy("review", "queued"));
        PrService.Classify(pr);
        Check(pr.Lane == Lane.Blocked && pr.Gates["reviews"].State == "fail", "Rejecting vote treated as a simple approval wait");
        return Task.CompletedTask;
    });

foreach (var (kind, status) in new[] { ("build", "rejected"), ("status", "rejected"), ("review", "broken"), ("review", "rejected") })
    await Test(kind + " failed automation " + status + " remains Blocked", () =>
    {
        var pr = Pr(autoCompleteBy: null);
        pr.Policies.Add(Policy(kind, status, automaticReview: kind == "review", description: "Automation failed"));
        PrService.Classify(pr);
        Check(pr.Lane == Lane.Blocked, "Failure was moved to a human-only lane");
        return Task.CompletedTask;
    });

await Test("only required no-vote reviewers create a human action", () =>
{
    foreach (var required in new[] { false, true })
    {
        var pr = Pr();
        pr.Reviewers.Add(new("Reviewer", 0, required, false));
        pr.Policies.Add(Policy("review", "queued", blocking: false));
        PrService.Classify(pr);
        Check(pr.Lane == (required ? Lane.NeedsHuman : Lane.Ready), "Wrong handling of optional/required reviewer");
    }
    return Task.CompletedTask;
});

await Test("observed reviewer statuses enter Needs Human after partial or clean review bot completion", () =>
{
    foreach (var agency in new[] { "partial", "clean" })
    {
        var pr = Pr();
        pr.Policies.Add(Policy("review", "queued"));
        pr.Policies.Add(Policy("review", "queued", true, "Owner approval: Missing Approvals"));
        pr.Policies.Add(Policy("review", "running", true, "Latest iteration sign-off - Waiting on reviewer(s) to approve on latest iteration."));
        pr.Policies.Add(Policy("build", "approved"));
        pr.Policies.Add(Policy("pop", "approved"));
        pr.Agency = new(agency, 1, 1, agency == "partial" ? "review bot completed with partial coverage on iteration #1" : "No issues on iteration #1");
        PrService.Classify(pr);
        Check(pr.Lane == Lane.NeedsHuman, "Observed review-only conditions misclassified");
        Check(pr.Gates["reviews"].State == "human", "Reviewer gate lost its human action");
    }
    Check(System.Text.Json.JsonSerializer.Serialize(Lane.NeedsHuman, Bridge.Json) == "\"needsHuman\"", "Bridge enum and UI lane key differ");
    return Task.CompletedTask;
});

Snapshot Snap(params PrView[] prs) => new() { Prs = prs.ToList(), LastRefresh = DateTimeOffset.UtcNow };
await Test("initial baseline and unchanged human rows create no arrival notifications", () =>
{
    var human = Pr(); human.Lane = Lane.NeedsHuman;
    Check(!PrService.GetLaneChanges(new Snapshot(), Snap(human)).Any(), "Initial baseline would sound");
    Check(!PrService.GetLaneChanges(Snap(human), Snap(human)).Any(), "Unchanged human row would sound");
    return Task.CompletedTask;
});

await Test("new and transitioned human PRs are detected, including single refresh during full refresh", () =>
{
    var waiting = Pr(); waiting.Lane = Lane.Waiting;
    var human = Pr(); human.Lane = Lane.NeedsHuman;
    var newHuman = Pr(id: 456); newHuman.Lane = Lane.NeedsHuman;
    var changes = PrService.GetLaneChanges(Snap(waiting), Snap(human, newHuman)).ToList();
    Check(changes.Count == 2 && changes.All(c => c.Pr.Lane == Lane.NeedsHuman), "Human arrivals missing");
    Check(changes.Single(c => c.Pr.Id == 123).Previous == Lane.Waiting && changes.Single(c => c.Pr.Id == 456).Previous is null, "Wrong prior lanes");
    var midRefresh = new Snapshot { Prs = [human], LastRefresh = DateTimeOffset.UtcNow, Refreshing = true };
    Check(PrService.GetLaneChanges(Snap(waiting), midRefresh).Count() == 1, "Single-PR transition lost during full refresh");
    return Task.CompletedTask;
});

await Test("hidden and error rows do not create arrival notifications", () =>
{
    var hidden = Pr(hidden: true); hidden.Lane = Lane.NeedsHuman;
    var error = Pr(id: 456); error.Lane = Lane.NeedsHuman; error.Error = "Offline error";
    Check(!PrService.GetLaneChanges(Snap(), Snap(hidden, error)).Any(), "Hidden/error rows would sound");
    return Task.CompletedTask;
});

await Test("custom chime is an original, non-clipping mono PCM wave under one second", () =>
{
    var wave = HumanAttentionSound.CreateWave();
    Check(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt ", "Invalid wave header");
    Check(BitConverter.ToInt32(wave, 4) == wave.Length - 8 && BitConverter.ToInt32(wave, 40) == wave.Length - 44, "Invalid wave length");
    Check(BitConverter.ToInt16(wave, 20) == 1 && BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt16(wave, 34) == 16, "Not mono 16-bit PCM");
    var duration = (wave.Length - 44) / (double)BitConverter.ToInt32(wave, 28);
    Check(duration > 0.6 && duration < 1, "Unexpected chime duration");
    var peak = Enumerable.Range(0, (wave.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wave, 44 + i * 2)));
    Check(peak > 1000 && peak < short.MaxValue / 3, "Silent or excessively loud/clipped chime");
    Check(wave.SequenceEqual(HumanAttentionSound.CreateWave()), "Chime is not stable");
    if (args.Contains("--preview-chime"))
    {
        using var player = new System.Media.SoundPlayer(new MemoryStream(wave));
        player.PlaySync();
        Console.WriteLine("Played the custom Needs Human chime once through the Windows audio API.");
    }
    return Task.CompletedTask;
});

await MergeChecksTests.Run(Test);
await ApprovalProviderTests.Run(Test);
await SettingsTests.Run(Test);
await ReviewBotScopeTests.Run(Test);
await BuildCacheScopeTests.Run(Test);
await CopilotSourceTests.Run(Test);
await CopilotRunTests.Run(Test);
await CopilotPreflightTests.Run(Test);
await CopilotRecoveryTests.Run(Test);
await PopPaneTests.Run(Test);
await AutoCompleteTests.Run(Test);

Console.WriteLine($"{tests} offline tests passed. No live HTTP or approval requests were made.");

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return respond(request);
    }
}
