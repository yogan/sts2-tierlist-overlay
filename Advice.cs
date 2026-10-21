using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

namespace TierListOverlay;

/// <summary>
/// Baalorlord's and Jorbs' advice on events, from Untapped.gg, cached next to the mod DLL like
/// the tier lists. Shown, and fetched, only with a working Untapped.gg login, like the other
/// things that come from there.
/// </summary>
internal static class Advice
{
    private const string ApiUrl = "https://sts2.untapped.gg/api/v1/commentary/events";

    // The advice changes rarely.
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    // Event ID ("ABYSSAL_BATHS") -> author -> text.
    private static volatile Dictionary<string, Dictionary<string, string>> _events = new();
    private static string _cachePath = "";
    private static bool _started;

    // Not .json: the game treats every .json file below mods/ as a mod manifest.
    public static void Load(string cacheDir) => _cachePath = Path.Combine(cacheDir, "event-advice.cache");

    /// <summary>Reads the cache, then fetches fresh advice if it is old, once the login works.</summary>
    public static void OnProcessFrame()
    {
        if (_started || _cachePath == "" || !Settings.EventAdvice)
            return;
        _started = true;
        Task.Run(async () =>
        {
            try
            {
                if (File.Exists(_cachePath))
                    Publish(Parse(await File.ReadAllTextAsync(_cachePath)), "from cache");
                if (File.Exists(_cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < RefreshAfter)
                    return;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var json = await http.GetStringAsync(ApiUrl);
                var events = Parse(json);
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                await File.WriteAllTextAsync(_cachePath, json);
                Publish(events, "fetched");
            }
            catch (Exception ex)
            {
                GD.Print($"[TierListOverlay] Fetching event advice failed ({ex.Message}), keeping the cached one");
            }
        });
    }

    /// <summary>The chosen tier list author's advice on the event, else the other author's.</summary>
    public static (string Author, string Text)? For(string eventId)
    {
        if (!_events.TryGetValue(eventId, out var byAuthor))
            return null;
        var chosen = Settings.Author;
        if (byAuthor.TryGetValue(chosen, out var text))
            return (chosen, text);
        return byAuthor.Select(a => ((string, string)?)(a.Key, a.Value)).FirstOrDefault();
    }

    private static void Publish(Dictionary<string, Dictionary<string, string>> events, string how)
    {
        _events = events;
        GD.Print($"[TierListOverlay] Event advice on {events.Count} events {how}");
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string json)
    {
        var into = new Dictionary<string, Dictionary<string, string>>();
        using var doc = JsonDocument.Parse(json);
        foreach (var ev in doc.RootElement.EnumerateObject())
        {
            if (ev.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in ev.Value.EnumerateArray())
            {
                var author = entry.TryGetProperty("author", out var a) ? a.GetString() : null;
                var text = entry.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(text))
                    continue;
                if (!into.TryGetValue(ev.Name, out var byAuthor))
                    into[ev.Name] = byAuthor = new Dictionary<string, string>();
                byAuthor[author] = text;
            }
        }
        return into;
    }
}
