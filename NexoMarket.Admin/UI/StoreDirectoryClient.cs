using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using NexoMarket.Admin.Data;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Cliente muy liviano para el directorio central de NexoMarket.
    /// Usa HttpWebRequest para conservar compatibilidad con .NET Framework 4.0 / Windows 8.
    /// </summary>
    public sealed class StoreDirectoryClient
    {
        private readonly AppDataStore _store;
        public StoreDirectoryClient(AppDataStore store) { _store = store; }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(CentralUrl()); }
        }

        public bool PublishStore(string publicUrl)
        {
            if (!IsConfigured) return false;
            string endpoint = Normalize(CentralUrl()) + "/api/stores/register";
            string syncKey = (_store.GetSetting("central_sync_key", "") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(syncKey)) { syncKey = Guid.NewGuid().ToString("N"); _store.SetSetting("central_sync_key", syncKey); }
            string resolvedPublicUrl = (publicUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(resolvedPublicUrl) || resolvedPublicUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0)
                resolvedPublicUrl = Normalize(CentralUrl()) + "/store/" + Uri.EscapeDataString(_store.StoreId);
            string body = Form(new Dictionary<string, string>
            {
                { "storeId", _store.StoreId },
                { "syncKey", syncKey },
                { "name", _store.GetSetting("store_name", "NexoMarket") },
                { "legalName", _store.GetSetting("store_legal_name", "") },
                { "category", _store.GetSetting("store_category", "") },
                { "address", _store.GetSetting("store_address", "") },
                { "city", _store.GetSetting("store_city", "") },
                { "province", _store.GetSetting("store_province", "") },
                { "description", _store.GetSetting("store_description", "") },
                { "logo", _store.GetSetting("store_logo", "") },
                { "slug", _store.GetSetting("store_slug", "") },
                { "publicUrl", resolvedPublicUrl },
                { "active", _store.GetSetting("store_web_active", "0") },
                { "delivery", _store.GetSetting("delivery_enabled", "1") },
                { "pickup", _store.GetSetting("pickup_enabled", "1") },
                { "latitude", _store.GetSetting("store_latitude", "") },
                { "longitude", _store.GetSetting("store_longitude", "") },
                { "updatedAt", DateTime.UtcNow.ToString("o") }
            });
            string response = Request(endpoint, "POST", body);
            return response != null && response.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
        }

        public List<RemoteStore> GetStores() { return GetStores("", 0d, 0d, false); }

        public List<RemoteStore> GetStores(string search, double latitude, double longitude, bool hasCoordinates)
        {
            List<RemoteStore> result = new List<RemoteStore>();
            if (!IsConfigured) return result;
            try
            {
                string endpoint = Normalize(CentralUrl()) + "/api/stores";
                List<string> qs = new List<string>();
                if (!string.IsNullOrWhiteSpace(search)) qs.Add("q=" + Uri.EscapeDataString(search));
                if (hasCoordinates) { qs.Add("lat=" + latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)); qs.Add("lon=" + longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                if (qs.Count > 0) endpoint += "?" + string.Join("&", qs.ToArray());
                string response = Request(endpoint, "GET", null);
                if (string.IsNullOrEmpty(response)) return result;
                using (StringReader reader = new StringReader(response))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("STORE|", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] p = line.Split('|');
                        if (p.Length < 12) continue;
                        RemoteStore s = new RemoteStore();
                        s.StoreId = Decode(p[1]); s.Name = Decode(p[2]); s.PublicUrl = Decode(p[3]);
                        s.City = Decode(p[4]); s.Province = Decode(p[5]); s.Category = Decode(p[6]);
                        s.Latitude = ParseDouble(Decode(p[7])); s.Longitude = ParseDouble(Decode(p[8]));
                        s.Active = Decode(p[9]) == "1"; s.Delivery = Decode(p[10]) == "1"; s.Pickup = Decode(p[11]) == "1";
                        s.UpdatedAt = Decode(p.Length > 12 ? p[12] : "");
                        s.DistanceKm = ParseDouble(Decode(p.Length > 13 ? p[13] : ""));
                        if (s.Active) result.Add(s);
                    }
                }
            }
            catch { }
            return result;
        }


        public LocationResult Geocode(string location)
        {
            LocationResult result = new LocationResult();
            if (string.IsNullOrWhiteSpace(location)) return result;
            try
            {
                string response = null;
                if (IsConfigured)
                {
                    string endpoint = Normalize(CentralUrl()) + "/api/geocode?q=" + Uri.EscapeDataString(location);
                    response = Request(endpoint, "GET", null);
                }
                if (string.IsNullOrEmpty(response))
                {
                    string endpoint = "https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ar&q=" + Uri.EscapeDataString(location + ", Argentina");
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint); req.Method = "GET"; req.Timeout = 8000; req.UserAgent = "NexoMarket/3.5.2 (location lookup)";
                    using (WebResponse resp = req.GetResponse()) using (StreamReader rr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) response = rr.ReadToEnd();
                    string latValue = JsonValue(response, "lat");
                    string lonValue = JsonValue(response, "lon");
                    if (!string.IsNullOrEmpty(latValue) && !string.IsNullOrEmpty(lonValue)) response = "OK|" + latValue + "|" + lonValue + "|" + location; else response = "NOT_FOUND";
                }
                string[] p = (response ?? "").Split(new[] { '|' }, 4);
                if (p.Length >= 3 && string.Equals(p[0], "OK", StringComparison.OrdinalIgnoreCase))
                {
                    double lat, lon;
                    if (double.TryParse(p[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat) && double.TryParse(p[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon))
                    { result.Success = true; result.Latitude = lat; result.Longitude = lon; result.DisplayName = p.Length > 3 ? Decode(p[3]) : location; }
                }
            }
            catch { }
            return result;
        }

        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return "";
            string marker = "\"" + key + "\"";
            int k = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return "";
            int colon = json.IndexOf(':', k + marker.Length);
            if (colon < 0) return "";
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start < json.Length && json[start] == '\"')
            {
                start++;
                int end = start;
                while (end < json.Length)
                {
                    if (json[end] == '\"' && json[end - 1] != '\\') break;
                    end++;
                }
                return end <= json.Length ? json.Substring(start, end - start) : "";
            }
            int stop = start;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}') stop++;
            return json.Substring(start, stop - start).Trim();
        }

        private string CentralUrl()
        {
            string configured = (_store.GetSetting("web_api_url", "") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(configured) || configured.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return "https://nexomarket-central.onrender.com";
            return configured;
        }

        private static string Normalize(string url)
        {
            string x = (url ?? "").Trim().TrimEnd('/'); if (x.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) x = x.Substring(0, x.Length - 4).TrimEnd('/'); return x;
        }

        private static string Form(Dictionary<string, string> values)
        {
            StringBuilder b = new StringBuilder();
            foreach (KeyValuePair<string, string> x in values)
            {
                if (b.Length > 0) b.Append('&');
                b.Append(Uri.EscapeDataString(x.Key ?? ""));
                b.Append('=');
                b.Append(Uri.EscapeDataString(x.Value ?? ""));
            }
            return b.ToString();
        }

        private static string Request(string url, string method, string body)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method; req.Timeout = 7000; req.ReadWriteTimeout = 7000;
            req.UserAgent = "NexoMarket/3.5 Windows";
            if (method == "POST")
            {
                byte[] data = Encoding.UTF8.GetBytes(body ?? "");
                req.ContentType = "application/x-www-form-urlencoded";
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            using (WebResponse resp = req.GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        private static string Decode(string value) { try { return Uri.UnescapeDataString(value ?? ""); } catch { return value ?? ""; } }
        private static double ParseDouble(string value) { double d; return double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0d; }
    }

    public sealed class LocationResult
    {
        public bool Success; public double Latitude; public double Longitude; public string DisplayName = "";
    }

    public sealed class RemoteStore
    {
        public string StoreId = "";
        public string Name = "";
        public string PublicUrl = "";
        public string City = "";
        public string Province = "";
        public string Category = "";
        public string UpdatedAt = "";
        public double Latitude;
        public double Longitude;
        public double DistanceKm;
        public bool Active;
        public bool Delivery;
        public bool Pickup;
    }
}
