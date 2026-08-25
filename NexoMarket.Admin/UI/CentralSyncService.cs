using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Sincronizador central: publica catálogo/promociones y recupera pedidos pendientes.
    /// Diseñado para .NET Framework 4.x y funciona sin dejar puertos abiertos en la PC.
    /// </summary>
    public sealed class CentralSyncService : IDisposable
    {
        private readonly AppDataStore _store;
        private readonly LicenseService _license;
        private Timer _timer;
        private volatile bool _busy;
        public CentralSyncService(AppDataStore store) { _store = store; _license = new LicenseService(store.Root); }
        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(delegate { SyncOnce(); }, null, 5000, 30000);
        }
        public void Dispose() { if (_timer != null) { try { _timer.Dispose(); } catch { } _timer = null; } }
        public void SyncOnce()
        {
            if (_busy || !Enabled()) return;
            _busy = true;
            try
            {
                string configuredUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(configuredUrl) || configuredUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0) configuredUrl = "https://nexomarket-central.onrender.com";
                string baseUrl = Normalize(configuredUrl);
                if (baseUrl.Length > 0) _license.RefreshFromServer(baseUrl);
                if (baseUrl.Length == 0) return;
                PublishStore(baseUrl);
                PublishAccounts(baseUrl);
                List<Product> products = _store.GetProducts("");
                foreach (Product p in products) PublishProduct(baseUrl, p);
                List<Promotion> promotions = _store.GetPromotions();
                foreach (Promotion p in promotions) PublishPromotion(baseUrl, p);
                string pending = Request(baseUrl + "/api/orders/pending?storeId=" + Uri.EscapeDataString(_store.StoreId), "GET", null);
                foreach (Dictionary<string,string> order in ParseObjects(pending))
                {
                    string centralId = Get(order, "centralOrderId");
                    if (centralId.Length == 0 || AlreadyImported(centralId)) continue;
                    Order o = new Order();
                    o.CentralOrderId = centralId;
                    o.CustomerId = ParseLong(Get(order,"customerId"));
                    o.CustomerName = Get(order,"customerName"); o.CustomerEmail = Get(order,"customerEmail"); o.Phone = Get(order,"phone");
                    o.Fulfillment = Get(order,"fulfillment"); o.Address = Get(order,"address"); o.Notes = Get(order,"notes"); o.Status = Get(order,"status");
                    o.Total = ParseDecimal(Get(order,"total")); o.PaymentMethod = Get(order,"paymentMethod"); o.PaymentStatus = Get(order,"paymentStatus");
                    o.PaymentReference = Get(order,"paymentReference"); o.PaymentProofPath = Get(order,"paymentProofPath"); o.ShippingCost = ParseDecimal(Get(order,"shippingCost"));
                    o.TrackingNumber = Get(order,"trackingNumber"); o.Carrier = Get(order,"carrier"); o.ItemsJson = Get(order,"itemsJson"); o.BuyerMessage = Get(order,"buyerMessage");
                    o.StoreId = _store.StoreId; o.Source = "Web Central"; o.CreatedAt = ParseDate(Get(order,"createdAt"));
                    ApplyOrderStock(o.ItemsJson);
                    _store.AddOrder(o);
                    Request(baseUrl + "/api/orders/ack", "POST", Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"centralOrderId",centralId}}));
                }
                _store.SetSetting("central_sync_last", DateTime.Now.ToString("o"));
            }
            catch { }
            finally { _busy = false; }
        }
        private bool Enabled() { return true; }
        private bool AlreadyImported(string id) { foreach (Order o in _store.GetOrders("")) if (string.Equals(o.CentralOrderId,id,StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private void PublishStore(string baseUrl) { try { new StoreDirectoryClient(_store).PublishStore(_store.GetSetting("web_public_url","")); } catch { } }

        private void PublishAccounts(string baseUrl)
        {
            try
            {
                foreach (var u in _store.GetWebUsers())
                {
                    Request(baseUrl+"/api/accounts/upsert","POST",Form(new Dictionary<string,string>{{"id",u.Id.ToString(CultureInfo.InvariantCulture)},{"name",u.Name},{"email",u.Email},{"phone",u.Phone},{"role",u.Role},{"storeId",u.StoreId},{"salt",u.Salt},{"passwordHash",u.PasswordHash},{"createdAt",u.CreatedAt.ToUniversalTime().ToString("o")}}));
                }
            }
            catch { }
        }
        private void PublishProduct(string baseUrl, Product p)
        {
            Request(baseUrl+"/api/products/publish","POST",Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"productId",p.Id.ToString(CultureInfo.InvariantCulture)},{"name",p.Name},{"category",p.Category},{"description",p.Description},{"price",p.Price.ToString(CultureInfo.InvariantCulture)},{"salePrice",p.SalePrice.ToString(CultureInfo.InvariantCulture)},{"stock",p.Stock.ToString(CultureInfo.InvariantCulture)},{"minimumStock",p.MinimumStock.ToString(CultureInfo.InvariantCulture)},{"sku",p.SKU},{"brand",p.Brand},{"size",p.Size},{"color",p.Color},{"active",p.Active?"1":"0"},{"onlineEnabled",p.OnlineEnabled?"1":"0"},{"imagePath",p.ImagePath},{"slug",p.Slug},{"publicDescription",p.PublicDescription},{"updatedAt",DateTime.UtcNow.ToString("o")}}));
        }
        private void PublishPromotion(string baseUrl, Promotion p)
        { Request(baseUrl+"/api/promotions/publish","POST",Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"promotionId",p.Id.ToString(CultureInfo.InvariantCulture)},{"name",p.Name},{"productIds",p.ProductIds},{"promotionalPrice",p.PromotionalPrice.ToString(CultureInfo.InvariantCulture)},{"active",p.Active?"1":"0"},{"from",p.From.ToString("o")},{"to",p.To.ToString("o")},{"updatedAt",DateTime.UtcNow.ToString("o")}})); }

        private void ApplyOrderStock(string itemsJson)
        {
            if (string.IsNullOrWhiteSpace(itemsJson)) return;
            foreach (Match m in Regex.Matches(itemsJson, @"""id""\s*:\s*""([^""]+)""[^}]*?""qty""\s*:\s*(\d+)", RegexOptions.IgnoreCase))
            {
                string id=m.Groups[1].Value;
                if(id.StartsWith("promo:",StringComparison.OrdinalIgnoreCase))continue;
                int qty; if(!int.TryParse(m.Groups[2].Value,out qty)||qty<1)continue;
                long pid; if(!long.TryParse(id,out pid))continue;
                Product p=_store.GetProducts("").Find(x=>x.Id==pid);
                if(p==null)continue;
                p.Stock=Math.Max(0,p.Stock-qty);
                _store.SaveProduct(p);
            }
        }

        private static string Get(Dictionary<string,string> d,string k){string v;return d!=null&&d.TryGetValue(k,out v)?v:"";}
        private static string Normalize(string u){string v=(u??"").Trim().TrimEnd('/');if(v.EndsWith("/api",StringComparison.OrdinalIgnoreCase))v=v.Substring(0,v.Length-4).TrimEnd('/');return v;}
        private static string Form(Dictionary<string,string> v){StringBuilder b=new StringBuilder();foreach(KeyValuePair<string,string> x in v){if(b.Length>0)b.Append('&');b.Append(Uri.EscapeDataString(x.Key??""));b.Append('=').Append(Uri.EscapeDataString(x.Value??""));}return b.ToString();}
        private static string Request(string url,string method,string body){HttpWebRequest r=(HttpWebRequest)WebRequest.Create(url);r.Method=method;r.Timeout=8000;r.ReadWriteTimeout=8000;r.UserAgent="NexoMarket Central Sync/4.0";if(method=="POST"){byte[] d=Encoding.UTF8.GetBytes(body??"");r.ContentType="application/x-www-form-urlencoded";r.ContentLength=d.Length;using(Stream s=r.GetRequestStream())s.Write(d,0,d.Length);}using(WebResponse x=r.GetResponse())using(StreamReader sr=new StreamReader(x.GetResponseStream(),Encoding.UTF8))return sr.ReadToEnd();}
        private static long ParseLong(string s){long v;return long.TryParse(s,out v)?v:0;}
        private static decimal ParseDecimal(string s){decimal v;return decimal.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out v)?v:0m;}
        private static DateTime ParseDate(string s){DateTime v;return DateTime.TryParse(s,null,DateTimeStyles.RoundtripKind,out v)?v.ToLocalTime():DateTime.Now;}
        private static List<Dictionary<string,string>> ParseObjects(string json)
        {
            List<Dictionary<string,string>> result=new List<Dictionary<string,string>>();
            if(string.IsNullOrEmpty(json)) return result;
            int i=0;
            while(i<json.Length)
            {
                int a=json.IndexOf('{',i); if(a<0) break;
                int b=a+1; bool inString=false; bool escape=false; int depth=1;
                for(;b<json.Length;b++)
                {
                    char c=json[b];
                    if(inString){ if(escape) escape=false; else if(c=='\\') escape=true; else if(c=='"') inString=false; continue; }
                    if(c=='"'){inString=true;continue;}
                    if(c=='{') depth++; else if(c=='}'){depth--;if(depth==0)break;}
                }
                if(b>=json.Length) break;
                string obj=json.Substring(a+1,b-a-1);
                Dictionary<string,string> d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                int p=0;
                while(p<obj.Length)
                {
                    while(p<obj.Length && (char.IsWhiteSpace(obj[p])||obj[p]==',')) p++;
                    if(p>=obj.Length) break;
                    if(obj[p]!='"') break;
                    int k2=p+1; bool ke=false;
                    for(;k2<obj.Length;k2++){char c=obj[k2];if(ke){ke=false;continue;}if(c=='\\'){ke=true;continue;}if(c=='"')break;}
                    if(k2>=obj.Length) break;
                    string key=obj.Substring(p+1,k2-p-1); int colon=obj.IndexOf(':',k2+1); if(colon<0) break;
                    int v=colon+1; while(v<obj.Length&&char.IsWhiteSpace(obj[v]))v++;
                    string value="";
                    if(v<obj.Length&&obj[v]=='"')
                    {
                        v++; StringBuilder vb=new StringBuilder(); bool ve=false;
                        for(;v<obj.Length;v++){char c=obj[v];if(ve){vb.Append(c);ve=false;continue;}if(c=='\\'){ve=true;continue;}if(c=='"'){v++;break;}vb.Append(c);} value=vb.ToString();
                    }
                    else
                    {
                        int v2=v; int nested=0; bool vs=false; bool esc=false;
                        for(;v2<obj.Length;v2++){char c=obj[v2];if(vs){if(esc)esc=false;else if(c=='\\')esc=true;else if(c=='"')vs=false;continue;}if(c=='"'){vs=true;continue;}if(c=='['||c=='{')nested++;else if(c==']'||c=='}')nested--;else if(c==','&&nested==0)break;} value=obj.Substring(v,v2-v).Trim();v=v2;
                    }
                    d[key]=value; p=v;
                }
                result.Add(d); i=b+1;
            }
            return result;
        }
    }
}
