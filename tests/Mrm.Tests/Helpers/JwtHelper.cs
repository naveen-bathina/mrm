using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Mrm.Infrastructure.Entities;

namespace Mrm.Tests.Helpers;

public static class JwtHelper
{
    private const string TestKey = "replace-with-32-char-secret-key-here!!";
    private const string Issuer = "mrm-api";
    private const string Audience = "mrm-client";

    public static string GenerateToken(Guid userId, UserRole role, Guid? studioId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.Role, role.ToString()),
        };
        if (studioId.HasValue)
            claims.Add(new("studioId", studioId.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
