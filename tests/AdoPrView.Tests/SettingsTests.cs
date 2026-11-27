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
        await test("settings Fix backend defaults Windows and selected targets require their own explicit path and distro", () =>
        {
            var repo = new RepoRef { Org = "org", Project = "project", Repo = "repo" };
            var settings = new AppSettings { WslDistro = "Ubuntu", WslRepoDirectories = new() { [repo.Key] = "/source" } };
            Check(settings.FixBackend == "windows-native", "Legacy WSL configuration opted into new WSL Fix runs");
            void Missing(string expected)
            {
                try { settings.FixTarget(repo); }
                catch (ArgumentException ex) { Check(ex.Message.Contains(expected), "Wrong selected target diagnostic"); return; }
                throw new Exception("Missing selected backend configuration was accepted");
            }
            Missing("Windows");
            settings.NativeRepoDirectories[repo.Key] = @"C:\source";
            Check(settings.FixTarget(repo) == ("windows-native", null, @"C:\source"), "Windows borrowed WSL distro/path");
            settings.FixBackend = "wsl";
            Check(settings.FixTarget(repo) == ("wsl", "Ubuntu", "/source"), "WSL borrowed native path");
            settings.WslDistro = null;
            Missing("explicit");
            settings.WslDistro = "Ubuntu";
            settings.WslRepoDirectories.Clear();
            Missing("WSL");
            return Task.CompletedTask;
        });
        await test("settings Fix backend round-trips both inactive mappings and older clients cannot reset the choice", () => InDir(dir =>
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            using var http = new HttpClient();
            var prs = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var bridge = new Bridge(prs, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            form["nativeRepoDirectories"] = new JsonObject { ["org/project/repo"] = @"C:\source" };
            form["wslRepoDirectories"] = new JsonObject { ["org/project/repo"] = "/source" };
            form["fixBackend"] = "wsl";
            void Save() => bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = form.DeepClone() } }.ToJsonString()).GetAwaiter().GetResult();
            Save();
            Check(reply!.Contains("explicit installed WSL"), "WSL was selected without a distro");
            form["wslDistro"] = "Ubuntu";
            Save();
            Check(!reply!.Contains("\"error\"") && store.Current.FixBackend == "wsl", "Valid WSL option failed");
            form.Remove("fixBackend"); form.Remove("wslDistro"); form.Remove("wslRepoDirectories");
            Save();
            Check(store.Current.FixBackend == "wsl" && store.Current.WslDistro == "Ubuntu" &&
                store.Current.WslRepoDirectories["org/project/repo"] == "/source", "Older client erased opted-in WSL configuration");
            form["fixBackend"] = "windows-native";
            Save();
            Check(store.Current.FixBackend == "windows-native" && store.Current.NativeRepoDirectories["org/project/repo"] == @"C:\source" &&
                store.Current.WslRepoDirectories["org/project/repo"] == "/source", "Switch discarded inactive configuration");
            form["fixBackend"] = "automatic";
            Save();
            Check(reply!.Contains("Choose Windows or WSL") && store.Current.FixBackend == "windows-native", "Automatic fallback backend was saved");
        }));
        await test("settings Windows checkout mappings validate native paths and preserve legacy WSL and future fields", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"wslDistro":"Legacy-Ubuntu","wslRepoDirectories":{"org/project/repo":"/legacy/source"},"futureTop":"KEEP"}""");
            var store = new SettingsStore(path);
            using var http = new HttpClient();
            var prs = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var bridge = new Bridge(prs, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            void Save() => bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = form.DeepClone() } }.ToJsonString()).GetAwaiter().GetResult();
            foreach (var invalid in new[] { "relative\\repo", "/home/legacy", "C:repo", "C:\\repos\\bad\npath" })
            {
                form["nativeRepoDirectories"] = new JsonObject { ["ORG/PROJECT/REPO"] = invalid };
                var original = File.ReadAllText(path);
                Save();
                Check(reply?.Contains("\"error\"") == true && reply.Contains("absolute Windows") && File.ReadAllText(path) == original,
                    "Invalid native mapping was saved or lacked actionable feedback: " + invalid);
            }
            form["nativeRepoDirectories"] = null;
            Save();
            Check(reply?.Contains("\"error\"") == true && reply.Contains("not null"), "Null native mappings were silently discarded");
            form["nativeRepoDirectories"] = new JsonObject { ["ORG/PROJECT/REPO"] = "\\\\server\\share\\repo" };
            Save();
            Check(reply?.Contains("\"error\"") == true && reply.Contains("local drive"), "Unsupported UNC checkout was saved without platform feedback");
            form["nativeRepoDirectories"] = new JsonObject { ["ORG/PROJECT/REPO"] = "  C:\\repos\\native repo  " };
            Save();
            var reloaded = new SettingsStore(path);
            Check(reply?.Contains("\"error\"") == false && reloaded.Current.NativeRepoDirectories["org/project/repo"] == "C:\\repos\\native repo" &&
                reloaded.Current.WslDistro == "Legacy-Ubuntu" && reloaded.Current.WslRepoDirectories["org/project/repo"] == "/legacy/source" &&
                reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Native settings erased legacy mappings or unrelated data");
            form.Remove("nativeRepoDirectories");
            Save();
            Check(new SettingsStore(path).Current.NativeRepoDirectories["org/project/repo"] == "C:\\repos\\native repo",
                "Older settings clients erased Windows mappings");
        }));
        await test("settings additional Fix instructions round-trip independently and older clients preserve them", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"triageInstructions":"CUSTOM","refreshSeconds":45,"hidden":["offline:123"],"approvalProvider":{"enabled":false,"futureProvider":"KEEP"},"futureTop":"KEEP"}""");
            var store = new SettingsStore(path);
            using var http = new HttpClient();
            var prs = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var bridge = new Bridge(prs, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
            const string extra = "  Extra\r\nverbatim\n";
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            form["additionalTriageInstructions"] = extra;
            void Save() => bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = form.DeepClone() } }.ToJsonString()).GetAwaiter().GetResult();
            Save();
            var reloaded = new SettingsStore(path);
            Check(reply is not null && !reply.Contains("\"error\"") && reloaded.Current.TriageInstructions == "CUSTOM" &&
                reloaded.Current.AdditionalTriageInstructions == extra &&
                reloaded.Current.ApprovalProvider!.Extra!["futureProvider"].GetString() == "KEEP" &&
                reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Suffix save lost Ask, whitespace or private/future fields");
            form.Remove("additionalTriageInstructions");
            Save();
            reloaded = new SettingsStore(path);
            Check(reloaded.Current.AdditionalTriageInstructions == extra &&
                reloaded.Current.ApprovalProvider!.Extra!["futureProvider"].GetString() == "KEEP" &&
                reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Older client erased suffix or integration fields");
            foreach (var cleared in new string?[] { null, "", " \r\n\t" })
            {
                form["additionalTriageInstructions"] = extra;
                Save();
                form["additionalTriageInstructions"] = cleared;
                Save();
                reloaded = new SettingsStore(path);
                Check(reloaded.Current.AdditionalTriageInstructions is null &&
                    reloaded.Current.TriageInstructions == "CUSTOM" && reloaded.Current.RefreshSeconds == 45 &&
                    reloaded.Current.Hidden.SequenceEqual(["offline:123"]) &&
                    reloaded.Current.ApprovalProvider!.Extra!["futureProvider"].GetString() == "KEEP" &&
                    reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Clearing suffix changed unrelated settings");
            }
        }));
        await test("settings missing file starts empty without seeded repos, authors or bot", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            Check(store.Current.Repos.Count == 0 && store.Current.Whitelist.Count == 0 && store.Current.ReviewBot is null &&
                store.Current.FixSkill is null && store.Current.FixReviewerModels.Count == 0 && store.Current.ApprovalProvider is null, "New settings were seeded");
            Check(!File.Exists(path), "Loading created a settings file");
            Check(!store.Current.IgnoreDrafts, "Draft ignoring must be opt-in");
            Check(store.Current.Notifications && store.Current.Sound, "New settings should enable notifications and sound");
            Check(store.Current.Theme == "light", "New settings must default to Light Mode");
        }));
        foreach (var original in new[] { "github", "light", "dark", "future-theme" })
            await test($"theme bridge persists supported choices and preserves unrelated settings from {original}", () => InDir(dir =>
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new { theme = original, refreshSeconds = 45,
                    hidden = new[] { "offline:123" }, futureTop = new { preserve = true } }));
                var originalText = File.ReadAllText(path);
                var store = new SettingsStore(path);
                var canonical = original == "github" ? "light" : original;
                Check(store.Current.Theme == canonical && File.ReadAllText(path) == originalText,
                    "Loading failed to normalize the legacy alias, changed an unknown theme, or rewrote settings");
                store.Update(s => s.RefreshSeconds = 45);
                using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
                    Check(saved.RootElement.GetProperty("theme").GetString() == canonical &&
                        saved.RootElement.GetProperty("futureTop").GetProperty("preserve").GetBoolean(),
                        "A settings save failed to retain the canonical/unknown theme or unrelated fields");
                using var http = new HttpClient();
                var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
                string? reply = null, applied = null;
                var kicks = 0;
                var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => kicks++, themeChanged: theme => applied = theme);
                foreach (var theme in new[] { "dark", "light" })
                {
                    bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "setTheme",
                        ["params"] = new JsonObject { ["theme"] = theme } }.ToJsonString()).GetAwaiter().GetResult();
                    Check(reply is not null && !reply.Contains("\"error\"") && applied == theme, "Theme bridge failed: " + reply);
                    var reloaded = new SettingsStore(path);
                    Check(reloaded.Current.Theme == theme && reloaded.Current.RefreshSeconds == 45 &&
                        reloaded.Current.Hidden.SequenceEqual(["offline:123"]) && reloaded.Current.Extra!["futureTop"].GetProperty("preserve").GetBoolean(),
                        "Theme save changed unrelated/unknown settings");
                }
                Check(kicks == 0, "Appearance change refreshed PRs");
                var before = File.ReadAllText(path);
                applied = null;
                bridge.HandleAsync("""{"id":2,"method":"setTheme","params":{"theme":"unsupported"}}""").GetAwaiter().GetResult();
                Check(reply!.Contains("\"error\"") && applied is null && File.ReadAllText(path) == before,
                    "Unsupported theme was accepted or applied");
                // Older clients and stale general-settings forms cannot reset the independently saved theme.
                store.SetTheme("dark");
                bridge.HandleAsync("""{"id":3,"method":"saveSettings","params":{"settings":{"theme":"github","refreshSeconds":90}}}""")
                    .GetAwaiter().GetResult();
                Check(new SettingsStore(path).Current.Theme == "dark", "General settings save reset the selected theme");
            }));
        await test("theme legacy default does not rewrite settings and failed saves roll back before native application", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            const string text = """{"refreshSeconds":45,"futureTop":"KEEP"}""";
            File.WriteAllText(path, text);
            var store = new SettingsStore(path);
            Check(store.Current.Theme == "light" && File.ReadAllText(path) == text, "Legacy theme migration rewrote settings");
            Directory.CreateDirectory(path + ".tmp");
            using var http = new HttpClient();
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var applied = false;
            var bridge = new Bridge(service, store, value => { reply = value; return Task.CompletedTask; }, _ => { }, themeChanged: _ => applied = true);
            bridge.HandleAsync("""{"id":1,"method":"setTheme","params":{"theme":"dark"}}""").GetAwaiter().GetResult();
            Check(reply!.Contains("\"error\"") && !applied && store.Current.Theme == "light" && File.ReadAllText(path) == text,
                "Failed theme save changed current settings, applied native chrome, or damaged the original file");
        }));
        foreach (var (json, expected) in new[] { ("""{"whitelist":["Friend"]}""", "watched"), ("""{"whitelist":["Friend"],"Scope":"all"}""", "all"), ("""{"whitelist":[]}""", null) })
            await test($"scope migration from {json} starts on {expected ?? "null"} without rewriting the file", () => InDir(dir =>
            {
                var path = Path.Combine(dir, "settings.json");
                File.WriteAllText(path, json);
                Check(new SettingsStore(path).Current.Scope == expected && File.ReadAllText(path) == json, "Scope migration wrong or rewrote settings");
            }));
        await test("scope bridge saves only valid scopes without refreshing and settings Save never changes it", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"whitelist":["Friend"],"refreshSeconds":45}""");
            var store = new SettingsStore(path);
            using var http = new HttpClient();
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var kicks = 0;
            var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => kicks++);
            bridge.HandleAsync("""{"id":1,"method":"setScope","params":{"scope":"mine"}}""").GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\"") && new SettingsStore(path).Current.Scope == "mine" && kicks == 0, "Scope was not saved alone: " + reply);
            bridge.HandleAsync("""{"id":2,"method":"setScope","params":{"scope":"drafts"}}""").GetAwaiter().GetResult();
            Check(reply!.Contains("\"error\"") && store.Current.Scope == "mine", "Invalid scope was accepted");
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            form["scope"] = "all";
            bridge.HandleAsync(new JsonObject { ["id"] = 3, ["method"] = "saveSettings", ["params"] = new JsonObject { ["settings"] = form } }.ToJsonString())
                .GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\"") && new SettingsStore(path).Current.Scope == "mine", "Settings Save changed the saved scope");
        }));
        foreach (var notifications in new[] { false, true })
        {
            await test($"legacy sound migration preserves notifications={notifications} without rewriting the file", () => InDir(dir =>
            {
                var path = Path.Combine(dir, "settings.json");
                var text = JsonSerializer.Serialize(new { notifications, futureTop = "KEEP" });
                File.WriteAllText(path, text);
                var store = new SettingsStore(path);
                Check(store.Current.Sound == notifications, "Legacy sound behavior changed");
                Check(File.ReadAllText(path) == text, "Loading rewrote legacy settings");
                store.Update(s => s.Notifications = !notifications);
                var reloaded = new SettingsStore(path);
                Check(reloaded.Current.Sound == notifications && reloaded.Current.Notifications == !notifications,
                    "Changing tray notifications changed the independent sound preference");
                Check(reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Migration dropped unknown fields");
            }));
            foreach (var sound in new[] { false, true })
                await test($"independent notification/sound settings round-trip through the bridge: {notifications}/{sound}", () => InDir(dir =>
                {
                    var path = Path.Combine(dir, "settings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(new { notifications = !sound, SoUnD = sound, futureTop = "KEEP" }));
                    var store = new SettingsStore(path);
                    Check(store.Current.Sound == sound, "Explicit case-insensitive sound setting was ignored");
                    using var http = new HttpClient();
                    var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
                    string? reply = null;
                    var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => { });
                    var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
                    form["notifications"] = notifications;
                    form["sound"] = sound;
                    bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings",
                        ["params"] = new JsonObject { ["settings"] = form } }.ToJsonString()).GetAwaiter().GetResult();
                    Check(reply is not null && !reply.Contains("\"error\""), "Settings save failed: " + reply);
                    var reloaded = new SettingsStore(path);
                    Check(reloaded.Current.Notifications == notifications && reloaded.Current.Sound == sound, "Independent settings did not persist");
                    Check(reloaded.Current.Extra!["futureTop"].GetString() == "KEEP", "Settings save dropped unknown fields");
                    form.Remove("sound");
                    form["notifications"] = !notifications;
                    bridge.HandleAsync(new JsonObject { ["id"] = 2, ["method"] = "saveSettings",
                        ["params"] = new JsonObject { ["settings"] = form.DeepClone() } }.ToJsonString()).GetAwaiter().GetResult();
                    Check(reply is not null && !reply.Contains("\"error\""), "Older settings save failed: " + reply);
                    Check(new SettingsStore(path).Current.Sound == sound, "Older settings clients reset the sound preference");
                }));
        }
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
        await test("failed settings drawer Save keeps committed state and unrelated author retry cannot persist its draft", () => InDir(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            const string text = """
                {"triageInstructions":"Saved Custom Ask","refreshSeconds":120,"wslDistro":"Saved distro",
                 "wslRepoDirectories":{"contoso/sample/repo":"/home/synthetic/saved"},"whitelist":["Saved author"],
                 "autoComplete":{"mergeStrategy":"squash","futureMerge":"KEEP"},"futureTop":{"keep":true}}
                """;
            File.WriteAllText(path, text);
            var store = new SettingsStore(path);
            var original = JsonSerializer.Serialize(store.Current, Bridge.Json);
            using var http = new HttpClient();
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var kicks = 0;
            var bridge = new Bridge(service, store, value => { reply = value; return Task.CompletedTask; }, _ => kicks++);
            var form = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            form["triageInstructions"] = "Uncommitted Custom Ask";
            form["refreshSeconds"] = 333;
            form["wslDistro"] = "Uncommitted distro";
            form["wslRepoDirectories"]!["contoso/sample/repo"] = "/home/synthetic/uncommitted";
            form["autoComplete"]!["mergeStrategy"] = "rebase";
            Directory.CreateDirectory(path + ".tmp");
            bridge.HandleAsync(new JsonObject { ["id"] = 1, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = form } }.ToJsonString()).GetAwaiter().GetResult();
            Check(reply is not null && reply.Contains("\"error\"") && kicks == 0, "Failed Save did not surface an error or refreshed committed state");
            Check(JsonSerializer.Serialize(store.Current, Bridge.Json) == original && File.ReadAllText(path) == text,
                "Failed drawer Save promoted the draft or damaged the saved file");
            Directory.Delete(path + ".tmp");
            var authorForm = JsonSerializer.SerializeToNode(store.Current, Bridge.Json)!.AsObject();
            authorForm["whitelist"]!.AsArray().Add("New author");
            bridge.HandleAsync(new JsonObject { ["id"] = 2, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = authorForm } }.ToJsonString()).GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\""), "Unrelated author Save failed");
            var reloaded = new SettingsStore(path);
            Check(reloaded.Current.TriageInstructions == "Saved Custom Ask" && reloaded.Current.RefreshSeconds == 120 &&
                reloaded.Current.WslDistro == "Saved distro" && reloaded.Current.WslRepoDirectories["contoso/sample/repo"] == "/home/synthetic/saved" &&
                reloaded.Current.AutoComplete.MergeStrategy == "squash" && reloaded.Current.Whitelist.SequenceEqual(["Saved author", "New author"]),
                "Unrelated author Save silently committed a failed draft");
            Check(reloaded.Current.Extra!["futureTop"].GetProperty("keep").GetBoolean(), "Transactional save dropped unknown settings");
            bridge.HandleAsync(new JsonObject { ["id"] = 3, ["method"] = "saveSettings",
                ["params"] = new JsonObject { ["settings"] = form.DeepClone() } }.ToJsonString()).GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\"") && new SettingsStore(path).Current.RefreshSeconds == 333,
                "Explicit draft retry did not persist the corrected file");
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
        await test("addRepo validates the repository with Azure DevOps and stores canonical names and ids", () => InDir(dir =>
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            string? url = null;
            using var http = new HttpClient(new FakeHandler(req =>
            {
                url = req.RequestUri!.AbsoluteUri;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                    """{"id":"repo-1","name":"Widgets","project":{"id":"proj-1","name":"Fabrikam"}}""") });
            }));
            var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
            string? reply = null;
            var kicks = 0;
            var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => kicks++);
            bridge.HandleAsync("""{"id":1,"method":"addRepo","params":{"url":"https://dev.azure.com/contoso/fabrikam/_git/widgets"}}""").GetAwaiter().GetResult();
            Check(reply is not null && !reply.Contains("\"error\""), "Valid repository add failed: " + reply);
            Check(url == "https://dev.azure.com/contoso/fabrikam/_apis/git/repositories/widgets?api-version=7.1", "Unexpected validation URL " + url);
            var added = new SettingsStore(Path.Combine(dir, "settings.json")).Current.Repos.Single();
            Check(added is { Org: "contoso", Project: "Fabrikam", Repo: "Widgets", RepoId: "repo-1", ProjectId: "proj-1" } && kicks == 1,
                "Validated repository was not stored with canonical names and ids");
        }));
        foreach (var (name, respond, expected) in new (string, Func<HttpResponseMessage>, string)[]
        {
            ("404", () => new(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"TF401019"}""") }, "or you don't have access to it"),
            ("401", () => new(System.Net.HttpStatusCode.Unauthorized), "Check your configured PAT, or run 'az login'"),
            ("empty success", () => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") }, "Azure DevOps did not return repository 'widgets'"),
            ("network", () => throw new HttpRequestException("offline"), "Could not reach Azure DevOps to validate repository 'widgets'"),
            ("timeout", () => throw new TaskCanceledException("timeout"), "Timed out contacting Azure DevOps to validate repository 'widgets'"),
        })
            await test($"addRepo rejects a repository that cannot be validated: {name}", () => InDir(dir =>
            {
                var path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(respond())));
                var service = new PrService(new AdoClient(http, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "offline"))), store);
                string? reply = null;
                var kicks = 0;
                var bridge = new Bridge(service, store, text => { reply = text; return Task.CompletedTask; }, _ => kicks++);
                bridge.HandleAsync("""{"id":1,"method":"addRepo","params":{"url":"https://dev.azure.com/contoso/Fabrikam/_git/widgets"}}""").GetAwaiter().GetResult();
                var error = reply is null ? null : JsonNode.Parse(reply)?["error"]?.GetValue<string>();
                Check(error is not null && error.Contains(expected), $"Unexpected validation failure: {reply}");
                Check(store.Current.Repos.Count == 0 && !File.Exists(path) && kicks == 0, "A repository that failed validation was persisted");
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
