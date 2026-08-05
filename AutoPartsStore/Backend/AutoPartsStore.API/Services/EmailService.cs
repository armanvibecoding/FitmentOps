using System.Net;
using System.Net.Mail;
using AutoPartsStore.API.Models;

namespace AutoPartsStore.API.Services
{
    public class EmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendOrderConfirmationEmail(Order order)
        {
            try
            {
                var subject = $"Sipariş Onayı - {SafeSubjectValue(order.OrderNumber)}";
                var body = $@"
                    <html>
                    <body style='font-family: Arial, sans-serif;'>
                        <h2>Parça Mühendisi - Sipariş Onayı</h2>
                        <p>Merhaba {Encode(order.CustomerName)},</p>
                        <p>Siparişiniz başarıyla alınmıştır.</p>

                        <h3>Sipariş Detayları:</h3>
                        <p><strong>Sipariş No:</strong> {Encode(order.OrderNumber)}</p>
                        <p><strong>Sipariş Tarihi:</strong> {order.OrderDate:dd.MM.yyyy HH:mm}</p>
                        <p><strong>Toplam Tutar:</strong> {order.TotalAmount:F2} TL</p>

                        <h3>Teslimat Bilgileri:</h3>
                        <p>{Encode(order.ShippingAddress)}</p>
                        <p>{Encode(order.City)} / {Encode(order.PostalCode)}</p>
                        <p><strong>Telefon:</strong> {Encode(order.CustomerPhone)}</p>

                        <h3>Ürünler:</h3>
                        <ul>
                            {string.Join("", order.OrderItems.Select(item =>
                                $"<li>{Encode(item.Product?.Name ?? "Ürün")} - {item.Quantity} adet - {item.Price:F2} TL</li>"
                            ))}
                        </ul>

                        <p>Siparişinizi <a href='http://localhost:5173/siparis-takibi'>buradan</a> takip edebilirsiniz.</p>

                        <p>Teşekkür ederiz!</p>
                        <p><strong>Parça Mühendisi</strong></p>
                    </body>
                    </html>
                ";

                await SendEmail(order.CustomerEmail, subject, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send order confirmation email for order {OrderNumber}", order.OrderNumber);
                // Email gönderimi başarısız olsa da sipariş işlemini etkilemesin
            }
        }

        public async Task SendLowStockAlert(Product product, int threshold = 10)
        {
            try
            {
                var adminEmail = _configuration["EmailSettings:AdminEmail"] ?? string.Empty;
                var subject = $"Düşük Stok Uyarısı - {SafeSubjectValue(product.Name)}";
                var body = $@"
                    <html>
                    <body style='font-family: Arial, sans-serif;'>
                        <h2>Düşük Stok Uyarısı</h2>
                        <p>Aşağıdaki ürünün stok seviyesi kritik seviyeye düştü:</p>

                        <h3>Ürün Bilgileri:</h3>
                        <p><strong>Ürün Adı:</strong> {Encode(product.Name)}</p>
                        <p><strong>Parça No:</strong> {Encode(product.PartNumber)}</p>
                        <p><strong>Mevcut Stok:</strong> {product.Stock} adet</p>
                        <p><strong>Eşik Değer:</strong> {threshold} adet</p>

                        <p>Lütfen stok yenilemesi yapınız.</p>

                        <p><a href='http://localhost:5173/admin'>Admin Paneline Git</a></p>
                    </body>
                    </html>
                ";

                await SendEmail(adminEmail, subject, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send low stock alert for product {ProductId}", product.Id);
            }
        }

        private async Task SendEmail(string toEmail, string subject, string body)
        {
            var smtpHost = _configuration["EmailSettings:SmtpServer"];
            var smtpPortValue = _configuration["EmailSettings:SmtpPort"];
            var fromEmail = _configuration["EmailSettings:SenderEmail"];
            var senderName = _configuration["EmailSettings:SenderName"] ?? "Parça Mühendisi";
            var username = _configuration["EmailSettings:Username"];
            var fromPassword = _configuration["EmailSettings:Password"];

            // Eğer email ayarları yapılmamışsa console'a log bas
            if (string.IsNullOrWhiteSpace(smtpHost) ||
                !int.TryParse(smtpPortValue, out var smtpPort) || smtpPort is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(fromEmail) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(fromPassword) ||
                !MailAddress.TryCreate(fromEmail, out _) ||
                !MailAddress.TryCreate(toEmail, out _))
            {
                _logger.LogWarning("Email configuration is missing. Email not sent.");
                return;
            }

            using var smtpClient = new SmtpClient(smtpHost, smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(username, fromPassword)
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(fromEmail, senderName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            mailMessage.To.Add(toEmail);

            await smtpClient.SendMailAsync(mailMessage);
            _logger.LogInformation("Email sent successfully.");
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);

        private static string SafeSubjectValue(string value) =>
            value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
