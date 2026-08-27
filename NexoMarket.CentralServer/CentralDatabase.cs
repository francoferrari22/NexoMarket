using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Npgsql;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Persistencia central. PostgreSQL es la fuente de verdad para identidad,
    /// tiendas y dispositivos. Los documentos XML siguen existiendo solamente
    /// para compatibilidad/migración de catálogo legado.
    /// </summary>
    public sealed class CentralDatabase : IDisposable
    {
        private readonly string _connectionString;
        private bool _initialized;
        private readonly object _gate = new object();
        public bool Enabled { get { return !string.IsNullOrWhiteSpace(_connectionString); } }

        public CentralDatabase()
        {
            _connectionString = BuildConnectionString(Environment.GetEnvironmentVariable("NEXOMARKET_DATABASE_URL") ?? Environment.GetEnvironmentVariable("DATABASE_URL"));
        }

        private static string BuildConnectionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            try
            {
                if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
                {
                    Uri uri = new Uri(raw);
                    NpgsqlConnectionStringBuilder b = new NpgsqlConnectionStringBuilder();
                    b.Host = uri.Host; b.Port = uri.IsDefaultPort ? 5432 : uri.Port;
                    string[] auth = uri.UserInfo.Split(new[] { ':' }, 2);
                    b.Username = Uri.UnescapeDataString(auth[0]);
                    if (auth.Length > 1) b.Password = Uri.UnescapeDataString(auth[1]);
                    string db = uri.AbsolutePath.Trim('/'); b.Database = string.IsNullOrWhiteSpace(db) ? "postgres" : Uri.UnescapeDataString(db);
                    b.SslMode = SslMode.Require; b.TrustServerCertificate = true;
                    return b.ConnectionString;
                }
                return raw;
            }
            catch { return raw; }
        }
        private NpgsqlConnection Open() { NpgsqlConnection c = new NpgsqlConnection(_connectionString); c.Open(); return c; }
        private void EnsureInitialized(NpgsqlConnection c)
        {
            lock (_gate)
            {
                if (_initialized) return;
                using (NpgsqlCommand cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS nexomarket_documents(dataset TEXT PRIMARY KEY, content TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
CREATE INDEX IF NOT EXISTS idx_nexomarket_documents_updated_at ON nexomarket_documents(updated_at);
CREATE TABLE IF NOT EXISTS nexomarket_orders (
    central_order_id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL DEFAULT '',
    seller_account_id TEXT NOT NULL DEFAULT '',
    seller_email TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'Pendiente',
    ack BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    content TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_orders_store_created ON nexomarket_orders(store_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_nexomarket_orders_seller_created ON nexomarket_orders(seller_account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_nexomarket_orders_email_created ON nexomarket_orders(lower(seller_email), created_at DESC);
CREATE TABLE IF NOT EXISTS nexomarket_accounts(
    account_id TEXT PRIMARY KEY,
    email TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    role TEXT NOT NULL DEFAULT 'seller',
    store_id TEXT NOT NULL DEFAULT '',
    salt TEXT NOT NULL DEFAULT '',
    password_hash TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_accounts_store ON nexomarket_accounts(store_id);
-- Migración de identidad: una tienda vendedora solo puede tener una cuenta canónica.
DO $$
BEGIN
    DELETE FROM nexomarket_accounts a
    WHERE a.role='seller' AND a.store_id<>''
      AND EXISTS (
        SELECT 1 FROM nexomarket_accounts b
        WHERE b.role='seller' AND b.store_id=a.store_id
          AND (b.updated_at > a.updated_at OR (b.updated_at=a.updated_at AND b.account_id>a.account_id))
      );
    BEGIN
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nexomarket_seller_store
        ON nexomarket_accounts(store_id) WHERE role='seller' AND store_id<>'';
    EXCEPTION WHEN duplicate_table THEN NULL;
    END;
END $$;
CREATE TABLE IF NOT EXISTS nexomarket_devices(
    device_id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL,
    account_email TEXT NOT NULL,
    device_name TEXT NOT NULL DEFAULT 'Windows',
    device_token_hash TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    active BOOLEAN NOT NULL DEFAULT TRUE
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_devices_store ON nexomarket_devices(store_id);
CREATE TABLE IF NOT EXISTS nexomarket_pairings(
    pairing_id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL,
    account_email TEXT NOT NULL,
    token_hash TEXT NOT NULL UNIQUE,
    pairing_code_hash TEXT,
    pairing_code TEXT,
    expires_at TIMESTAMPTZ NOT NULL,
    used BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE nexomarket_pairings ADD COLUMN IF NOT EXISTS pairing_code_hash TEXT;
ALTER TABLE nexomarket_pairings ADD COLUMN IF NOT EXISTS pairing_code TEXT;
ALTER TABLE nexomarket_accounts ADD COLUMN IF NOT EXISTS active BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE nexomarket_accounts ADD COLUMN IF NOT EXISTS trial_expires_at TIMESTAMPTZ;
ALTER TABLE nexomarket_accounts ADD COLUMN IF NOT EXISTS commission_rate NUMERIC(8,4) NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_nexomarket_accounts_trial ON nexomarket_accounts(trial_expires_at);
CREATE INDEX IF NOT EXISTS idx_nexomarket_pairings_code_hash ON nexomarket_pairings(pairing_code_hash);
CREATE INDEX IF NOT EXISTS idx_nexomarket_pairings_expiry ON nexomarket_pairings(expires_at);
";
                    cmd.ExecuteNonQuery();
                }
                _initialized = true;
            }
        }
        private static string HashToken(string value)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
        public string GetDocument(string dataset)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(dataset)) return null;
            try { using (NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT content FROM nexomarket_documents WHERE dataset=@dataset";cmd.Parameters.AddWithValue("dataset",dataset);object v=cmd.ExecuteScalar();return v==null||v==DBNull.Value?null:Convert.ToString(v);}}} catch{return null;}
        }
        public bool SaveDocument(string dataset,string content)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(dataset)||content==null)return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="INSERT INTO nexomarket_documents(dataset,content,updated_at) VALUES(@dataset,@content,NOW()) ON CONFLICT(dataset) DO UPDATE SET content=EXCLUDED.content,updated_at=NOW();";cmd.Parameters.AddWithValue("dataset",dataset);cmd.Parameters.AddWithValue("content",content);cmd.ExecuteNonQuery();}}return true;}catch{return false;}
        }
        public bool SaveOrdersDocument(string xml)
        {
            if(!Enabled || string.IsNullOrWhiteSpace(xml)) return false;
            try
            {
                XDocument d=XDocument.Parse(xml);
                XElement root=d.Root==null?null:d.Root.Element("Orders");
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlTransaction tx=c.BeginTransaction())
                    {
                        if(root!=null)
                        {
                            foreach(XElement o in root.Elements("Order"))
                            {
                                string id=V(o,"CentralOrderId"); if(string.IsNullOrWhiteSpace(id)) continue;
                                using(NpgsqlCommand cmd=c.CreateCommand())
                                {
                                    cmd.Transaction=tx;
                                    cmd.CommandText=@"INSERT INTO nexomarket_orders(central_order_id,store_id,seller_account_id,seller_email,status,ack,created_at,updated_at,content)
VALUES(@id,@store,@seller,@email,@status,@ack,@created,@updated,@content)
ON CONFLICT(central_order_id) DO UPDATE SET store_id=EXCLUDED.store_id,seller_account_id=EXCLUDED.seller_account_id,seller_email=EXCLUDED.seller_email,status=EXCLUDED.status,ack=EXCLUDED.ack,created_at=EXCLUDED.created_at,updated_at=EXCLUDED.updated_at,content=EXCLUDED.content";
                                    cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("store",V(o,"StoreId")); cmd.Parameters.AddWithValue("seller",V(o,"SellerAccountId")); cmd.Parameters.AddWithValue("email",V(o,"SellerEmail")); cmd.Parameters.AddWithValue("status",string.IsNullOrWhiteSpace(V(o,"Status"))?"Pendiente":V(o,"Status")); cmd.Parameters.AddWithValue("ack",V(o,"Ack")=="1");
                                    cmd.Parameters.AddWithValue("created",ParseDate(V(o,"CreatedAt"))); cmd.Parameters.AddWithValue("updated",ParseDate(string.IsNullOrWhiteSpace(V(o,"UpdatedAt"))?V(o,"CreatedAt"):V(o,"UpdatedAt"))); cmd.Parameters.AddWithValue("content",o.ToString(SaveOptions.None)); cmd.ExecuteNonQuery();
                                }
                            }
                        }
                        tx.Commit();
                    }
                }
                return true;
            }
            catch { return false; }
        }

        public List<string> GetOrdersForSeller(string storeId,string sellerAccountId,string sellerEmail)
        {
            var list=new List<string>(); if(!Enabled) return list;
            try
            {
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlCommand cmd=c.CreateCommand())
                    {
                        cmd.CommandText=@"SELECT content FROM nexomarket_orders WHERE lower(store_id)=lower(@store) OR (@seller<>'' AND seller_account_id=@seller) OR (@email<>'' AND lower(seller_email)=lower(@email)) ORDER BY created_at DESC";
                        cmd.Parameters.AddWithValue("store",storeId??""); cmd.Parameters.AddWithValue("seller",sellerAccountId??""); cmd.Parameters.AddWithValue("email",sellerEmail??"");
                        using(NpgsqlDataReader r=cmd.ExecuteReader()) while(r.Read()) list.Add(r.GetString(0));
                    }
                }
            } catch { }
            return list;
        }

        private static string V(XElement e,string n){XElement x=e==null?null:e.Element(n);return x==null?"":x.Value??"";}
        private static DateTime ParseDate(string s){DateTime d;if(DateTime.TryParse(s,null,System.Globalization.DateTimeStyles.RoundtripKind,out d)) return d.ToUniversalTime();return DateTime.UtcNow;}

        public bool EnsureDocument(string dataset,string content){if(!Enabled)return false;return GetDocument(dataset)!=null||SaveDocument(dataset,content);}
        public string Status(){if(!Enabled)return "disabled";try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT COUNT(*) FROM nexomarket_documents";long n=Convert.ToInt64(cmd.ExecuteScalar());return "connected|documents="+n.ToString(System.Globalization.CultureInfo.InvariantCulture);}}}catch(Exception ex){return "error|"+ex.GetType().Name;}}


        public bool UpdatePassword(string email, string salt, string passwordHash)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(passwordHash)) return false;
            try
            {
                using (NpgsqlConnection c = Open())
                {
                    EnsureInitialized(c);
                    using (NpgsqlCommand cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE nexomarket_accounts SET salt=@salt,password_hash=@hash,updated_at=NOW() WHERE lower(email)=lower(@email)";
                        cmd.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
                        cmd.Parameters.AddWithValue("salt", salt);
                        cmd.Parameters.AddWithValue("hash", passwordHash);
                        return cmd.ExecuteNonQuery() > 0;
                    }
                }
            }
            catch { return false; }
        }

        public bool UpsertAccount(string id,string name,string email,string phone,string role,string storeId,string salt,string passwordHash,string createdAt)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return false;
            try
            {
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    email=email.Trim().ToLowerInvariant();
                    role=(role??"seller").Trim().ToLowerInvariant();
                    storeId=(storeId??"").Trim();
                    using(NpgsqlCommand tx=c.CreateCommand())
                    {
                        tx.CommandText="BEGIN"; tx.ExecuteNonQuery();
                        try
                        {
                            // La identidad de vendedor es la tienda, no el correo enviado por una versión.
                            // Si ya existe un vendedor para ese Store ID, se actualiza ESA cuenta y nunca se crea otra.
                            string existingId="";
                            if(role=="seller" && storeId.Length>0)
                            {
                                using(NpgsqlCommand q=c.CreateCommand())
                                { q.CommandText="SELECT account_id,email FROM nexomarket_accounts WHERE role='seller' AND store_id=@store ORDER BY updated_at DESC LIMIT 1"; q.Parameters.AddWithValue("store",storeId); using(NpgsqlDataReader r=q.ExecuteReader()){ if(r.Read()) existingId=r.GetString(0); } }
                            }
                            // El correo tampoco puede pertenecer a una identidad diferente.
                            using(NpgsqlCommand q2=c.CreateCommand())
                            { q2.CommandText="SELECT account_id FROM nexomarket_accounts WHERE lower(email)=lower(@email) LIMIT 1"; q2.Parameters.AddWithValue("email",email); object v=q2.ExecuteScalar(); if(v!=null && existingId.Length>0 && !string.Equals(Convert.ToString(v),existingId,StringComparison.OrdinalIgnoreCase)){ tx.Dispose(); return false; } if(v!=null && existingId.Length==0) existingId=Convert.ToString(v); }
                            if(existingId.Length>0)
                            {
                                using(NpgsqlCommand up=c.CreateCommand())
                                {
                                    up.CommandText=@"UPDATE nexomarket_accounts SET email=@email,name=@name,phone=@phone,role=@role,store_id=@store,salt=@salt,password_hash=@hash,updated_at=NOW() WHERE account_id=@id";
                                    up.Parameters.AddWithValue("id",existingId); up.Parameters.AddWithValue("email",email); up.Parameters.AddWithValue("name",name??""); up.Parameters.AddWithValue("phone",phone??""); up.Parameters.AddWithValue("role",role); up.Parameters.AddWithValue("store",storeId); up.Parameters.AddWithValue("salt",salt??""); up.Parameters.AddWithValue("hash",passwordHash??""); up.ExecuteNonQuery();
                                }
                            }
                            else
                            {
                                using(NpgsqlCommand ins=c.CreateCommand())
                                {
                                    ins.CommandText=@"INSERT INTO nexomarket_accounts(account_id,email,name,phone,role,store_id,salt,password_hash,created_at,updated_at) VALUES(@id,@email,@name,@phone,@role,@store,@salt,@hash,COALESCE(NULLIF(@created,'')::timestamptz,NOW()),NOW())";
                                    ins.Parameters.AddWithValue("id",string.IsNullOrWhiteSpace(id)?Guid.NewGuid().ToString("N"):id); ins.Parameters.AddWithValue("email",email); ins.Parameters.AddWithValue("name",name??""); ins.Parameters.AddWithValue("phone",phone??""); ins.Parameters.AddWithValue("role",role); ins.Parameters.AddWithValue("store",storeId); ins.Parameters.AddWithValue("salt",salt??""); ins.Parameters.AddWithValue("hash",passwordHash??""); ins.Parameters.AddWithValue("created",createdAt??""); ins.ExecuteNonQuery();
                                }
                            }
                            using(NpgsqlCommand commit=c.CreateCommand()){commit.CommandText="COMMIT";commit.ExecuteNonQuery();}
                            return true;
                        }
                        catch { try{using(NpgsqlCommand rb=c.CreateCommand()){rb.CommandText="ROLLBACK";rb.ExecuteNonQuery();}}catch{} return false; }
                    }
                }
            }catch{return false;}
        }

        public List<Dictionary<string,string>> GetAccountsForAdmin()
        {
            var list=new List<Dictionary<string,string>>();
            if(!Enabled)return list;
            try
            {
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlCommand cmd=c.CreateCommand())
                    {
                        cmd.CommandText="SELECT account_id,name,email,phone,role,store_id,created_at,active,trial_expires_at FROM nexomarket_accounts ORDER BY created_at DESC";
                        using(NpgsqlDataReader r=cmd.ExecuteReader())
                        {
                            while(r.Read())
                            {
                                list.Add(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    {"id",r.GetString(0)}, {"name",r.GetString(1)}, {"email",r.GetString(2)}, {"phone",r.GetString(3)},
                                    {"role",r.GetString(4)}, {"storeId",r.GetString(5)}, {"createdAt",r.GetDateTime(6).ToUniversalTime().ToString("o")},
                                    {"active",r.GetBoolean(7)?"1":"0"}, {"trialExpiresAt",r.IsDBNull(8)?"":r.GetDateTime(8).ToUniversalTime().ToString("o")}
                                });
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }
        public bool SetAccountTrial(string email,int days)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_accounts SET trial_expires_at=NOW()+(@days * INTERVAL '1 day'), active=TRUE, updated_at=NOW() WHERE lower(email)=lower(@email)";cmd.Parameters.AddWithValue("email",email.Trim());cmd.Parameters.AddWithValue("days",days);return cmd.ExecuteNonQuery()>0;}}}catch{return false;}
        }
        public bool SetAccountActive(string email,bool active)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_accounts SET active=@active, updated_at=NOW() WHERE lower(email)=lower(@email)";cmd.Parameters.AddWithValue("email",email.Trim());cmd.Parameters.AddWithValue("active",active);return cmd.ExecuteNonQuery()>0;}}}catch{return false;}
        }
        public bool DeleteAccount(string email)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="DELETE FROM nexomarket_accounts WHERE lower(email)=lower(@email)";cmd.Parameters.AddWithValue("email",email.Trim());return cmd.ExecuteNonQuery()>0;}}}catch{return false;}
        }
        public void FactoryResetAll()
        {
            if(!Enabled)return;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="DELETE FROM nexomarket_devices; DELETE FROM nexomarket_pairings; DELETE FROM nexomarket_accounts; DELETE FROM nexomarket_documents;";cmd.ExecuteNonQuery();}}}catch{}
        }

        public void DeleteStoreLinks(string storeId)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(storeId))return;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="DELETE FROM nexomarket_devices WHERE store_id=@store; DELETE FROM nexomarket_pairings WHERE store_id=@store; DELETE FROM nexomarket_accounts WHERE store_id=@store;";cmd.Parameters.AddWithValue("store",storeId.Trim());cmd.ExecuteNonQuery();}}}catch{}
        }

        public void DeleteAccountsForStore(string storeId)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(storeId)) return;
            try
            {
                using (NpgsqlConnection c = Open()) using (NpgsqlCommand cmd = c.CreateCommand())
                {
                    EnsureInitialized(c);
                    cmd.CommandText = "DELETE FROM nexomarket_accounts WHERE store_id=@storeId";
                    cmd.Parameters.AddWithValue("storeId", storeId);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public Dictionary<string,string> GetAccount(string email)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return null;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT account_id,name,email,phone,role,store_id,salt,password_hash,created_at,active,trial_expires_at,commission_rate FROM nexomarket_accounts WHERE lower(email)=lower(@email) LIMIT 1";cmd.Parameters.AddWithValue("email",email.Trim());using(NpgsqlDataReader r=cmd.ExecuteReader()){if(!r.Read())return null;return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",r.GetString(0)},{"name",r.GetString(1)},{"email",r.GetString(2)},{"phone",r.GetString(3)},{"role",r.GetString(4)},{"storeId",r.GetString(5)},{"salt",r.GetString(6)},{"passwordHash",r.GetString(7)},{"createdAt",r.GetDateTime(8).ToUniversalTime().ToString("o")},{"active",r.GetBoolean(9)?"1":"0"},{"trialExpiresAt",r.IsDBNull(10)?"":r.GetDateTime(10).ToUniversalTime().ToString("o")},{"commissionRate",r.IsDBNull(11)?"0":r.GetDecimal(11).ToString("0.####",System.Globalization.CultureInfo.InvariantCulture)}};}}}}catch{return null;}
        }
        public bool SetAccountStore(string email, string storeId)
        {
            if(!Enabled || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(storeId)) return false;
            try
            {
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlCommand cmd=c.CreateCommand())
                    {
                        cmd.CommandText="UPDATE nexomarket_accounts SET store_id=@store, updated_at=NOW() WHERE lower(email)=lower(@email) AND role='seller'";
                        cmd.Parameters.AddWithValue("email",email.Trim());
                        cmd.Parameters.AddWithValue("store",storeId.Trim());
                        return cmd.ExecuteNonQuery()>0;
                    }
                }
            }
            catch { return false; }
        }

        public Dictionary<string,string> GetSellerByStore(string storeId)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(storeId))return null;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT account_id,name,email,phone,role,store_id,salt,password_hash,created_at,active,trial_expires_at,commission_rate FROM nexomarket_accounts WHERE lower(store_id)=lower(@store) AND role='seller' ORDER BY updated_at DESC LIMIT 1";cmd.Parameters.AddWithValue("store",storeId.Trim());using(NpgsqlDataReader r=cmd.ExecuteReader()){if(!r.Read())return null;return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",r.GetString(0)},{"name",r.GetString(1)},{"email",r.GetString(2)},{"phone",r.GetString(3)},{"role",r.GetString(4)},{"storeId",r.GetString(5)},{"salt",r.GetString(6)},{"passwordHash",r.GetString(7)},{"createdAt",r.GetDateTime(8).ToUniversalTime().ToString("o")},{"active",r.GetBoolean(9)?"1":"0"},{"trialExpiresAt",r.IsDBNull(10)?"":r.GetDateTime(10).ToUniversalTime().ToString("o")},{"commissionRate",r.IsDBNull(11)?"0":r.GetDecimal(11).ToString("0.####",System.Globalization.CultureInfo.InvariantCulture)}};}}}}catch{return null;}
        }
        public bool SetCommission(string email, decimal rate)
        {
            if(!Enabled || string.IsNullOrWhiteSpace(email)) return false;
            rate=Math.Max(0m,Math.Min(100m,rate));
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_accounts SET commission_rate=@rate,updated_at=NOW() WHERE lower(email)=lower(@email) AND role='seller'";cmd.Parameters.AddWithValue("email",email.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("rate",rate);return cmd.ExecuteNonQuery()>0;}}}catch{return false;}
        }
        public decimal GetCommission(string email)
        {
            if(!Enabled || string.IsNullOrWhiteSpace(email)) return 0m;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT commission_rate FROM nexomarket_accounts WHERE lower(email)=lower(@email) LIMIT 1";cmd.Parameters.AddWithValue("email",email.Trim().ToLowerInvariant());object v=cmd.ExecuteScalar();return v==null||v==DBNull.Value?0m:Convert.ToDecimal(v,System.Globalization.CultureInfo.InvariantCulture);}}}catch{return 0m;}
        }
        public string CreatePairing(string storeId,string email,int minutes)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(email))return null;
            try
            {
                // Código corto para copiar desde el teléfono al programa Windows.
                // Se guarda únicamente su hash y expira; nunca se guarda el código en claro.
                string code=GeneratePairCode();
                string id=Guid.NewGuid().ToString("N");
                DateTime exp=DateTime.UtcNow.AddMinutes(minutes<1?5:minutes);
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlCommand cmd=c.CreateCommand())
                    {
                        cmd.CommandText="UPDATE nexomarket_pairings SET used=TRUE WHERE store_id=@store AND account_email=@email AND used=FALSE; INSERT INTO nexomarket_pairings(pairing_id,store_id,account_email,token_hash,pairing_code_hash,pairing_code,expires_at,used) VALUES(@id,@store,@email,@hash,@codehash,@plaincode,@exp,FALSE);";
                        cmd.Parameters.AddWithValue("id",id);
                        cmd.Parameters.AddWithValue("store",storeId.Trim());
                        cmd.Parameters.AddWithValue("email",email.Trim().ToLowerInvariant());
                        cmd.Parameters.AddWithValue("hash",HashToken(code));
                        cmd.Parameters.AddWithValue("codehash",HashToken(NormalizePairCode(code)));
                        cmd.Parameters.AddWithValue("plaincode",NormalizePairCode(code));
                        cmd.Parameters.AddWithValue("exp",exp);
                        cmd.ExecuteNonQuery();
                    }
                }
                return code;
            }
            catch{return null;}
        }
        public Dictionary<string,string> CompletePairing(string token,string deviceId,string deviceName)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(deviceId))return null;
            try
            {
                string normalized=NormalizePairCode(token);
                string hash=HashToken(normalized);
                using(NpgsqlConnection c=Open())
                {
                    EnsureInitialized(c);
                    using(NpgsqlCommand tx=c.CreateCommand())
                    {
                        tx.CommandText="BEGIN; SELECT pairing_id,store_id,account_email FROM nexomarket_pairings WHERE (token_hash=@hash OR pairing_code_hash=@hash OR pairing_code=@code) AND used=FALSE AND expires_at>NOW() ORDER BY created_at DESC LIMIT 1 FOR UPDATE;";
                        tx.Parameters.AddWithValue("hash",hash);
                        tx.Parameters.AddWithValue("code",normalized);
                        using(NpgsqlDataReader r=tx.ExecuteReader())
                        {
                            if(!r.Read())
                            {
                                r.Close();
                                using(NpgsqlCommand rb=c.CreateCommand()){rb.CommandText="ROLLBACK";rb.ExecuteNonQuery();}
                                return null;
                            }
                            string pairingId=r.GetString(0),storeId=r.GetString(1),email=r.GetString(2);
                            r.Close();
                            string rawDeviceToken=Convert.ToBase64String(RandomBytes(32)).Replace("+","-").Replace("/","_").TrimEnd('=');
                            using(NpgsqlCommand up=c.CreateCommand())
                            {
                                up.CommandText="INSERT INTO nexomarket_devices(device_id,store_id,account_email,device_name,device_token_hash,created_at,last_seen_at,active) VALUES(@id,@store,@email,@name,@hash,NOW(),NOW(),TRUE) ON CONFLICT(device_id) DO UPDATE SET store_id=EXCLUDED.store_id,account_email=EXCLUDED.account_email,device_name=EXCLUDED.device_name,device_token_hash=EXCLUDED.device_token_hash,last_seen_at=NOW(),active=TRUE; UPDATE nexomarket_pairings SET used=TRUE WHERE pairing_id=@pair;";
                                up.Parameters.AddWithValue("id",deviceId); up.Parameters.AddWithValue("store",storeId); up.Parameters.AddWithValue("email",email); up.Parameters.AddWithValue("name",deviceName??"Windows"); up.Parameters.AddWithValue("hash",HashToken(rawDeviceToken)); up.Parameters.AddWithValue("pair",pairingId); up.ExecuteNonQuery();
                            }
                            using(NpgsqlCommand commit=c.CreateCommand()){commit.CommandText="COMMIT";commit.ExecuteNonQuery();}
                            return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"deviceId",deviceId},{"deviceToken",rawDeviceToken},{"storeId",storeId},{"email",email}};
                        }
                    }
                }
            }
            catch{return null;}
        }
        private static string NormalizePairCode(string value)
        {
            return (value??"").Trim().Replace("-","").Replace(" ","").Replace("\r","").Replace("\n","");
        }
        private static string GeneratePairCode()
        {
            byte[] b=RandomBytes(4); uint n=BitConverter.ToUInt32(b,0)%1000000U; return n.ToString("D6",System.Globalization.CultureInfo.InvariantCulture);
        }
        public bool ValidateDevice(string deviceId,string deviceToken,string storeId)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(deviceToken)||string.IsNullOrWhiteSpace(storeId))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_devices SET last_seen_at=NOW() WHERE device_id=@id AND device_token_hash=@hash AND store_id=@store AND active=TRUE";cmd.Parameters.AddWithValue("id",deviceId);cmd.Parameters.AddWithValue("hash",HashToken(deviceToken));cmd.Parameters.AddWithValue("store",storeId);return cmd.ExecuteNonQuery()==1;}}}catch{return false;}
        }
        private static byte[] RandomBytes(int count){byte[] b=new byte[count];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return b;}
        public void Dispose() { }
    }
}
