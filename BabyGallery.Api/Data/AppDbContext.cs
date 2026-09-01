using System;
using BabyGallery.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BabyGallery.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    public DbSet<MediaDeletion> MediaDeletions => Set<MediaDeletion>();

    // 엔티티 추가 시 개별 지정 없이 모든 DateTime 속성에 적용.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.Username).HasMaxLength(64);
            entity.Property(u => u.PasswordHash).HasMaxLength(256);
            entity.Property(u => u.DisplayName).HasMaxLength(64);

            // DB 직접 조회 시 가독성 확보. enum 값 재배치에도 안전.
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.Property(t => t.TokenHash).HasMaxLength(128);

            entity.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaItem>(entity =>
        {
            // 스캐너의 파일 단위 조회 키.
            entity.HasIndex(m => m.RelativePath).IsUnique();

            // 중복 제거 판정 키. 동일 내용의 재등록을 DB 제약으로 차단.
            entity.HasIndex(m => m.ContentHash).IsUnique();

            // 목록 정렬·커서 페이징 전용. ORDER BY와 방향을 일치시켜 정렬 단계 제거.
            entity.HasIndex(m => new { m.CapturedAt, m.Id }).IsDescending();

            entity.Property(m => m.RelativePath).HasMaxLength(1024);
            entity.Property(m => m.OriginalFileName).HasMaxLength(256);

            // SHA-256 hex 고정 길이.
            entity.Property(m => m.ContentHash).HasMaxLength(64);

            entity.Property(m => m.MediaType).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<MediaDeletion>(entity =>
        {
            // 보존 기간 정리가 디렉터리 순회 없이 미처리분만 조회.
            entity.HasIndex(d => d.PurgedAt);

            // 같은 내용의 삭제·재업로드 이력 추적용.
            entity.HasIndex(d => d.ContentHash);

            entity.Property(d => d.ContentHash).HasMaxLength(64);
            entity.Property(d => d.OriginalRelativePath).HasMaxLength(1024);
            entity.Property(d => d.TrashRelativePath).HasMaxLength(1024);
            entity.Property(d => d.OriginalFileName).HasMaxLength(256);
            entity.Property(d => d.DeletedByUsername).HasMaxLength(64);
        });
    }
}
