using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BabyGallery.Api.Data;

// SQLite는 DateTime의 Kind를 보존하지 않음.
// 조회 값이 Unspecified가 되면 JSON 직렬화에 타임존 표기가 빠져 클라이언트가 로컬시간으로 해석.
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            value => value.ToUniversalTime(),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
