using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BabyGallery.Api.Services;

public sealed class TokenService
{
    // 인바운드 클레임 매핑 미사용. 발급과 검증이 동일한 짧은 이름 사용.
    public const string UserIdClaimType = JwtRegisteredClaimNames.Sub;

    public const string NameClaimType = JwtRegisteredClaimNames.Name;

    public const string RoleClaimType = "role";

    private const int RefreshTokenBytes = 32;

    private readonly JwtOptions _options;

    private readonly SigningCredentials _credentials;

    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;

        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
    }

    public int AccessTokenLifetimeSeconds => _options.AccessTokenMinutes * 60;

    public string CreateAccessToken(User user)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [UserIdClaimType] = user.Id.ToString(CultureInfo.InvariantCulture),
                [NameClaimType] = user.DisplayName,
                [RoleClaimType] = user.Role.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            }
        };

        return _handler.CreateToken(descriptor);
    }

    // 원문은 응답으로 1회만 전달. 저장은 해시.
    public RefreshTokenPair CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

        return new RefreshTokenPair(
            token,
            HashRefreshToken(token),
            DateTime.UtcNow.AddDays(_options.RefreshTokenDays));
    }

    // 원문 엔트로피가 128비트 이상. 사전 공격 대상이 아니므로 단순 해시로 충분.
    public static string HashRefreshToken(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}

public sealed record RefreshTokenPair(string Token, string Hash, DateTime ExpiresAt);
