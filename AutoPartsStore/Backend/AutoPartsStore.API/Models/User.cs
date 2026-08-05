using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models
{
    public class User
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Email adresi gereklidir.")]
        [EmailAddress(ErrorMessage = "Geçerli bir email adresi giriniz.")]
        [StringLength(200, ErrorMessage = "Email adresi en fazla 200 karakter olabilir.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Şifre gereklidir.")]
        [StringLength(500, MinimumLength = 6, ErrorMessage = "Şifre en az 6 karakter olmalıdır.")]
        [JsonIgnore]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Ad Soyad gereklidir.")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Ad Soyad 2-200 karakter arasında olmalıdır.")]
        public string FullName { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Geçerli bir telefon numarası giriniz.")]
        [StringLength(20, ErrorMessage = "Telefon numarası en fazla 20 karakter olabilir.")]
        public string Phone { get; set; } = string.Empty;

        [Required]
        [StringLength(20)]
        public string Role { get; set; } = "User"; // "User" or "Admin"

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;
    }
}
