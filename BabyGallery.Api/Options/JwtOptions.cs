using System.ComponentModel.DataAnnotations;

namespace BabyGallery.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    // 설정 파일에 두지 않음. 환경변수 Jwt__SigningKey 또는 user-secrets로 주입.
    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 30;

    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 60;
}
