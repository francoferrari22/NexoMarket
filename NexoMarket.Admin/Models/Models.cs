using System;

namespace NexoMarket.Admin.Models
{
    public class Product
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public decimal SalePrice { get; set; }
        public int Stock { get; set; }
        public int MinimumStock { get; set; }
        public string Variants { get; set; }
        public bool Active { get; set; }
        public string ImagePath { get; set; }
        public string Barcode { get; set; }
        // Campos preparados para catálogo web y retail de indumentaria.
        public string SKU { get; set; }
        public string Brand { get; set; }
        public string Size { get; set; }
        public string Color { get; set; }
        public decimal Cost { get; set; }
        public decimal TaxRate { get; set; }
        public bool OnlineEnabled { get; set; }
        public string Slug { get; set; }
        public string PublicDescription { get; set; }
        public string VideoUrl { get; set; }
        public string BarcodeImagePath { get; set; }

        public Product()
        {
            Name = "";
            Category = "";
            Description = "";
            Variants = "";
            ImagePath = "";
            Barcode = "";
            SKU = "";
            Brand = "";
            Size = "";
            Color = "";
            Slug = "";
            PublicDescription = "";
            VideoUrl = "";
            BarcodeImagePath = "";
            Active = true;
            OnlineEnabled = true;
        }
    }

    public class Customer
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Address { get; set; }
        public string Notes { get; set; }
        public int Orders { get; set; }
        public decimal TotalSpent { get; set; }
        public string PhotoPath { get; set; }

        public Customer()
        {
            Name = "";
            Phone = "";
            Email = "";
            Address = "";
            Notes = "";
            PhotoPath = "";
        }
    }

    public class Coupon
    {
        public long Id { get; set; }
        public string Code { get; set; }
        public string Description { get; set; }
        public decimal DiscountPercent { get; set; }
        public decimal DiscountAmount { get; set; }
        public int MaxUses { get; set; }
        public int Used { get; set; }
        public bool Active { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public Coupon()
        {
            Code = ""; Description = ""; Active = true; MaxUses = 0; Used = 0;
            From = DateTime.Today; To = DateTime.Today.AddDays(30);
        }
    }

    public class Promotion
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string ProductIds { get; set; }
        public decimal PromotionalPrice { get; set; }
        public bool Active { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public Promotion() { Name = ""; ProductIds = ""; Active = true; From = DateTime.Today; To = DateTime.Today.AddDays(30); }
    }

    public class Order
    {
        public long Id { get; set; }
        public string CustomerName { get; set; }
        public string Phone { get; set; }
        public string Fulfillment { get; set; }
        public string Address { get; set; }
        public string Notes { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public DateTime CreatedAt { get; set; }
        public long CustomerId { get; set; }
        public string PaymentMethod { get; set; }
        public string Source { get; set; }
        public string ItemsJson { get; set; }
        public string CustomerEmail { get; set; }
        public string PaymentStatus { get; set; }
        public string PaymentReference { get; set; }
        public string PaymentProofPath { get; set; }
        public string PostalCode { get; set; }
        public decimal ShippingCost { get; set; }
        public string TrackingNumber { get; set; }
        public string Carrier { get; set; }
        public string StoreId { get; set; }
        public string SellerMessage { get; set; }
        public string BuyerMessage { get; set; }
        public string NegotiationStatus { get; set; }
        public string CentralOrderId { get; set; }
        public string CouponCode { get; set; }
        public decimal CouponDiscount { get; set; }

        public Order()
        {
            CustomerName = "";
            Phone = "";
            Fulfillment = "Retiro";
            Address = "";
            Notes = "";
            Status = "Pendiente";
            PaymentMethod = "Efectivo";
            Source = "Mostrador";
            ItemsJson = "[]";
            CustomerEmail = "";
            PaymentStatus = "Pendiente";
            PaymentReference = "";
            PaymentProofPath = "";
            PostalCode = "";
            ShippingCost = 0m;
            TrackingNumber = "";
            Carrier = "";
            StoreId = "";
            SellerMessage = "";
            BuyerMessage = "";
            NegotiationStatus = "Ninguna";
            CentralOrderId = "";
            CouponCode = ""; CouponDiscount = 0m;
        }
    }

    public class Review
    {
        public long Id { get; set; }
        public long OrderId { get; set; }
        public long CustomerId { get; set; }
        public string CustomerEmail { get; set; }
        public string StoreId { get; set; }
        public int Rating { get; set; }
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }
        public Review() { CustomerEmail=""; StoreId=""; Text=""; CreatedAt=DateTime.Now; Rating=5; }
    }

    public class ChatMessage
    {
        public long Id { get; set; }
        public long OrderId { get; set; }
        public string FromEmail { get; set; }
        public string ToEmail { get; set; }
        public string Body { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool Read { get; set; }
        public ChatMessage() { FromEmail=""; ToEmail=""; Body=""; CreatedAt=DateTime.Now; }
    }

    public class MediaItem
    {
        public long Id { get; set; }
        public string FileName { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
        public string ProductName { get; set; }

        public MediaItem()
        {
            FileName = "";
            Path = "";
            Type = "";
            ProductName = "";
        }
    }

    public class DashboardData
    {
        public decimal TodaySales;
        public int NewOrders;
        public int Preparing;
        public int Ready;
        public int LowStock;
        public int TotalProducts;
        public int TotalCustomers;
        public int DeliveryPending;
    }
    public class WebUser
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Role { get; set; }
        public string Salt { get; set; }
        public string PasswordHash { get; set; }
        public string StoreId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string RecoveryCode { get; set; }
        public DateTime RecoveryExpires { get; set; }

        public WebUser()
        {
            Name = ""; Email = ""; Phone = ""; Role = "buyer"; Salt = ""; PasswordHash = ""; StoreId = "";
            CreatedAt = DateTime.Now; RecoveryCode = ""; RecoveryExpires = DateTime.MinValue;
        }
    }

}
