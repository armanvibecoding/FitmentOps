using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models
{
    public class Order
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Sipariş numarası gereklidir.")]
        [StringLength(50, ErrorMessage = "Sipariş numarası en fazla 50 karakter olabilir.")]
        public string OrderNumber { get; set; } = string.Empty;

        [StringLength(100)]
        [JsonIgnore]
        public string? CheckoutIdempotencyKey { get; set; }

        public int? UserId { get; set; }
        public User? User { get; set; }

        [Required(ErrorMessage = "Müşteri adı gereklidir.")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Müşteri adı 2-200 karakter arasında olmalıdır.")]
        public string CustomerName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email adresi gereklidir.")]
        [EmailAddress(ErrorMessage = "Geçerli bir email adresi giriniz.")]
        [StringLength(200, ErrorMessage = "Email adresi en fazla 200 karakter olabilir.")]
        public string CustomerEmail { get; set; } = string.Empty;

        [Required(ErrorMessage = "Telefon numarası gereklidir.")]
        [Phone(ErrorMessage = "Geçerli bir telefon numarası giriniz.")]
        [StringLength(20, ErrorMessage = "Telefon numarası en fazla 20 karakter olabilir.")]
        public string CustomerPhone { get; set; } = string.Empty;

        [Required(ErrorMessage = "Teslimat adresi gereklidir.")]
        [StringLength(500, MinimumLength = 10, ErrorMessage = "Teslimat adresi 10-500 karakter arasında olmalıdır.")]
        public string ShippingAddress { get; set; } = string.Empty;

        [Required(ErrorMessage = "Şehir gereklidir.")]
        [StringLength(100, ErrorMessage = "Şehir adı en fazla 100 karakter olabilir.")]
        public string City { get; set; } = string.Empty;

        [Required(ErrorMessage = "Posta kodu gereklidir.")]
        [StringLength(10, ErrorMessage = "Posta kodu en fazla 10 karakter olabilir.")]
        public string PostalCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Toplam tutar gereklidir.")]
        [Range(0.01, double.MaxValue, ErrorMessage = "Toplam tutar 0'dan büyük olmalıdır.")]
        public decimal TotalAmount { get; set; }

        [Required(ErrorMessage = "Sipariş durumu gereklidir.")]
        [StringLength(50, ErrorMessage = "Durum en fazla 50 karakter olabilir.")]
        public string Status { get; set; } = "Pending";

        public DateTime OrderDate { get; set; } = DateTime.UtcNow;

        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public Payment? Payment { get; set; }

        [JsonIgnore]
        public ICollection<LegalAcceptance> LegalAcceptances { get; set; } = new List<LegalAcceptance>();
    }

    public class OrderItem
    {
        public int Id { get; set; }

        [Required]
        public int OrderId { get; set; }
        public Order Order { get; set; } = null!;

        [Required]
        public int ProductId { get; set; }
        public Product Product { get; set; } = null!;

        [Required(ErrorMessage = "Miktar gereklidir.")]
        [Range(1, int.MaxValue, ErrorMessage = "Miktar en az 1 olmalıdır.")]
        public int Quantity { get; set; }

        [Required(ErrorMessage = "Fiyat gereklidir.")]
        [Range(0.01, double.MaxValue, ErrorMessage = "Fiyat 0'dan büyük olmalıdır.")]
        public decimal Price { get; set; }
    }
}
