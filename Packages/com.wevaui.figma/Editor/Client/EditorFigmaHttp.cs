using System;
using System.Net;
using Weva.Figma.Client;

namespace Weva.Figma.EditorTools
{
    /// <summary>
    /// Editor-time <see cref="IFigmaHttp"/> backed by <see cref="WebClient"/>
    /// (synchronous, real sockets — no Unity player-loop pumping needed, which a
    /// main-thread UnityWebRequest spin would deadlock on). The Figma token is
    /// only attached to api.figma.com requests, never to the S3 image CDN.
    ///
    /// NEEDS UNITY VALIDATION: written without a Unity compile/run available.
    /// </summary>
    public sealed class EditorFigmaHttp : IFigmaHttp
    {
        const string TokenHeader = "X-Figma-Token";

        public bool TryGetString(string url, string token, out string body, out string error)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    AddToken(wc, url, token);
                    body = wc.DownloadString(url);
                    error = null;
                    return true;
                }
            }
            catch (Exception e)
            {
                body = null;
                error = Describe(e);
                return false;
            }
        }

        public bool TryGetBytes(string url, string token, out byte[] data, out string error)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    AddToken(wc, url, token);
                    data = wc.DownloadData(url);
                    error = null;
                    return true;
                }
            }
            catch (Exception e)
            {
                data = null;
                error = Describe(e);
                return false;
            }
        }

        static void AddToken(WebClient wc, string url, string token)
        {
            if (!string.IsNullOrEmpty(token) && url != null
                && url.StartsWith("https://api.figma.com", StringComparison.Ordinal))
                wc.Headers.Add(TokenHeader, token);
        }

        static string Describe(Exception e)
        {
            if (e is WebException we && we.Response is HttpWebResponse resp)
                return $"{(int)resp.StatusCode} {resp.StatusDescription}";
            return e.Message;
        }
    }
}
