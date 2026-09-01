using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api;
using BabyGallery.Api.Data;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using tusdotnet.Interfaces;

namespace BabyGallery.Api.Tests;

// 테스트 클래스마다 별도 인스턴스. DB와 rate limiter 상태가 클래스 간에 섞이지 않음.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // JwtOptions의 MinLength(32) 충족용.
    private const string SigningKey = "baby-gallery-integration-test-signing-key";

    private readonly string _rootPath;

    private readonly string _databasePath;

    // 스캔 대상과 SQLite 파일을 분리. 스캐너 순회에 DB 파일이 섞이지 않음.
    public string GalleryPath { get; }

    // 캐시 적중·재생성 여부를 파일로 직접 확인하기 위해 노출.
    public string ThumbnailPath { get; }

    // 스테이징이 갤러리 마운트 내부인지 파일로 직접 확인하기 위해 노출.
    public string StagingPath { get; }

    // 삭제분이 실제로 옮겨졌는지 파일로 직접 확인하기 위해 노출.
    public string TrashPath { get; }

    private readonly string? _timeZone;

    private readonly long? _maxUploadSizeBytes;

    private readonly int? _trashRetentionDays;

    // xUnit의 IClassFixture는 무인자 생성자만 활성화 가능. 선택적 매개변수로는 대체되지 않음.
    public ApiFactory()
        : this(null)
    {
    }

    // 촬영일시 해석과 업로드 상한은 구성값에 좌우되면 안 됨. 필요한 테스트만 값을 명시.
    // IClassFixture는 public 생성자가 하나뿐이어야 하므로 internal로 노출.
    internal ApiFactory(string? timeZone, long? maxUploadSizeBytes = null, int? trashRetentionDays = null)
    {
        _timeZone = timeZone;
        _maxUploadSizeBytes = maxUploadSizeBytes;
        _trashRetentionDays = trashRetentionDays;

        _rootPath = Path.Combine(Path.GetTempPath(), $"fg-test-{Guid.NewGuid():N}");
        GalleryPath = Path.Combine(_rootPath, "gallery");
        Directory.CreateDirectory(GalleryPath);
        ThumbnailPath = Path.Combine(_rootPath, "thumbnails");
        StagingPath = Path.Combine(GalleryPath, ".uploads");
        TrashPath = Path.Combine(GalleryPath, ".trash");
        _databasePath = Path.Combine(_rootPath, "test.db");

        // Services 접근 시 호스트가 생성되며 ConfigureWebHost가 적용됨.
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    public async Task AddUserAsync(string username, string password, UserRole role = UserRole.Viewer)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Users.Add(new User
        {
            Username = username,
            DisplayName = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // appsettings.Development.json의 .local 경로를 타지 않도록 별도 환경 사용.
        builder.UseEnvironment("Testing");

        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
            ["Jwt:SigningKey"] = SigningKey,
            ["Gallery:RootPath"] = GalleryPath,
            ["Thumbnail:CachePath"] = ThumbnailPath
        };

        // 시간대 지정 시에만 기본 설정 덮어씀.
        if (_timeZone is not null)
        {
            overrides["Indexing:TimeZone"] = _timeZone;
        }

        if (_trashRetentionDays is not null)
        {
            overrides["Upload:TrashRetentionDays"] =
                _trashRetentionDays.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_maxUploadSizeBytes is not null)
        {
            overrides["Upload:MaxUploadSizeBytes"] =
                _maxUploadSizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // 기존 구성 소스 뒤에 추가되어 우선 적용.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));

        // 주기 스캔이 테스트의 파일 조작과 경합. 스캔 시점은 테스트가 직접 정함.
        builder.ConfigureTestServices(services =>
            services.Remove(services.Single(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(MediaIndexingService))));
    }

    // 목록·페이징 검증용. 인덱싱을 거치지 않고 정렬 키만 지정해 직접 등록.
    public async Task<IReadOnlyList<int>> AddMediaAsync(params (string RelativePath, DateTime CapturedAt)[] items)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var added = new List<MediaItem>();

        foreach (var (relativePath, capturedAt) in items)
        {
            var item = new MediaItem
            {
                RelativePath = relativePath,
                OriginalFileName = Path.GetFileName(relativePath),
                ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                MediaType = MediaType.Image,
                FileSize = 1024,
                CapturedAt = capturedAt,
                FileModifiedAt = capturedAt,
                IndexedAt = DateTime.UtcNow
            };

            db.MediaItems.Add(item);
            added.Add(item);
        }

        await db.SaveChangesAsync();

        return added.Select(item => item.Id).ToList();
    }

    // 중복 판정 검증용. 해시를 지정해 등록.
    public async Task<int> AddMediaWithHashAsync(string relativePath, string contentHash)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var capturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var item = new MediaItem
        {
            RelativePath = relativePath,
            OriginalFileName = Path.GetFileName(relativePath),
            ContentHash = contentHash,
            MediaType = MediaType.Image,
            FileSize = 1024,
            CapturedAt = capturedAt,
            FileModifiedAt = capturedAt,
            IndexedAt = DateTime.UtcNow
        };

        db.MediaItems.Add(item);

        await db.SaveChangesAsync();

        return item.Id;
    }

    // 스캔으로 등록된 항목의 Id 조회. 상대 경로는 API 응답에 없으므로 DB에서 직접 확인.
    public async Task<int> FindMediaIdAsync(string relativePath)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.MediaItems
            .AsNoTracking()
            .Where(m => m.RelativePath == relativePath)
            .Select(m => m.Id)
            .SingleAsync();
    }

    // 감사 로그는 API로 노출하지 않으므로 DB에서 직접 확인.
    public async Task<IReadOnlyList<MediaDeletion>> GetDeletionsAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.MediaDeletions.AsNoTracking().ToListAsync();
    }

    // 보존 기간 판정은 DeletedAt 기준. 삭제 엔드포인트를 거치지 않고 시각을 직접 지정.
    public async Task AddDeletionAsync(string trashRelativePath, DateTime deletedAt)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.MediaDeletions.Add(new MediaDeletion
        {
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            OriginalRelativePath = "IMG_0001.JPG",
            TrashRelativePath = trashRelativePath,
            OriginalFileName = "IMG_0001.JPG",
            FileSize = 1024,
            DeletedByUserId = 1,
            DeletedByUsername = "tester",
            DeletedAt = deletedAt
        });

        await db.SaveChangesAsync();
    }

    // 보존 기간 정리 검증도 주기가 아닌 명시적 1회 실행으로 수행.
    public async Task<int> PurgeTrashAsync()
    {
        using var scope = Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<MediaTrashService>()
            .PurgeExpiredAsync(CancellationToken.None);
    }

    public async Task ExpireUploadAsync(string fileId, DateTimeOffset expiresAt)
    {
        var store = (ITusExpirationStore)Services.GetRequiredService<ITusStore>();

        await store.SetExpirationAsync(fileId, expiresAt, CancellationToken.None);
    }

    public async Task<int> RemoveExpiredUploadsAsync()
    {
        var store = (ITusExpirationStore)Services.GetRequiredService<ITusStore>();

        return (await store.RemoveExpiredFilesAsync(CancellationToken.None));
    }

    // 인덱싱 동작 검증은 백그라운드 주기가 아닌 명시적 1회 실행으로 수행.
    public async Task<MediaScanResult> ScanAsync()
    {
        using var scope = Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<MediaScanner>().ScanAsync(CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || !Directory.Exists(_rootPath))
        {
            return;
        }

        // SQLite 파일 핸들이 남아 있으면 정리 실패. 임시 디렉터리라 방치해도 무해.
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
