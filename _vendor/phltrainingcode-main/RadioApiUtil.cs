using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Shared helpers for phlstats radio HTTP. UnityWebRequest TLS is flaky on some Linux
/// dedicated hosts — we bypass cert checks for this public API and fall back to WebClient.
/// </summary>
public static class RadioApiUtil
{
    public const string DefaultApiBase = "https://phlstats.com/radio/api";

    private static readonly Regex IdRegex = new Regex(
        "\\\"id\\\"\\s*:\\s*\\\"([^\\\"\\\\]+)\\\"",
        RegexOptions.CultureInvariant);

    private static readonly Regex TitleRegex = new Regex(
        "\\\"title\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
        RegexOptions.CultureInvariant);

    private sealed class AcceptAllCertificates : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }

    [Serializable]
    private sealed class PlaylistResponse
    {
        public TrackDto[] tracks;
    }

    [Serializable]
    private sealed class TrackDto
    {
        public string id;
        public string title;
    }

    public static void ConfigureRequest(UnityWebRequest req)
    {
        if (req == null)
            return;

        req.timeout = 15;
        req.certificateHandler = new AcceptAllCertificates();
        req.disposeCertificateHandlerOnDispose = true;
    }

    /// <summary>Long MP3 streams keep downloading during playback — do not use the 15s API timeout.</summary>
    public static void ConfigureStreamRequest(UnityWebRequest req)
    {
        if (req == null)
            return;

        req.timeout = 0;
        req.certificateHandler = new AcceptAllCertificates();
        req.disposeCertificateHandlerOnDispose = true;
    }

    public static bool TryParsePlaylist(string json, List<string> ids, List<string> titles = null)
    {
        ids?.Clear();
        titles?.Clear();
        if (string.IsNullOrWhiteSpace(json) || ids == null)
            return false;

        try
        {
            PlaylistResponse parsed = JsonUtility.FromJson<PlaylistResponse>(json);
            if (parsed?.tracks != null && parsed.tracks.Length > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < parsed.tracks.Length; i++)
                {
                    TrackDto dto = parsed.tracks[i];
                    if (dto == null || string.IsNullOrWhiteSpace(dto.id))
                        continue;
                    string id = dto.id.Trim();
                    if (!seen.Add(id))
                        continue;
                    ids.Add(id);
                    titles?.Add(string.IsNullOrWhiteSpace(dto.title) ? id : dto.title.Trim());
                }

                if (ids.Count > 0)
                    return true;
            }
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Radio JsonUtility playlist parse failed: " + ex.Message);
        }

        // Fallback: pull id fields (Unity JsonUtility arrays are unreliable on some builds).
        MatchCollection idMatches = IdRegex.Matches(json);
        if (idMatches.Count == 0)
            return false;

        MatchCollection titleMatches = titles != null ? TitleRegex.Matches(json) : null;
        var seen2 = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < idMatches.Count; i++)
        {
            string id = idMatches[i].Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(id) || id == "tracks" || !seen2.Add(id))
                continue;
            // Skip health-like noise
            if (id == "ok" || id == "bucketConfigured")
                continue;
            ids.Add(id);
            if (titles != null)
            {
                string title = id;
                if (titleMatches != null && i < titleMatches.Count)
                    title = UnescapeJson(titleMatches[i].Groups[1].Value);
                titles.Add(title);
            }
        }

        return ids.Count > 0;
    }

    /// <summary>Blocking HTTPS GET — used as dedicated-server fallback when UWR fails.</summary>
    public static bool TryDownloadString(string url, out string body, out string error)
    {
        body = null;
        error = null;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = AcceptAnyCert;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "FlamiePrac-Radio/1.0";

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    error = "HTTP " + (int)resp.StatusCode;
                    return false;
                }

                return !string.IsNullOrWhiteSpace(body);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ServicePointManager.ServerCertificateValidationCallback = null;
        }
    }

    public static bool TryLoadPlaylistFile(List<string> ids, List<string> titles = null)
    {
        ids?.Clear();
        titles?.Clear();
        if (ids == null)
            return false;

        string[] candidates =
        {
            Path.Combine(Directory.GetCurrentDirectory(), "config", "radio_playlist.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "radio_playlist.example.json")
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            string path = candidates[i];
            if (!File.Exists(path))
                continue;

            try
            {
                string json = File.ReadAllText(path);
                if (TryParsePlaylist(json, ids, titles))
                {
                    FlamieLog.Info("[FlamiePrac] Radio playlist file: " + path + " (" + ids.Count + " track(s)).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                FlamieLog.Warn("[FlamiePrac] Radio playlist file failed (" + path + "): " + ex.Message);
            }
        }

        return false;
    }

    private static bool AcceptAnyCert(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate certificate,
        System.Security.Cryptography.X509Certificates.X509Chain chain,
        System.Net.Security.SslPolicyErrors sslPolicyErrors) => true;

    private static string UnescapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}
