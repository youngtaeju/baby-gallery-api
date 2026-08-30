using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FamilyGallery.Api.Tests;

// tus 세션 생성 → 전송 → commit을 한 번에 수행.
// 업로드와 삭제 테스트가 함께 사용.
internal static class TestUploads
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>단일 청크 전송 후 commit 응답 반환. declaredHash 지정 시 손상 전송 시나리오 재현.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(
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

    public static string Sha256(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
