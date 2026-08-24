using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.Data
{
    /// <summary>
    /// Genera un catálogo JSON estable para que la futura tienda web pueda consumir
    /// productos, variantes, precios, stock y datos básicos sin depender de WinForms.
    /// No publica Internet por sí mismo: prepara el contrato de datos para la API/web.
    /// </summary>
    public sealed class WebCatalogExporter
    {
        private readonly AppDataStore _store;

        public WebCatalogExporter(AppDataStore store)
        {
            _store = store;
        }

        public string Export()
        {
            string directory = Path.Combine(_store.Root, "WebCatalog");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "catalog.json");
            List<Product> products = _store.GetProducts("")
                .Where(p => p.Active && p.OnlineEnabled)
                .ToList();

            StringBuilder json = new StringBuilder();
            json.Append("{\"generatedAt\":\"")
                .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .Append("\",\"storeName\":\"")
                .Append(Escape(_store.GetSetting("store_name", "NexoMarket")))
                .Append("\",\"storeActive\":")
                .Append(_store.GetSetting("store_web_active", "0") == "1" ? "true" : "false")
                .Append(",\"store\":")
                .Append(_store.GetStoreProfileJson())
                .Append(",\"products\":[");

            for (int i = 0; i < products.Count; i++)
            {
                if (i > 0) json.Append(",");
                Product p = products[i];
                json.Append("{\"id\":").Append(p.Id)
                    .Append(",\"sku\":\"").Append(Escape(p.SKU))
                    .Append("\",\"barcode\":\"").Append(Escape(p.Barcode))
                    .Append("\",\"name\":\"").Append(Escape(p.Name))
                    .Append("\",\"slug\":\"").Append(Escape(p.Slug))
                    .Append("\",\"category\":\"").Append(Escape(p.Category))
                    .Append("\",\"brand\":\"").Append(Escape(p.Brand))
                    .Append("\",\"color\":\"").Append(Escape(p.Color))
                    .Append("\",\"size\":\"").Append(Escape(p.Size))
                    .Append("\",\"description\":\"").Append(Escape(p.PublicDescription.Length == 0 ? p.Description : p.PublicDescription))
                    .Append("\",\"price\":").Append((p.SalePrice > 0 ? p.SalePrice : p.Price).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"stock\":").Append(p.Stock)
                    .Append(",\"image\":\"").Append(Escape(p.ImagePath))
                    .Append("\"}");
            }
            json.Append("]}");
            File.WriteAllText(path, json.ToString(), Encoding.UTF8);
            return path;
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
