namespace BabyGallery.Api.Data.Entities;

public enum UserRole
{
    // 목록·조회만 가능.
    Viewer = 0,

    // Viewer 권한 + 업로드·삭제.
    Editor = 1
}
