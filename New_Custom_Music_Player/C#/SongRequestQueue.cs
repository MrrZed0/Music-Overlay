using System;
using System.Text;
using System.Reflection;

public class CPHInline
{
    // Base URL for CodeMathis/youtube-music API Server
    private const string YtmBaseUrl = "http://127.0.0.1:26538";

    // Options:
    // "INSERT_AT_END"                 -> Adds requested song to the end of your active queue/playlist
    // "INSERT_AFTER_CURRENT_VIDEO"    -> Plays the requested song immediately after the current track finishes
    private const string QueueInsertMode = "INSERT_AT_END";

    public bool Execute()
    {
        // 1. Grab the viewer's username and input text from the Channel Point Redemption (or !sr command)
        string user = "Viewer";
        if (CPH.TryGetArg("user", out string argUser) && !string.IsNullOrWhiteSpace(argUser))
            user = argUser;
        else if (CPH.TryGetArg("userName", out string argUserName) && !string.IsNullOrWhiteSpace(argUserName))
            user = argUserName;

        string rawInput = "";
        if (CPH.TryGetArg("rawInput", out string argRaw) && !string.IsNullOrWhiteSpace(argRaw))
            rawInput = argRaw.Trim();
        else if (CPH.TryGetArg("message", out string argMsg) && !string.IsNullOrWhiteSpace(argMsg))
            rawInput = argMsg.Trim();

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            CPH.SendMessage($"@{user}, please enter an Artist - Song Name (or YouTube Music link) to request a song!", true);
            return true;
        }

        // 2. Extract videoId if the viewer pasted a direct link, otherwise search for the track
        string videoId = TryExtractVideoIdFromUrl(rawInput);

        if (string.IsNullOrEmpty(videoId))
        {
            videoId = SearchViaYtmApiServer(rawInput);
        }

        if (string.IsNullOrEmpty(videoId))
        {
            videoId = SearchViaYouTubeWebFallback(rawInput);
        }

        if (string.IsNullOrEmpty(videoId))
        {
            CPH.SendMessage($"❌ @{user}, couldn't find a matching track for \"{rawInput}\".", true);
            return true;
        }

        // 3. Add the track to the active YouTube Music playlist / queue
        string queuePayload = "{\"videoId\":\"" + EscapeJson(videoId) + "\",\"insertPosition\":\"" + QueueInsertMode + "\"}";
        bool queuedOk = HttpPostJson(YtmBaseUrl + "/api/v1/queue", queuePayload, out string queueResponse);

        if (!queuedOk)
        {
            CPH.SendMessage($"⚠️ @{user}, couldn't reach the YouTube Music player queue. Make sure the player is open!", true);
            return true;
        }

        // 4. Fetch the clean Song Title & Artist name for the Twitch chat confirmation
        string displayTrack = rawInput;
        if (TryGetCleanTrackMetadata(videoId, out string cleanArtist, out string cleanTitle))
        {
            displayTrack = string.IsNullOrWhiteSpace(cleanArtist)
                ? cleanTitle
                : $"{cleanArtist} - {cleanTitle}";
        }

        string songLink = "https://music.youtube.com/watch?v=" + videoId;
        string positionLabel = QueueInsertMode == "INSERT_AFTER_CURRENT_VIDEO" ? "playing next" : "added to the playlist queue";

        CPH.SendMessage($"🎶 @{user}, \"{displayTrack}\" has been {positionLabel}! | 🔗 {songLink}", true);
        return true;
    }

    // Searches CodeMathis/youtube-music local API server: POST /api/v1/search
    private string SearchViaYtmApiServer(string query)
    {
        string body = "{\"query\":\"" + EscapeJson(query) + "\"}";
        if (!HttpPostJson(YtmBaseUrl + "/api/v1/search", body, out string responseJson))
            return "";

        return ExtractFirstVideoId(responseJson);
    }

    // Fallback search if /api/v1/search returns empty
    private string SearchViaYouTubeWebFallback(string query)
    {
        string searchUrl = "https://www.youtube.com/results?search_query=" + UrlEncode(query + " audio");
        if (!HttpGet(searchUrl, out string html))
            return "";

        return ExtractFirstVideoId(html);
    }

    // Resolves the exact Song Title & Artist from YouTube oEmbed
    private bool TryGetCleanTrackMetadata(string videoId, out string artist, out string title)
    {
        artist = "";
        title = "";
        string oembedUrl = "https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v=" + videoId + "&format=json";
        if (!HttpGet(oembedUrl, out string json) || string.IsNullOrWhiteSpace(json))
            return false;

        title = ExtractJsonString(json, "title");
        artist = ExtractJsonString(json, "author_name");

        if (!string.IsNullOrWhiteSpace(artist))
        {
            // Strip YouTube's auto-generated " - Topic" suffix from artist channels
            int topicIdx = artist.IndexOf(" - Topic", StringComparison.OrdinalIgnoreCase);
            if (topicIdx != -1)
                artist = artist.Substring(0, topicIdx).Trim();
        }

        return !string.IsNullOrWhiteSpace(title);
    }

    // =========================================================================
    // ZERO-REFERENCE HTTP ENGINE (Uses mscorlib Reflection on System.Net.WebClient)
    // =========================================================================
    private Type GetWebClientType()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type t = assemblies[i].GetType("System.Net.WebClient", false);
            if (t != null) return t;
        }
        try
        {
            Assembly sysAsm = Assembly.Load("System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            if (sysAsm != null) return sysAsm.GetType("System.Net.WebClient", false);
        }
        catch { }
        return null;
    }

    private bool HttpPostJson(string url, string jsonBody, out string responseText)
    {
        responseText = "";
        object wc = null;
        try
        {
            Type wcType = GetWebClientType();
            if (wcType == null) return false;

            wc = Activator.CreateInstance(wcType);

            PropertyInfo headersProp = wcType.GetProperty("Headers");
            if (headersProp != null)
            {
                object headers = headersProp.GetValue(wc, null);
                if (headers != null)
                {
                    MethodInfo setHeader = headers.GetType().GetMethod("Add", new Type[] { typeof(string), typeof(string) });
                    if (setHeader != null)
                    {
                        setHeader.Invoke(headers, new object[] { "Content-Type", "application/json" });
                        setHeader.Invoke(headers, new object[] { "Accept", "application/json" });
                    }
                }
            }

            MethodInfo uploadData = wcType.GetMethod("UploadData", new Type[] { typeof(string), typeof(string), typeof(byte[]) });
            byte[] payload = Encoding.UTF8.GetBytes(jsonBody);
            byte[] resBytes = (byte[])uploadData.Invoke(wc, new object[] { url, "POST", payload });

            if (resBytes != null && resBytes.Length > 0)
                responseText = Encoding.UTF8.GetString(resBytes);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (wc is IDisposable disp) disp.Dispose();
        }
    }

    private bool HttpGet(string url, out string responseText)
    {
        responseText = "";
        object wc = null;
        try
        {
            Type wcType = GetWebClientType();
            if (wcType == null) return false;

            wc = Activator.CreateInstance(wcType);

            PropertyInfo headersProp = wcType.GetProperty("Headers");
            if (headersProp != null)
            {
                object headers = headersProp.GetValue(wc, null);
                if (headers != null)
                {
                    MethodInfo setHeader = headers.GetType().GetMethod("Add", new Type[] { typeof(string), typeof(string) });
                    if (setHeader != null)
                        setHeader.Invoke(headers, new object[] { "User-Agent", "Mozilla/5.0" });
                }
            }

            MethodInfo downloadData = wcType.GetMethod("DownloadData", new Type[] { typeof(string) });
            byte[] resBytes = (byte[])downloadData.Invoke(wc, new object[] { url });

            if (resBytes != null && resBytes.Length > 0)
                responseText = Encoding.UTF8.GetString(resBytes);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (wc is IDisposable disp) disp.Dispose();
        }
    }

    // =========================================================================
    // STRING & JSON PARSING HELPERS (Pure mscorlib)
    // =========================================================================
    private string TryExtractVideoIdFromUrl(string input)
    {
        if (input.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) == -1 &&
            input.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) == -1)
            return "";

        int vIdx = input.IndexOf("v=", StringComparison.OrdinalIgnoreCase);
        if (vIdx != -1 && input.Length >= vIdx + 13)
        {
            string candidate = input.Substring(vIdx + 2, 11);
            if (IsValidVideoId(candidate)) return candidate;
        }

        int shortIdx = input.IndexOf("youtu.be/", StringComparison.OrdinalIgnoreCase);
        if (shortIdx != -1 && input.Length >= shortIdx + 20)
        {
            string candidate = input.Substring(shortIdx + 9, 11);
            if (IsValidVideoId(candidate)) return candidate;
        }

        return "";
    }

    private string ExtractFirstVideoId(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        string key = "\"videoId\"";
        int searchPos = 0;

        while (searchPos < text.Length)
        {
            int idx = text.IndexOf(key, searchPos, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) break;

            int colon = text.IndexOf(':', idx + key.Length);
            if (colon == -1) break;

            int quoteStart = text.IndexOf('"', colon + 1);
            if (quoteStart == -1) break;

            if (quoteStart + 12 < text.Length && text[quoteStart + 12] == '"')
            {
                string candidate = text.Substring(quoteStart + 1, 11);
                if (IsValidVideoId(candidate))
                    return candidate;
            }

            searchPos = quoteStart + 1;
        }

        return "";
    }

    private bool IsValidVideoId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 11) return false;
        for (int i = 0; i < 11; i++)
        {
            char c = id[i];
            bool ok = (c >= 'a' && c <= 'z') ||
                      (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') ||
                      c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

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
        pos++;

        StringBuilder sb = new StringBuilder();
        while (pos < json.Length)
        {
            char c = json[pos++];
            if (c == '\\' && pos < json.Length)
            {
                char esc = json[pos++];
                if (esc == '"' || esc == '\\' || esc == '/') sb.Append(esc);
                else if (esc == 'n') sb.Append(' ');
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

    private string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
    }

    private string UrlEncode(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
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
            if (isSafe) sb.Append(c);
            else if (c == ' ') sb.Append("%20");
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}