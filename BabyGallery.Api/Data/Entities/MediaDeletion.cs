using System;

namespace BabyGallery.Api.Data.Entities;

// 삭제 감사 로그. 실삭제 대신 휴지통으로 옮긴 기록이며 보존 기간 정리의 대상 목록도 겸함.
public class MediaDeletion
{
    public int Id { get; set; }

    // 삭제 당시 원본 해시. 같은 내용의 재업로드 이력을 되짚는 키.
    public required string ContentHash { get; set; }

    // 갤러리 루트 기준. 수동 복구 시 되돌릴 위치.
    public required string OriginalRelativePath { get; set; }

    // 휴지통 루트 기준. 복구 대상 파일의 현재 위치.
    public required string TrashRelativePath { get; set; }

    public required string OriginalFileName { get; set; }

    public long FileSize { get; set; }

    public int DeletedByUserId { get; set; }

    // 계정이 사라져도 감사가 성립하도록 조인 없이 보관.
    public required string DeletedByUsername { get; set; }

    public DateTime DeletedAt { get; set; }

    // 보존 기간 경과 후 실삭제한 시각. null이면 휴지통에 남아 있음.
    public DateTime? PurgedAt { get; set; }
}
