using System;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using tusdotnet.Interfaces;

namespace FamilyGallery.Api.Services;

// 외부에서 직접 등록된 파일 인덱싱과 휴지통 및 만료 업로드 정리를 주기적으로 직렬 수행.
// API 업로드는 편입 시 별도 등록.
public sealed class MediaIndexingService(
    IServiceScopeFactory scopeFactory,
    ITusStore store,
    IOptions<IndexingOptions> options,
    ILogger<MediaIndexingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));

        try
        {
            // 서비스 시작 직후 1회 실행 후 주기적 반복
            do
            {
                await ScanAsync(stoppingToken);
                await PurgeTrashAsync(stoppingToken);
                await RemoveExpiredUploadsAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // 호스트 종료.
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<MediaScanner>();

            var result = await scanner.ScanAsync(cancellationToken);

            logger.LogInformation(
                "미디어 스캔 완료. 추가 {Added}건, 갱신 {Updated}건, 제거 {Removed}건, 건너뜀 {Skipped}건.",
                result.Added,
                result.Updated,
                result.Removed,
                result.Skipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "미디어 스캔 중 오류가 발생했습니다.");
        }
    }

    private async Task PurgeTrashAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();

            await scope.ServiceProvider.GetRequiredService<MediaTrashService>()
                .PurgeExpiredAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "휴지통 정리 중 오류가 발생했습니다.");
        }
    }

    private async Task RemoveExpiredUploadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await ((ITusExpirationStore)store).RemoveExpiredFilesAsync(cancellationToken);

            if (removed > 0)
            {
                logger.LogInformation("만료된 업로드 세션 {Removed}건을 정리했습니다.", removed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "만료된 업로드 세션 정리 중 오류가 발생했습니다.");
        }
    }
}
