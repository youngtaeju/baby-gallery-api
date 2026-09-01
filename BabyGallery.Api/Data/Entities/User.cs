using System;
using System.Collections.Generic;

namespace BabyGallery.Api.Data.Entities;

public class User
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public required string DisplayName { get; set; }

    // 조회 범위는 권한과 무관하게 전원 동일. 쓰기 작업만 구분.
    public UserRole Role { get; set; } = UserRole.Viewer;

    public DateTime CreatedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
