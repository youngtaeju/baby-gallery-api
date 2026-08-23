using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FamilyGallery.Api.Tests;

// 실제 디코딩이 가능한 미디어 생성. MediaFixtures의 조립 바이트는 메타데이터만 유효해 썸네일 검증에 쓸 수 없음.
// 썸네일 기능 자체가 ffmpeg를 요구하므로 테스트도 같은 전제를 따름.
public static class FfmpegFixtures
{
    private const string FfmpegPath = "ffmpeg";

    private const int TimeoutMs = 60_000;

    /// <summary>단색 JPEG 생성. EXIF는 없으며 필요하면 MediaFixtures.InsertExifSegment로 덧붙임.</summary>
    public static byte[] CreateJpeg(int width, int height, string color = "red")
    {
        return Run(
            ".jpg",
            output =>
            [
                "-f", "lavfi",
                "-i", $"color=c={color}:s={width}x{height}",
                "-frames:v", "1",
                output
            ]);
    }

    /// <summary>재생 가능한 H.264 MP4 생성. 첫 프레임과 이후 프레임의 화면이 달라 seek 동작을 가림.</summary>
    public static byte[] CreateMp4(int width, int height, double durationSeconds)
    {
        return Run(
            ".mp4",
            output =>
            [
                "-f", "lavfi",
                "-i", $"testsrc=size={width}x{height}:rate=10:duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}",
                "-c:v", "libx264",
                "-pix_fmt", "yuv420p",
                output
            ]);
    }

    private static byte[] Run(string extension, Func<string, IReadOnlyList<string>> buildArguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fg-fixture-{Guid.NewGuid():N}{extension}");

        try
        {
            var startInfo = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-y");

            foreach (var argument in buildArguments(path))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{FfmpegPath} 프로세스를 시작하지 못했습니다.");

            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill(entireProcessTree: true);

                throw new InvalidOperationException($"{FfmpegPath} 실행이 제한 시간을 넘었습니다.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{FfmpegPath} 실행이 실패했습니다. 종료 코드 {process.ExitCode}: {error}");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
