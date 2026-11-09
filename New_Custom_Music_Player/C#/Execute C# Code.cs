using System;
using System.Text;

public class CPHInline
{
    // Programs to ignore when reading from SMTC Bridge
    private static readonly string[] BlockedApps = new string[]
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe",
        "opera.exe", "discord.exe", "obs64.exe", "vlc.exe"
    };

    public bool Execute()
    {
        string title = "";
        string artist = "";
        string platform = "";
        string songUrl = "";

        // 1. Triggered automatically by overlay.html via Streamer.bot WebSocket
        if (CPH.TryGetArg("songTitle", out string argTitle) && !string.IsNullOrWhiteSpace(argTitle))
        {
            title = argTitle;
            CPH.TryGetArg("songArtist", out artist);
            CPH.TryGetArg("songPlatform", out platform);
            CPH.TryGetArg("songUrl", out songUrl);
        }
        // 2. Or triggered via Streamer.bot's built-in "Fetch URL" sub-actions (%ytmJson% / %smtcJson%)
        else
        {
            if (CPH.TryGetArg("ytmJson", out string ytmJson) && !string.IsNullOrWhiteSpace(ytmJson))
            {
                TryParseYouTubeMusicJson(ytmJson, out title, out artist, out platform, out songUrl);
            }

            if (string.IsNullOrWhiteSpace(title) &&
                CPH.TryGetArg("smtcJson", out string smtcJson) &&
                !string.IsNullOrWhiteSpace(smtcJson))
            {
                TryParseSmtcBridgeJson(smtcJson, out title, out artist, out platform, out songUrl);
            }
        }

        if (string.IsNullOrWhiteSpace(title))
            return true;

        if (string.IsNullOrWhiteSpace(artist))
            artist = "Unknown Artist";

        if (string.IsNullOrWhiteSpace(platform))
            platform = "Music";

        if (string.IsNullOrWhiteSpace(songUrl))
            songUrl = BuildPlatformLink(platform, artist, title);

        // Deduplicate so pausing/resuming the same track won't spam chat
        string currentSignature = (platform + "::" + artist + "::" + title).ToLowerInvariant();
        string lastAnnounced = CPH.GetGlobalVar<string>("zed_last_announced_song", false) ?? "";

        if (currentSignature == lastAnnounced)
            return true;

        CPH.SetGlobalVar("zed_last_announced_song", currentSignature, false);

        // Save current track info into a global variable for !song commands
        string formattedTrack = $"🎵 Now Playing on {platform}: {artist} - {title} | 🔗 {songUrl}";
        CPH.SetGlobalVar("zed_current_song_message", formattedTrack, false);

        // Send to Twitch Chat
        CPH.TwitchAnnounce(formattedTrack, true);

        return true;
    }

    private bool TryParseYouTubeMusicJson(string json, out string title, out string artist, out string platform, out string songUrl)
    {
        title = "";
        artist = "";
        platform = "";
        songUrl = "";

        // Check that isPaused is Explicitly false
        string isPausedStr = ExtractJsonScalar(json, "isPaused");
        if (!string.Equals(isPausedStr, "false", StringComparison.OrdinalIgnoreCase))
            return false;

        title = ExtractJsonString(json, "title");
        if (string.IsNullOrWhiteSpace(title))
            return false;

        artist = ExtractJsonString(json, "artist");
        if (string.IsNullOrWhiteSpace(artist))
            artist = "Unknown Artist";

        platform = "YouTube Music";

        string directUrl = ExtractJsonString(json, "url");
        string videoId = ExtractJsonString(json, "videoId");

        if (!string.IsNullOrWhiteSpace(directUrl))
            songUrl = directUrl;
        else if (!string.IsNullOrWhiteSpace(videoId))
            songUrl = "https://music.youtube.com/watch?v=" + UrlEncode(videoId);
        else
            songUrl = BuildPlatformLink(platform, artist, title);

        return true;
    }

    private bool TryParseSmtcBridgeJson(string json, out string title, out string artist, out string platform, out string songUrl)
    {
        title = "";
        artist = "";
        platform = "";
        songUrl = "";

        // Split sessions by "source_app_id"
        string marker = "\"source_app_id\"";
        int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        while (idx != -1)
        {
            int nextIdx = json.IndexOf(marker, idx + marker.Length, StringComparison.OrdinalIgnoreCase);
            string chunk = nextIdx != -1 ? json.Substring(idx, nextIdx - idx) : json.Substring(idx);

            string appId = ExtractJsonString(chunk, "source_app_id");
            string status = ExtractJsonScalar(chunk, "PlaybackStatus");

            if (!IsBlockedApp(appId) && status == "4")
            {
                string foundTitle = ExtractJsonString(chunk, "Title");
                if (!string.IsNullOrWhiteSpace(foundTitle))
                {
                    title = foundTitle;
                    artist = ExtractJsonString(chunk, "Artist");
                    if (string.IsNullOrWhiteSpace(artist))
                        artist = "Unknown Artist";

                    platform = DetectPlatformName(appId);
                    songUrl = BuildPlatformLink(platform, artist, title);
                    return true;
                }
            }

            idx = nextIdx;
        }

        return false;
    }

    private bool IsBlockedApp(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        string lower = sourceAppId.ToLowerInvariant();
        for (int i = 0; i < BlockedApps.Length; i++)
        {
            string clean = BlockedApps[i].ToLowerInvariant().Replace(".exe", "").Trim();
            if (clean.Length != 0 && lower.IndexOf(clean, StringComparison.Ordinal) != -1)
                return true;
        }
        return false;
    }

    private string DetectPlatformName(string appId)
    {
        string s = (appId ?? "").ToLowerInvariant();
        if (s.IndexOf("spotify", StringComparison.Ordinal) != -1) return "Spotify";
        if (s.IndexOf("amazon", StringComparison.Ordinal) != -1) return "Amazon Music";
        if (s.IndexOf("apple", StringComparison.Ordinal) != -1 || s.IndexOf("itunes", StringComparison.Ordinal) != -1) return "Apple Music";
        if (s.IndexOf("tidal", StringComparison.Ordinal) != -1) return "Tidal";
        if (s.IndexOf("soundcloud", StringComparison.Ordinal) != -1) return "SoundCloud";
        if (s.IndexOf("youtube", StringComparison.Ordinal) != -1) return "YouTube Music";
        return "Music Player";
    }

    private string BuildPlatformLink(string platform, string artist, string title)
    {
        string query = UrlEncode((artist + " " + title).Trim());
        string p = (platform ?? "").ToLowerInvariant();

        if (p.IndexOf("spotify", StringComparison.Ordinal) != -1) return "https://open.spotify.com/search/" + query;
        if (p.IndexOf("amazon", StringComparison.Ordinal) != -1) return "https://music.amazon.com/search/" + query;
        if (p.IndexOf("apple", StringComparison.Ordinal) != -1) return "https://music.apple.com/us/search?term=" + query;
        if (p.IndexOf("tidal", StringComparison.Ordinal) != -1) return "https://listen.tidal.com/search?q=" + query;
        if (p.IndexOf("soundcloud", StringComparison.Ordinal) != -1) return "https://soundcloud.com/search?q=" + query;
        return "https://music.youtube.com/search?q=" + query;
    }

    // Pure mscorlib URL Encoder (replaces System.Uri.EscapeDataString)
    private string UrlEncode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        StringBuilder sb = new StringBuilder(bytes.Length * 2);

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            char c = (char)b;
            bool isSafe = (c >= 'a' && c <= 'z') ||
                          (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') ||
                          c == '-' || c == '_' || c == '.' || c == '~';

            if (isSafe)
                sb.Append(c);
            else if (c == ' ')
                sb.Append("%20");
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // Pure mscorlib JSON String Extractor (replaces Newtonsoft JObject/JToken)
    private string ExtractJsonString(string json, string key)
    {
        string pattern = "\"" + key + "\"";
        int keyIdx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (keyIdx == -1) return "";

        int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
        if (colonIdx == -1) return "";

        int pos = colonIdx + 1;
        while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

        if (pos >= json.Length || json[pos] != '"') return "";
        pos++; // Skip opening quote

        StringBuilder sb = new StringBuilder();
        while (pos < json.Length)
        {
            char c = json[pos++];
            if (c == '\\' && pos < json.Length)
            {
                char esc = json[pos++];
                if (esc == '"' || esc == '\\' || esc == '/') sb.Append(esc);
                else if (esc == 'n') sb.Append(' ');
                else if (esc == 'r' || esc == 't') continue;
                else sb.Append(esc);
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Pure mscorlib JSON Boolean/Number Extractor
    private string ExtractJsonScalar(string json, string key)
    {
        string pattern = "\"" + key + "\"";
        int keyIdx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (keyIdx == -1) return "";

        int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
        if (colonIdx == -1) return "";

        int pos = colonIdx + 1;
        while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

        int start = pos;
        while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']' && !char.IsWhiteSpace(json[pos]))
        {
            pos++;
        }
        return json.Substring(start, pos - start).Trim();
    }
}