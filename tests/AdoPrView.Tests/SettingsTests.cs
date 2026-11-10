using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using AdoPrView;

static class SettingsTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("settings missing file starts empty without seeded repos, authors or bot", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            Check(store.Current.Repos.Count == 0 && store.Current.Whitelist.Count == 0 && store.Current.ReviewBot is null &&
                store.Current.FixSkill is null && store.Current.FixReviewerModels.Count == 0 && store.Current.ApprovalProvider is null, "New settings were seeded");
            Check(!File.Exists(path), "Loading created a settings file");
            Check(!store.Current.IgnoreDrafts, "Draft ignoring must be opt-in");
        }));
        await test("settings save preserves unknown fields at every level and personal choices", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """
                {"repos":[{"org":"contoso","project":"Sample","repo":"sample-repo","futureRepo":1}],
                 "whitelist":["someone"],"hidden":["r:1"],"refreshSeconds":45,
                 "autoComplete":{"mergeStrategy":"rebase","futureMerge":true},
                 "window":{"left":1,"top":2,"width":3,"height":4,"futureWindow":"x"},
                 "reviewBot":{"name":"Bot","authorId":"b","futureBot":2},
                 "approvalProvider":{"enabled":false,"futureProvider":3},
                 "futureTop":{"nested":[1,2]}}
                """);
            var store = new SettingsStore(path);
            store.Update(s => s.MergeCheckMinutes = 7);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(saved["futureTop"]!["nested"]![1]!.GetValue<int>() == 2 && saved["repos"]![0]!["futureRepo"]!.GetValue<int>() == 1 &&
                saved["autoComplete"]!["futureMerge"]!.GetValue<bool>() && saved["window"]!["futureWindow"]!.GetValue<string>() == "x" &&
                saved["reviewBot"]!["futureBot"]!.GetValue<int>() == 2 && saved["approvalProvider"]!["futureProvider"]!.GetValue<int>() == 3,
                "Unknown fields were dropped");
            Check(saved["refreshSeconds"]!.GetValue<int>() == 45 && saved["mergeCheckMinutes"]!.GetValue<int>() == 7 &&
                saved["autoComplete"]!["mergeStrategy"]!.GetValue<string>() == "rebase" && saved["hidden"]![0]!.GetValue<string>() == "r:1",
                "Personal choices changed");
            Check(!File.Exists(path + ".tmp"), "Atomic save left a temporary file");
        }));
        await test("settings drawer Save through the native bridge keeps unknown nested fields", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """
                {"autoComplete":{"mergeStrategy":"rebase","deleteSourceBranch":true,"transitionWorkItems":true,"futureMerge":"KEEP"},
                 "window":{"left":1,"top":2,"width":900,"height":700,"futureWindow":"x"},"futureTop":1}
                """);
            var store = new SettingsStore(path);
            using var http = new HttpClient();
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            // Same shape as the drawer: the native settings spread, with the Save form's fields replacing them.
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            form["refreshSeconds"] = 300;
            form["ignoreDrafts"] = true;
            form["autoComplete"] = new JsonObject { ["mergeStrategy"] = "noFastForward", ["deleteSourceBranch"] = false, ["transitionWorkItems"] = false };
            bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings", ["params"] = new JsonObject { ["settings"] = form } }.ToJsonString())
                .GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\""), "Save failed: " + reply);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            var ac = saved["autoComplete"]!;
            Check(ac["futureMerge"]!.GetValue<string>() == "KEEP" && ac["mergeStrategy"]!.GetValue<string>() == "noFastForward" &&
                !ac["deleteSourceBranch"]!.GetValue<bool>() && !ac["transitionWorkItems"]!.GetValue<bool>(), "Auto-complete save lost or ignored fields");
            Check(saved["window"]!["futureWindow"]!.GetValue<string>() == "x" && saved["futureTop"]!.GetValue<int>() == 1 &&
                saved["refreshSeconds"]!.GetValue<int>() == 300, "Drawer save dropped other settings");
            Check(new SettingsStore(path).Current.IgnoreDrafts, "Draft setting did not survive bridge save/reload");
        }));
        await test("window placement persistence keeps unknown placement fields", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"window":{"left":1,"top":2,"width":900,"height":700,"futureWindow":"x"}}""");
            var store = new SettingsStore(path);
            store.Update(s => MainWindow.StorePlacement(s, new Rect(10, 20, 1200, 800), true));
            var w = JsonNode.Parse(File.ReadAllText(path))!["window"]!;
            Check(w["futureWindow"]!.GetValue<string>() == "x" && w["left"]!.GetValue<double>() == 10 && w["width"]!.GetValue<double>() == 1200 &&
                w["maximized"]!.GetValue<bool>(), "Placement save lost unknown fields or values");
            var fresh = new SettingsStore(Path.Combine(dir, "fresh.json"));
            fresh.Update(s => MainWindow.StorePlacement(s, new Rect(5, 6, 800, 600), false));
            Check(fresh.Current.Window is { Left: 5, Height: 600, Maximized: false }, "First placement was not stored");
        }));
        foreach (var bad in new[] { "{ not json", "null", "" })
            await test("unreadable settings fail visibly and are never overwritten: " + (bad.Length == 0 ? "empty" : bad), () => InDir(dir =>
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, bad);
                var before = File.ReadAllBytes(path);
                try { new SettingsStore(path); throw new Exception("Malformed settings loaded"); }
                catch (SettingsLoadException ex) { Check(ex.Message.Contains("not changed"), "Failure does not say the file was preserved"); }
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before) && Directory.GetFiles(dir).Length == 1, "Malformed settings were modified");
            }));
    }

    static Task InDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ado-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }
}
