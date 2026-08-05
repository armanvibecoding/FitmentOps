using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoPartsStore.API.Models;

namespace AutoPartsStore.API.Services
{
    public class JwtService
    {
        private readonly IConfiguration _configuration;
        private readonly string _jwtKey;

        public JwtService(IConfiguration configuration)
        {
            _configuration = configuration;

            var configuredJwtKey = configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(configuredJwtKey) || configuredJwtKey.Length < 32)
            {
                throw new InvalidOperationException(
                    "Jwt:Key must be configured with at least 32 characters through a secure configuration source.");
            }

            _jwtKey = configuredJwtKey;
        }

        public string GenerateToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.FullName),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));

            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"] ?? "AutoPartsStore",
                audience: _configuration["Jwt:Audience"] ?? "AutoPartsStoreUsers",
                claims: claims,
                expires: DateTime.UtcNow.AddHours(24),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
