using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdoPrView;

// A recurring real-cat character: which bundled photo plays which part of this PR's story.
internal sealed record VideoCast(string CatId, string Role);
// One visual idea, revealed when the narration reaches Cue (words quoted from that scene's narration).
internal sealed record VideoBeat(string Text, string Cue, string Tone);
internal sealed record VideoCatShot(string CatId, string Shot, string Place, string Action, string Bubble);
internal sealed record VideoScene(string Layout, string Kicker, string Narration, VideoBeat[] Beats, VideoCatShot Cat, string Camera);
internal sealed record VideoStory(string Title, VideoCast[] Cast, VideoScene[] Scenes)
{
    internal const int Width = 1280, Height = 720;
    internal const string Model = "claude-opus-5.5";
    internal static readonly string[] Layouts = ["hook", "term", "contrast", "journey", "list"];
    internal static readonly string[] Tones = ["plain", "old", "new", "alert"];
    internal static readonly string[] Shots = ["portrait", "face"];
    internal static readonly string[] Actions = ["enter", "pop", "peek", "drift"];
    internal static readonly string[] Cameras = ["still", "push", "drift"];
    internal static readonly JsonSerializerOptions Json = new(Bridge.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static VideoStory Parse(string text, IEnumerable<string> catIds)
    {
        if (text.Length > 32_000) throw new InvalidDataException("The storyboard exceeded its size limit.");
        var story = JsonSerializer.Deserialize<VideoStory>(text, Json)
            ?? throw new InvalidDataException("Copilot returned no storyboard.");
        story.Validate(catIds);
        return story;
    }

    internal static (int Min, int Max) BeatRange(string layout) => layout switch
    {
        "hook" => (1, 2), "term" or "contrast" => (2, 2), _ => (2, 4),
    };

    internal static string[] Places(string layout) => layout switch
    {
        "contrast" => ["before", "after"], "journey" => ["travel"], _ => ["left", "right"],
    };

    static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    static string Plain(string text) =>
        string.Join(' ', Words(new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())));

    public void Validate(IEnumerable<string> catIds)
    {
        static void Text(string? text, int limit, string field, bool empty = false)
        {
            if (text is null || (!empty && string.IsNullOrWhiteSpace(text)) || text.Length > limit ||
                text.Any(char.IsControl) || text.Contains("://", StringComparison.Ordinal) ||
                text.Contains("www.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The storyboard has an invalid {field} (empty, too long, a URL or control text).");
        }
        static void OneOf(string? value, string[] allowed, string field)
        {
            if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException($"The storyboard {field} must be one of: {string.Join(", ", allowed)}.");
        }
        Text(Title, 80, "title");
        var catalog = catIds.ToHashSet(StringComparer.Ordinal);
        if (Cast is null || Cast.Length is < 1 or > 3 || Cast.Any(c => c is null))
            throw new InvalidDataException("Cast 1-3 real cats as recurring characters.");
        foreach (var member in Cast)
        {
            Text(member.CatId, 48, "cast cat ID");
            Text(member.Role, 60, "cast role");
            if (!catalog.Contains(member.CatId)) throw new InvalidDataException("Copilot cast an unknown cat asset ID.");
        }
        if (Cast.Select(c => c.CatId).Distinct(StringComparer.Ordinal).Count() != Cast.Length)
            throw new InvalidDataException("Each cast cat plays exactly one role.");
        if (Scenes is null || Scenes.Length is < 6 or > 10 || Scenes.Any(s => s is null))
            throw new InvalidDataException("Use 6-10 scenes.");
        var appearances = Cast.ToDictionary(c => c.CatId, _ => 0, StringComparer.Ordinal);
        for (var number = 1; number <= Scenes.Length; number++)
        {
            var scene = Scenes[number - 1];
            OneOf(scene.Layout, Layouts, $"scene {number} layout");
            OneOf(scene.Camera, Cameras, $"scene {number} camera");
            Text(scene.Kicker, 40, $"scene {number} kicker");
            Text(scene.Narration, 320, $"scene {number} narration");
            if (Words(scene.Narration).Length < 6) throw new InvalidDataException($"Scene {number} narration is too short to follow.");
            var (min, max) = BeatRange(scene.Layout);
            if (scene.Beats is null || scene.Beats.Length < min || scene.Beats.Length > max || scene.Beats.Any(b => b is null))
                throw new InvalidDataException($"Scene {number}: a {scene.Layout} layout needs {min}-{max} beats.");
            var cursor = 0;
            var spoken = Plain(scene.Narration);
            foreach (var beat in scene.Beats)
            {
                Text(beat.Text, 44, $"scene {number} beat text");
                Text(beat.Cue, 40, $"scene {number} beat cue");
                OneOf(beat.Tone, Tones, $"scene {number} beat tone");
                var at = scene.Narration.IndexOf(beat.Cue, cursor, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    throw new InvalidDataException($"Scene {number} cue \"{beat.Cue}\" must quote words from that scene's narration, in reveal order.");
                cursor = at + beat.Cue.Length;
                if (Plain(beat.Text) == Plain(scene.Kicker))
                    throw new InvalidDataException($"Scene {number} repeats its kicker as a beat; make on-screen text complementary.");
                if (Words(beat.Text).Length >= 6 && spoken.Contains(Plain(beat.Text), StringComparison.Ordinal))
                    throw new InvalidDataException($"Scene {number} beat \"{beat.Text}\" copies the spoken caption; condense it to a short visual label.");
            }
            var cat = scene.Cat ?? throw new InvalidDataException($"Scene {number} needs a cat object (empty strings for no cat).");
            if (cat.CatId == "")
            {
                if (cat.Shot != "" || cat.Place != "" || cat.Action != "" || cat.Bubble != "")
                    throw new InvalidDataException($"Scene {number} has cat staging without a cat; leave every cat field empty.");
                continue;
            }
            Text(cat.CatId, 48, $"scene {number} cat ID");
            if (!appearances.ContainsKey(cat.CatId)) throw new InvalidDataException($"Scene {number} uses a cat that is not in the cast.");
            OneOf(cat.Shot, Shots, $"scene {number} cat shot");
            OneOf(cat.Place, Places(scene.Layout), $"scene {number} cat place for {scene.Layout}");
            OneOf(cat.Action, Actions, $"scene {number} cat action");
            Text(cat.Bubble, 36, $"scene {number} cat bubble", empty: true);
            if (scene.Layout == "journey" && cat.Shot != "face")
                throw new InvalidDataException($"Scene {number}: a cat travelling a journey uses the face shot.");
            appearances[cat.CatId]++;
        }
        var words = Scenes.Sum(s => Words(s.Narration).Length);
        if (words is < 120 or > 210)
            throw new InvalidDataException($"Narration has {words} words; use 120-210 total spoken words, ideally 150-175.");
        var catScenes = appearances.Values.Sum();
        if (catScenes < 3 || appearances.Values.Any(n => n < 2))
            throw new InvalidDataException($"Cats appear in {catScenes} scenes; weave them through at least 3 scenes and give every cast member 2+ appearances.");
        if (!Scenes.Any(s => s.Layout is "contrast" or "journey"))
            throw new InvalidDataException("Include a concrete before/after contrast or a journey through the changed flow.");
        VideoRenderer.CheckLayout(this);
    }

    static object Choice(string[] values, string description) => new { type = "string", @enum = values, description };
    static object Str(string description) => new { type = "string", description };

    internal static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "title", "cast", "scenes" },
        properties = new
        {
            title = Str("Short title, at most 80 characters. Metadata only; not drawn on screen."),
            cast = new
            {
                type = "array", description = "1-3 real-cat characters; every cast member appears in 2+ scenes.",
                items = new
                {
                    type = "object", additionalProperties = false, required = new[] { "catId", "role" },
                    properties = new
                    {
                        catId = Str("A supplied catalog ID."),
                        role = Str("Who this cat plays in THIS PR's story, at most 60 characters."),
                    },
                },
            },
            scenes = new
            {
                type = "array", description = "6-10 scenes; 120-210 total narration words. The host enforces all limits.",
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "layout", "kicker", "narration", "beats", "cat", "camera" },
                    properties = new
                    {
                        layout = Choice(Layouts, "hook: 1-2 beats; term: 2 (word, plain meaning); contrast: 2 (before, after); journey: 2-4 stations; list: 2-4 points."),
                        kicker = Str("Tiny corner label, at most 40 characters; never repeated by a beat."),
                        narration = Str("Spoken words, at most 320 characters; captioned automatically."),
                        beats = new
                        {
                            type = "array", description = "Visual labels revealed in sync with narration.",
                            items = new
                            {
                                type = "object", additionalProperties = false, required = new[] { "text", "cue", "tone" },
                                properties = new
                                {
                                    text = Str("At most 44 characters; a condensed visual, not a copy of the narration."),
                                    cue = Str("1-4 words quoted exactly from this scene's narration; the beat animates in as they are spoken. Cues in order, without reusing matched words. Repeated cues need later occurrences."),
                                    tone = Choice(Tones, "Color meaning: old behavior, new behavior, alert/caveat, or plain."),
                                },
                            },
                        },
                        cat = new
                        {
                            type = "object", additionalProperties = false,
                            required = new[] { "catId", "shot", "place", "action", "bubble" },
                            description = "A cast member acting inside this scene, or every field empty for no cat.",
                            properties = new
                            {
                                catId = Str("A cast catId, or empty."),
                                shot = Choice([.. Shots, ""], "portrait: the real photo as a tilted card; face: circular close-up token."),
                                place = Choice(["left", "right", "before", "after", "travel", ""], "hook/term/list: left|right; contrast: before|after (sits on that side); journey: travel (rides between stations)."),
                                action = Choice([.. Actions, ""], "Arrival: enter slides in, pop bounces, peek rises from below, drift slowly pushes into the photo."),
                                bubble = Str("Optional in-character speech bubble, at most 36 characters, or empty."),
                            },
                        },
                        camera = Choice(Cameras, "Whole-stage motion over the scene."),
                    },
                },
            },
        },
    });
}
