using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Endpoints;
using Xunit;

namespace BabyGallery.Api.Tests;

// 전송 완료 후 원본 트리 편입 과정 검증. 검증 실패분은 원본 트리에 남기지 않음.
public sealed class MediaIngestTests
{
    private const string Password = "familypass1";

    private static readonly DateTime CapturedAt = new(2026, 5, 17, 3, 24, 51, DateTimeKind.Utc);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 완료하면_원본_트리에_편입되고_인덱스에_등록()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-basic");
        var content = MediaFixtures.CreateJpeg(640, 480, CapturedAt, utcOffset: "+00:00");

        var response = await UploadAsync(client, content, "IMG_0001.JPG");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var mediaId = await ReadMediaIdAsync(response);
        var item = await GetMediaAsync(client, mediaId);

        Assert.Equal("IMG_0001.JPG", item.FileName);
        Assert.Equal(nameof(MediaType.Image), item.MediaType);
        Assert.Equal(content.Length, item.FileSize);
        Assert.Single(GalleryFiles(factory, "Uploads"));
    }

    [Fact]
    public async Task 저장_경로가_촬영일시_기준_규칙을_따름()
    {
        using var factory = new ApiFactory(timeZone: "UTC");
        var client = await CreateEditorAsync(factory, "ingest-path");
        var content = MediaFixtures.CreateJpeg(640, 480, CapturedAt, utcOffset: "+00:00");

        await UploadAsync(client, content, "IMG_0001.JPG");

        var stored = Assert.Single(GalleryFiles(factory, "Uploads"));
        var hashPrefix = Sha256(content)[..8];

        Assert.Equal(
            Path.Combine("Uploads", "2026", "05", $"20260517_032451_{hashPrefix}.jpg"),
            stored);
    }

    [Fact]
    public async Task 촬영일시가_없으면_업로드_시각으로_대체()
    {
        using var factory = new ApiFactory(timeZone: "UTC");
        var client = await CreateEditorAsync(factory, "ingest-nodate");

        // EXIF 세그먼트 자체가 없는 JPEG.
        var content = MediaFixtures.CreateJpeg(320, 240);

        await UploadAsync(client, content, "IMG_0001.JPG");

        var stored = Assert.Single(GalleryFiles(factory, "Uploads"));
        var today = DateTime.UtcNow;

        Assert.StartsWith(
            Path.Combine("Uploads", today.ToString("yyyy", CultureInfo.InvariantCulture), today.ToString("MM", CultureInfo.InvariantCulture)),
            stored,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 확장자는_클라이언트_파일명이_아니라_판별_결과에서_도출()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-ext");
        var content = MediaFixtures.CreateJpeg(320, 240);

        // 실제 내용은 JPEG인데 클라이언트가 다른 확장자를 주장.
        var response = await UploadAsync(client, content, "payload.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = Assert.Single(GalleryFiles(factory, "Uploads"));

        Assert.EndsWith(".jpg", stored, StringComparison.Ordinal);

        // 원본 파일명은 표시용으로 그대로 보관.
        var item = await GetMediaAsync(client, await ReadMediaIdAsync(response));

        Assert.Equal("payload.txt", item.FileName);
    }

    [Fact]
    public async Task 해시가_일치하지_않으면_422이고_원본_트리에_남지_않음()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-badhash");
        var content = MediaFixtures.CreateJpeg(320, 240);

        // 전송 손상이나 클라이언트 오류를 모사. 선언 해시와 실제 내용이 어긋남.
        var response = await UploadAsync(client, content, "IMG_0001.JPG", declaredHash: Sha256([0x00]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(GalleryFiles(factory, "Uploads"));
        Assert.Empty(await ListMediaAsync(client));
    }

    [Fact]
    public async Task 화이트리스트_밖_형식이면_415이고_원본_트리에_남지_않음()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-badtype");
        var content = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n1 0 obj\n");

        var response = await UploadAsync(client, content, "IMG_0001.JPG");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(GalleryFiles(factory, "Uploads"));
        Assert.Empty(await ListMediaAsync(client));
    }

    [Fact]
    public async Task 편입이_끝나면_스테이징이_비워짐()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-cleanup");

        await UploadAsync(client, MediaFixtures.CreateJpeg(320, 240), "IMG_0001.JPG");

        // 본문과 사이드카가 모두 정리되어야 함. 남으면 만료 정리 때까지 마운트를 점유.
        Assert.Empty(Directory.GetFiles(factory.StagingPath));
    }

    [Fact]
    public async Task 검증에_실패해도_스테이징이_비워짐()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-cleanup-fail");
        var content = MediaFixtures.CreateJpeg(320, 240);

        await UploadAsync(client, content, "IMG_0001.JPG", declaredHash: Sha256([0x00]));

        Assert.Empty(Directory.GetFiles(factory.StagingPath));
    }

    [Fact]
    public async Task 이미_같은_해시가_있으면_기존_mediaId를_반환하고_파일을_추가하지_않음()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-dupe");
        var content = MediaFixtures.CreateJpeg(320, 240);

        var first = await UploadAsync(client, content, "IMG_0001.JPG");
        var second = await UploadAsync(client, content, "IMG_0001_복사본.JPG");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var duplicate = await ReadCommitAsync(second);

        Assert.Equal(await ReadMediaIdAsync(first), duplicate.MediaId);
        Assert.True(duplicate.Duplicate);

        // 내용이 같으므로 원본 트리에 두 벌이 남으면 안 됨.
        Assert.Single(GalleryFiles(factory, "Uploads"));
    }

    [Fact]
    public async Task 편입된_파일은_다음_스캔에서_중복_등록되지_않음()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-rescan");

        var response = await UploadAsync(client, MediaFixtures.CreateJpeg(320, 240), "IMG_0001.JPG");
        var mediaId = await ReadMediaIdAsync(response);

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);

        var items = await ListMediaAsync(client);

        Assert.Equal([mediaId], items.Select(i => i.Id));
    }

    [Fact]
    public async Task 편입_후_원본_조회가_가능()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "ingest-original");
        var content = MediaFixtures.CreateJpeg(640, 480, CapturedAt, utcOffset: "+00:00");

        var mediaId = await ReadMediaIdAsync(await UploadAsync(client, content, "IMG_0001.JPG"));

        var response = await client.GetAsync($"/media/{mediaId}/original", TestToken);

        response.EnsureSuccessStatusCode();

        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(TestToken));
        Assert.Equal($"\"{Sha256(content)}\"", response.Headers.ETag?.ToString());
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string? declaredHash = null)
    {
        var metadata = string.Join(
            ",",
            $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName))}",
            $"contentHash {Convert.ToBase64String(Encoding.UTF8.GetBytes(declaredHash ?? Sha256(content)))}");

        var creation = new HttpRequestMessage(HttpMethod.Post, "/media/uploads");

        creation.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        creation.Headers.TryAddWithoutValidation(
            "Upload-Length",
            content.Length.ToString(CultureInfo.InvariantCulture));
        creation.Headers.TryAddWithoutValidation("Upload-Metadata", metadata);

        var created = await client.SendAsync(creation, TestToken);

        created.EnsureSuccessStatusCode();

        var patch = new HttpRequestMessage(HttpMethod.Patch, created.Headers.Location!.OriginalString)
        {
            Content = new ByteArrayContent(content)
        };

        patch.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        patch.Headers.TryAddWithoutValidation("Upload-Offset", "0");
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");

        var written = await client.SendAsync(patch, TestToken);

        written.EnsureSuccessStatusCode();

        return await client.PostAsync(
            $"{created.Headers.Location!.OriginalString}/commit",
            content: null,
            TestToken);
    }

    private static async Task<int> ReadMediaIdAsync(HttpResponseMessage response)
    {
        return (await ReadCommitAsync(response)).MediaId;
    }

    private static async Task<UploadCommitResponse> ReadCommitAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<UploadCommitResponse>(TestToken);

        Assert.NotNull(payload);

        return payload;
    }

    // 지정한 하위 디렉터리의 파일만 갤러리 루트 기준 상대 경로로 반환.
    private static List<string> GalleryFiles(ApiFactory factory, string subdirectory)
    {
        var root = Path.Combine(factory.GalleryPath, subdirectory);

        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(factory.GalleryPath, path))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
    }

    private static string Sha256(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static async Task<MediaItemResponse> GetMediaAsync(HttpClient client, int mediaId)
    {
        var item = await client.GetFromJsonAsync<MediaItemResponse>($"/media/{mediaId}", TestToken);

        Assert.NotNull(item);

        return item;
    }

    private static async Task<IReadOnlyList<MediaItemResponse>> ListMediaAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<MediaListResponse>("/media", TestToken);

        Assert.NotNull(page);

        return page.Items;
    }

    private static async Task<HttpClient> CreateEditorAsync(ApiFactory factory, string username)
    {
        await factory.AddUserAsync(username, Password, UserRole.Editor);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(username, Password), TestToken);

        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        return client;
    }
}
