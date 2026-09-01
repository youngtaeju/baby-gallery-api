using System;

namespace BabyGallery.Api.Data.Entities;

public class MediaItem
{
    public int Id { get; set; }

    // 갤러리 루트 기준 상대 경로. 구분자는 '/'로 정규화. API 응답에 미노출.
    public required string RelativePath { get; set; }

    // 표시용. 업로드분은 저장 파일명이 규칙에 따라 달라지므로 별도 보관.
    public required string OriginalFileName { get; set; }

    public MediaType MediaType { get; set; }

    public long FileSize { get; set; }

    // 원본 내용의 SHA-256 소문자 hex. 중복 판정 키.
    public required string ContentHash { get; set; }

    // 목록 정렬 키. 메타데이터 부재 시 mtime fallback을 인덱싱 시점에 확정.
    public DateTime CapturedAt { get; set; }

    // 변경 감지용 mtime. 크기와 함께 재인덱싱 여부 판단.
    public DateTime FileModifiedAt { get; set; }

    public DateTime IndexedAt { get; set; }

    // 메타데이터 부재 시 null. 그리드 레이아웃과 재생 UI 표시용.
    public int? Width { get; set; }

    public int? Height { get; set; }

    // 영상만 해당.
    public int? DurationMs { get; set; }
}
