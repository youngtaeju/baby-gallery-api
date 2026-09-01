using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace BabyGallery.Api.Tests;

/// <summary>tkhd 회전 행렬 종류. 이름은 MetadataExtractor가 보고하는 각도 기준.</summary>
public enum TrackRotation
{
    None,

    // 세로로 촬영한 iPhone 영상에서 관측되는 행렬.
    Minus90,

    Plus90,

    Minus180
}

// 실제 메타데이터 파싱 경로용 미디어 바이트 조립. 바이너리 자산 보관 방지.
// 산출물은 메타데이터만 유효. 디코딩 가능한 스트림이 아님.
public static class MediaFixtures
{
    // EXIF 태그 번호.
    private const ushort OrientationTag = 274;

    private const ushort ExifIfdPointerTag = 34665;

    private const ushort DateTimeOriginalTag = 36867;

    private const ushort OffsetTimeOriginalTag = 36881;

    private const ushort PixelXDimensionTag = 40962;

    private const ushort PixelYDimensionTag = 40963;

    // TIFF 값 타입.
    private const ushort AsciiType = 2;

    private const ushort ShortType = 3;

    private const ushort LongType = 4;

    // QuickTime 시각 값의 기준시.
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // mvhd/tkhd의 시간 단위. 재생시간은 이 값으로 나눈 초.
    private const uint MovieTimescale = 600;

    // 16.16 고정소수의 1.0.
    private const int FixedOne = 0x00010000;

    // 행렬 마지막 성분 w는 2.30 고정소수라 1.0의 표현이 다름.
    private const int FixedOne230 = 0x40000000;

    private const string AppleCreationDateKey = "com.apple.quicktime.creationdate";

    /// <summary>지정한 해상도와 EXIF를 갖는 JPEG 생성. capturedAt이 null이면 EXIF 세그먼트 자체를 넣지 않음.</summary>
    public static byte[] CreateJpeg(
        int width,
        int height,
        DateTime? capturedAt = null,
        string? utcOffset = null,
        ushort orientation = 1,
        byte filler = 0x00)
    {
        using var jpeg = new MemoryStream();

        WriteMarker(jpeg, 0xD8);

        if (capturedAt is not null)
        {
            WriteExifSegment(jpeg, capturedAt.Value, utcOffset, orientation, width, height);
        }

        WriteStartOfFrame(jpeg, width, height);

        // 내용 해시를 서로 다르게 만들기 위한 주석 세그먼트.
        WriteComment(jpeg, filler);

        WriteMarker(jpeg, 0xD9);

        return jpeg.ToArray();
    }

    /// <summary>
    /// QuickTime 메타데이터만 유효한 MP4 생성. 샘플 데이터(mdat) 없음.
    /// createdAt이 null이면 mvhd 생성 시각이 QuickTime 기준시(1904-01-01)로 기록됨.
    /// </summary>
    public static byte[] CreateMp4(
        DateTime? createdAt = null,
        TimeSpan? duration = null,
        int width = 1920,
        int height = 1440,
        TrackRotation rotation = TrackRotation.None,
        bool includeAudioTrack = true,
        string? appleCreationDate = null,
        byte filler = 0x00)
    {
        var created = createdAt is null
            ? 0u
            : (uint)(createdAt.Value - QuickTimeEpoch).TotalSeconds;

        var durationUnits = (uint)((duration ?? TimeSpan.Zero).TotalSeconds * MovieTimescale);

        using var moov = new MemoryStream();

        moov.Write(BuildMovieHeader(created, durationUnits, includeAudioTrack ? 3u : 2u));

        // 폭·높이 0인 트랙 건너뛰기 경로 검증용. 오디오를 영상보다 앞에 배치.
        if (includeAudioTrack)
        {
            moov.Write(BuildTrack(created, durationUnits, trackId: 1, width: 0, height: 0, TrackRotation.None, isAudio: true));
        }

        moov.Write(BuildTrack(created, durationUnits, trackId: includeAudioTrack ? 2u : 1u, width, height, rotation, isAudio: false));

        if (appleCreationDate is not null)
        {
            moov.Write(BuildAppleMetadata(appleCreationDate));
        }

        using var mp4 = new MemoryStream();

        mp4.Write(BuildFileType());

        // 내용 해시를 서로 다르게 만들기 위한 자리. free 박스는 파서가 무시함.
        mp4.Write(Box("free", [filler]));
        mp4.Write(Box("moov", moov.ToArray()));

        return mp4.ToArray();
    }

    /// <summary>
    /// 기존 JPEG의 SOI 뒤에 EXIF APP1 세그먼트 삽입.
    /// FfmpegFixtures가 만든 디코딩 가능한 이미지에 orientation을 부여할 때 사용.
    /// </summary>
    public static byte[] InsertExifSegment(
        byte[] jpeg,
        DateTime capturedAt,
        ushort orientation,
        int width,
        int height,
        string? utcOffset = null)
    {
        var segment = BuildExifSegment(capturedAt, utcOffset, orientation, width, height);

        using var result = new MemoryStream(jpeg.Length + segment.Length);

        // SOI(0xFFD8) 직후가 APP1의 자리.
        result.Write(jpeg.AsSpan(0, 2));
        result.Write(segment);
        result.Write(jpeg.AsSpan(2));

        return result.ToArray();
    }

    // 메타데이터 추출 실패 시 mtime 대체 경로 검증용.
    public static byte[] CreateOpaqueBytes(string seed)
    {
        return Encoding.UTF8.GetBytes($"not-a-real-media-file:{seed}");
    }

    public static void Write(string path, byte[] content, DateTime? lastWriteTimeUtc = null)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        if (lastWriteTimeUtc is not null)
        {
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value);
        }
    }

    private static void WriteMarker(Stream stream, byte marker)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
    }

    // 해상도는 SOF0에 실림. MetadataExtractor의 JpegDirectory가 이 값을 읽음.
    private static void WriteStartOfFrame(Stream stream, int width, int height)
    {
        WriteMarker(stream, 0xC0);
        WriteBigEndianUInt16(stream, 11);
        stream.WriteByte(8);
        WriteBigEndianUInt16(stream, (ushort)height);
        WriteBigEndianUInt16(stream, (ushort)width);
        stream.WriteByte(1);

        // 컴포넌트 1개: 식별자, 샘플링 계수, 양자화 테이블 번호.
        stream.WriteByte(1);
        stream.WriteByte(0x11);
        stream.WriteByte(0);
    }

    private static void WriteComment(Stream stream, byte filler)
    {
        WriteMarker(stream, 0xFE);
        WriteBigEndianUInt16(stream, 3);
        stream.WriteByte(filler);
    }

    private static void WriteExifSegment(
        Stream stream,
        DateTime capturedAt,
        string? utcOffset,
        ushort orientation,
        int width,
        int height)
    {
        stream.Write(BuildExifSegment(capturedAt, utcOffset, orientation, width, height));
    }

    private static byte[] BuildExifSegment(
        DateTime capturedAt,
        string? utcOffset,
        ushort orientation,
        int width,
        int height)
    {
        var tiff = BuildTiff(capturedAt, utcOffset, orientation, width, height);

        using var segment = new MemoryStream();

        WriteMarker(segment, 0xE1);
        WriteBigEndianUInt16(segment, (ushort)(2 + 6 + tiff.Length));
        segment.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        segment.Write(tiff);

        return segment.ToArray();
    }

    private static byte[] BuildTiff(
        DateTime capturedAt,
        string? utcOffset,
        ushort orientation,
        int width,
        int height)
    {
        // EXIF DateTimeOriginal은 오프셋을 담지 않는 고정 폭 문자열.
        var capturedBytes = Encoding.ASCII.GetBytes(capturedAt.ToString("yyyy:MM:dd HH:mm:ss") + "\0");
        var offsetBytes = utcOffset is null ? [] : Encoding.ASCII.GetBytes(utcOffset + "\0");

        const uint ifd0Offset = 8;
        const uint ifd0Size = 2 + (2 * 12) + 4;

        var subIfdOffset = ifd0Offset + ifd0Size;
        var subEntryCount = utcOffset is null ? 3 : 4;
        var subIfdSize = (uint)(2 + (subEntryCount * 12) + 4);

        var capturedOffset = subIfdOffset + subIfdSize;
        var offsetTimeOffset = capturedOffset + (uint)capturedBytes.Length;

        using var tiff = new MemoryStream();
        var writer = new BinaryWriter(tiff);

        // 리틀엔디언 TIFF 헤더.
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifd0Offset);

        writer.Write((ushort)2);
        WriteEntry(writer, OrientationTag, ShortType, 1, orientation);
        WriteEntry(writer, ExifIfdPointerTag, LongType, 1, subIfdOffset);
        writer.Write(0u);

        writer.Write((ushort)subEntryCount);
        WriteEntry(writer, DateTimeOriginalTag, AsciiType, (uint)capturedBytes.Length, capturedOffset);

        if (utcOffset is not null)
        {
            WriteEntry(writer, OffsetTimeOriginalTag, AsciiType, (uint)offsetBytes.Length, offsetTimeOffset);
        }

        WriteEntry(writer, PixelXDimensionTag, LongType, 1, (uint)width);
        WriteEntry(writer, PixelYDimensionTag, LongType, 1, (uint)height);
        writer.Write(0u);

        writer.Write(capturedBytes);
        writer.Write(offsetBytes);
        writer.Flush();

        return tiff.ToArray();
    }

    // SHORT 단일 값은 4바이트 값 필드에 직접 기록.
    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);

        if (type == ShortType && count == 1)
        {
            writer.Write((ushort)value);
            writer.Write((ushort)0);
            return;
        }

        writer.Write(value);
    }

    // JPEG 세그먼트 길이는 빅엔디언.
    private static void WriteBigEndianUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    // 브랜드가 'qt  '면 MetadataExtractor가 QuickTime으로 판별.
    private static byte[] BuildFileType()
    {
        return Box("ftyp", Ascii("qt  "), U32(0x00000200), Ascii("qt  "));
    }

    private static byte[] BuildMovieHeader(uint created, uint duration, uint nextTrackId)
    {
        return Box(
            "mvhd",
            U32(0),
            U32(created),
            U32(created),
            U32(MovieTimescale),
            U32(duration),
            U32(FixedOne),
            [0x01, 0x00],
            new byte[2 + 8],
            IdentityMatrix(),
            new byte[24],
            U32(nextTrackId));
    }

    // 트랙 구성은 tkhd만 둠. MetadataExtractor의 해상도·회전 판정에 mdia가 필요하지 않음.
    private static byte[] BuildTrack(
        uint created,
        uint duration,
        uint trackId,
        int width,
        int height,
        TrackRotation rotation,
        bool isAudio)
    {
        var tkhd = Box(
            "tkhd",
            // 하위 3비트는 enabled / in movie / in preview.
            U32(0x0000000F),
            U32(created),
            U32(created),
            U32(trackId),
            U32(0),
            U32(duration),
            new byte[8],
            new byte[2 + 2],
            isAudio ? [0x01, 0x00] : new byte[2],
            new byte[2],
            RotationMatrix(rotation),
            U32((uint)width << 16),
            U32((uint)height << 16));

        return Box("trak", tkhd);
    }

    // moov 직속 meta. QuickTime 형식이라 version/flags 없이 하위 박스가 바로 이어짐.
    private static byte[] BuildAppleMetadata(string creationDate)
    {
        var hdlr = Box("hdlr", U32(0), U32(0), Ascii("mdta"), new byte[12], new byte[2]);

        var key = Ascii(AppleCreationDateKey);
        var keys = Box("keys", U32(0), U32(1), Box("mdta", key));

        // ilst 항목은 keys의 1-기반 인덱스를 박스 타입 자리에 실음.
        var data = Box("data", U32(1), U32(0), Encoding.UTF8.GetBytes(creationDate));
        var ilst = Box("ilst", BoxWithTypeCode(1, data));

        return Box("meta", hdlr, keys, ilst);
    }

    private static byte[] RotationMatrix(TrackRotation rotation)
    {
        var (a, b, c, d) = rotation switch
        {
            TrackRotation.Minus90 => (0, FixedOne, -FixedOne, 0),
            TrackRotation.Plus90 => (0, -FixedOne, FixedOne, 0),
            TrackRotation.Minus180 => (-FixedOne, 0, 0, -FixedOne),
            _ => (FixedOne, 0, 0, FixedOne)
        };

        return Matrix(a, b, c, d);
    }

    private static byte[] IdentityMatrix()
    {
        return Matrix(FixedOne, 0, 0, FixedOne);
    }

    // 9개 성분 중 회전·확대를 담당하는 a, b, c, d만 지정. 나머지는 회전 판정에 무관해 0.
    private static byte[] Matrix(int a, int b, int c, int d)
    {
        return Concat(
            U32((uint)a),
            U32((uint)b),
            U32(0),
            U32((uint)c),
            U32((uint)d),
            U32(0),
            U32(0),
            U32(0),
            U32((uint)FixedOne230));
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        return BoxWithType(Ascii(type), parts);
    }

    private static byte[] BoxWithTypeCode(uint code, params byte[][] parts)
    {
        return BoxWithType(U32(code), parts);
    }

    private static byte[] BoxWithType(byte[] type, params byte[][] parts)
    {
        var payload = Concat(parts);
        var box = new byte[8 + payload.Length];

        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        type.CopyTo(box, 4);
        payload.CopyTo(box, 8);

        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;

        foreach (var part in parts)
        {
            length += part.Length;
        }

        var result = new byte[length];
        var offset = 0;

        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);

        return bytes;
    }

    private static byte[] Ascii(string value)
    {
        return Encoding.ASCII.GetBytes(value);
    }
}
