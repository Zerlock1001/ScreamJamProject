using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace ScreamJam.Debugging
{
    public sealed partial class DebugConsole
    {
        private bool _ipLookupBusy;
        private string _ipLookupError;
        private IpLocation _ipLookupResult;

        private void BeginIpLookup()
        {
            StartIpLookup(null);
        }

        private void BeginSayIp()
        {
            StartIpLookup(result =>
            {
                if (result == null || string.IsNullOrEmpty(result.City))
                {
                    AddCommandLine("City not found in the IP lookup result.", LogType.Warning);
                    return;
                }

                PetDialogue.SayOnce("Do you live in " + result.City.Trim() + "?");
            });
        }

        private void StartIpLookup(Action<IpLocation> onResult)
        {
            if (_ipLookupBusy)
            {
                AddCommandLine("IP lookup is already running.", LogType.Warning);
                return;
            }

            StartCoroutine(LookupIpLocation(onResult));
        }

        private IEnumerator LookupIpLocation(Action<IpLocation> onResult)
        {
            _ipLookupBusy = true;
            _ipLookupError = null;
            _ipLookupResult = null;

            AddCommandLine("Looking up public IP...");

            try
            {
                IpLocation best = null;

                yield return RequestIpLocation("https://ipwho.is/", ParseIpWho);
                if (_ipLookupResult != null)
                    best = _ipLookupResult;

                if (best == null || string.IsNullOrEmpty(best.City))
                {
                    _ipLookupResult = null;
                    yield return RequestIpLocation("https://ipapi.co/json/", ParseIpApi);
                    if (_ipLookupResult != null && (best == null || string.IsNullOrEmpty(best.City)))
                        best = _ipLookupResult;
                }

                if (best == null || string.IsNullOrEmpty(best.City))
                {
                    _ipLookupResult = null;
                    yield return RequestIpLocation("https://ipinfo.io/json", ParseIpInfo);
                    if (_ipLookupResult != null && (best == null || string.IsNullOrEmpty(best.City)))
                        best = _ipLookupResult;
                }

                _ipLookupResult = best;

                if (_ipLookupResult == null)
                {
                    AddCommandLine(
                        "IP lookup failed: " + (_ipLookupError ?? "unknown error"),
                        LogType.Error);
                    yield break;
                }

                IpLocation result = _ipLookupResult;

                if (onResult != null)
                {
                    onResult(result);
                    yield break;
                }

                AddCommandLine("City: " + (string.IsNullOrEmpty(result.City) ? "unknown" : result.City));
            }
            finally
            {
                _ipLookupBusy = false;
            }
        }

        private IEnumerator RequestIpLocation(string url, Func<string, IpLocation> parse)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 8;
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("User-Agent", "ScreamJamProject/1.0");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    _ipLookupError = url + ": " + (request.error ?? "request failed");
                    yield break;
                }

                IpLocation result;
                try
                {
                    result = parse(request.downloadHandler.text);
                }
                catch (Exception exception)
                {
                    _ipLookupError = url + ": " + exception.Message;
                    yield break;
                }

                if (result == null || string.IsNullOrEmpty(result.Ip))
                {
                    _ipLookupError = url + ": invalid response";
                    yield break;
                }

                _ipLookupResult = result;
            }
        }

        private static IpLocation ParseIpWho(string body)
        {
            IpWhoResponse payload = JsonUtility.FromJson<IpWhoResponse>(body);
            if (payload == null || !payload.success)
                return null;

            return new IpLocation
            {
                Ip = payload.ip,
                City = payload.city
            };
        }

        private static IpLocation ParseIpApi(string body)
        {
            IpApiResponse payload = JsonUtility.FromJson<IpApiResponse>(body);
            if (payload == null || string.IsNullOrEmpty(payload.ip))
                return null;

            return new IpLocation
            {
                Ip = payload.ip,
                City = payload.city
            };
        }

        private static IpLocation ParseIpInfo(string body)
        {
            IpInfoResponse payload = JsonUtility.FromJson<IpInfoResponse>(body);
            if (payload == null || string.IsNullOrEmpty(payload.ip) || payload.bogon)
                return null;

            return new IpLocation
            {
                Ip = payload.ip,
                City = payload.city
            };
        }

        private sealed class IpLocation
        {
            public string Ip;
            public string City;
        }

#pragma warning disable 0649
        [Serializable]
        private sealed class IpWhoResponse
        {
            public bool success;
            public string ip;
            public string city;
        }

        [Serializable]
        private sealed class IpApiResponse
        {
            public string ip;
            public string city;
        }

        [Serializable]
        private sealed class IpInfoResponse
        {
            public string ip;
            public string city;
            public bool bogon;
        }
#pragma warning restore 0649
    }
}
