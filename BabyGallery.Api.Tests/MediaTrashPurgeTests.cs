using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Endpoints;
using Xunit;

namespace BabyGallery.Api.Tests;

// 보존 기간이 지난 휴지통 항목의 실삭제.
public sealed class MediaTrashPurgeTests
{
    private const string Password = "familypass1";

    private const int RetentionDays = 30;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 보존_기간이_지난_항목은_파일이_지워지고_처리_시각이_기록됨()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);
        var trashed = await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays + 1));

        var purged = await factory.PurgeTrashAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(trashed));

        var log = Assert.Single(await factory.GetDeletionsAsync());

        Assert.NotNull(log.PurgedAt);
    }

    [Fact]
    public async Task 보존_기간_이내_항목은_유지됨()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);
        var trashed = await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays - 1));

        var purged = await factory.PurgeTrashAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(trashed));
        Assert.Null(Assert.Single(await factory.GetDeletionsAsync()).PurgedAt);
    }

    [Fact]
    public async Task 이미_정리된_항목은_다시_처리하지_않음()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);

        await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays + 1));
        await factory.PurgeTrashAsync();

        // PurgedAt 기록을 기준으로 이미 처리된 항목의 재처리 방지
        Assert.Equal(0, await factory.PurgeTrashAsync());
    }

    [Fact]
    public async Task 휴지통_파일이_이미_없어도_처리_시각을_기록()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);
        var trashed = await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays + 1));

        File.Delete(trashed);

        Assert.Equal(1, await factory.PurgeTrashAsync());
        Assert.NotNull(Assert.Single(await factory.GetDeletionsAsync()).PurgedAt);
    }

    [Fact]
    public async Task 휴지통_폴더까지_사라져도_처리_시각을_기록()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);

        await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays + 1));

        // 상위 디렉터리 제거 후 재처리되는 항목의 DirectoryNotFoundException 정상 처리 검증.
        Directory.Delete(Path.Combine(factory.TrashPath, "20260101"), recursive: true);

        Assert.Equal(1, await factory.PurgeTrashAsync());
        Assert.NotNull(Assert.Single(await factory.GetDeletionsAsync()).PurgedAt);
    }

    [Fact]
    public async Task 정리_후_빈_날짜_폴더가_남지_않음()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);

        await AddTrashedAsync(factory, "20260101/Uploads/2026/01/IMG_0001.JPG", DaysAgo(RetentionDays + 1));
        await factory.PurgeTrashAsync();

        Assert.False(Directory.Exists(Path.Combine(factory.TrashPath, "20260101")));
    }

    [Fact]
    public async Task 다른_날짜_폴더에_남은_항목은_보존됨()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);

        await AddTrashedAsync(factory, "20260101/IMG_0001.JPG", DaysAgo(RetentionDays + 1));
        var kept = await AddTrashedAsync(factory, "20260201/IMG_0002.JPG", DaysAgo(1));

        await factory.PurgeTrashAsync();

        Assert.True(File.Exists(kept));
        Assert.True(Directory.Exists(Path.Combine(factory.TrashPath, "20260201")));
    }

    [Fact]
    public async Task 휴지통_루트_밖을_가리키는_기록은_파일을_건드리지_않음()
    {
        using var factory = new ApiFactory(timeZone: null, trashRetentionDays: RetentionDays);
        var outside = Path.Combine(factory.GalleryPath, "IMG_OUTSIDE.JPG");

        MediaFixtures.Write(outside, MediaFixtures.CreateJpeg(320, 240));

        await factory.AddDeletionAsync("../IMG_OUTSIDE.JPG", DaysAgo(RetentionDays + 1));

        await factory.PurgeTrashAsync();

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task 만료된_업로드_세션이_정리됨()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "purge-session");
        var fileId = await CreateSessionAsync(client);

        await factory.ExpireUploadAsync(fileId, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(1, await factory.RemoveExpiredUploadsAsync());
        Assert.Empty(Directory.GetFiles(factory.StagingPath, $"{fileId}*"));
    }

    [Fact]
    public async Task 만료되지_않은_세션은_유지됨()
    {
        using var factory = new ApiFactory();
        var client = await CreateEditorAsync(factory, "keep-session");
        var fileId = await CreateSessionAsync(client);

        Assert.Equal(0, await factory.RemoveExpiredUploadsAsync());
        Assert.NotEmpty(Directory.GetFiles(factory.StagingPath, $"{fileId}*"));
    }

    private static DateTime DaysAgo(int days)
    {
        return DateTime.UtcNow.AddDays(-days);
    }

    // 휴지통에 파일을 두고 대응하는 감사 로그를 남김. 삭제 엔드포인트를 거치지 않고 시각을 직접 지정.
    private static async Task<string> AddTrashedAsync(ApiFactory factory, string trashRelativePath, DateTime deletedAt)
    {
        var fullPath = Path.Combine(factory.TrashPath, trashRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        MediaFixtures.Write(fullPath, MediaFixtures.CreateJpeg(320, 240));

        await factory.AddDeletionAsync(trashRelativePath, deletedAt);

        return fullPath;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var response = await TestUploads.CreateSessionAsync(client, 1024, "IMG_0001.JPG");

        response.EnsureSuccessStatusCode();

        return response.Headers.Location!.OriginalString.Split('/').Last();
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
