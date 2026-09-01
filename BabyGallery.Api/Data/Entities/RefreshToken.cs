using System;

namespace BabyGallery.Api.Data.Entities;

public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    // 원문 미저장. 조회는 해시 비교.
    public required string TokenHash { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public User? User { get; set; }
}
