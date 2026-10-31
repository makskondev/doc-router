using System.Net;
using System.Net.Http.Json;
using DocRouter.Core;
using Microsoft.Extensions.Options;

namespace DocRouter.Infrastructure;

/// <summary>
/// ПРЕДПОЛОЖЕНИЕ о формате API (поправьте под реальный сервис):
///   POST SignUrl   {fileName, content(base64)}              → {signature(base64)}
///   POST VerifyUrl {fileName, content(base64), signature}   → {valid, details}
/// </summary>
public sealed class RestSignatureService(HttpClient http, IOptions<SignatureServiceOptions> options) : ISignatureService
{
    private sealed record SignResponse(string? Signature);
    private sealed record VerifyResponse(bool Valid, string? Details);

    public async Task<byte[]> SignAsync(string fileName, byte[] content, CancellationToken ct)
    {
        var resp = await PostAsync(options.Value.SignUrl,
            new { fileName, content = Convert.ToBase64String(content) }, ct);
        var body = await resp.Content.ReadFromJsonAsync<SignResponse>(cancellationToken: ct);
        if (string.IsNullOrEmpty(body?.Signature)) throw new SignatureServiceException("Сервис подписи вернул пустую подпись", transient: false);
        return Convert.FromBase64String(body.Signature);
    }

    public async Task<VerifyResult> VerifyAsync(string fileName, byte[] content, byte[] signature, CancellationToken ct)
    {
        var resp = await PostAsync(options.Value.VerifyUrl, new
        {
            fileName,
            content = Convert.ToBase64String(content),
            signature = Convert.ToBase64String(signature)
        }, ct);
        var body = await resp.Content.ReadFromJsonAsync<VerifyResponse>(cancellationToken: ct)
                   ?? throw new SignatureServiceException("Пустой ответ сервиса проверки подписи", transient: false);
        return new VerifyResult(body.Valid, body.Details);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, object payload, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(url, payload, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SignatureServiceException($"Сервис подписи недоступен: {ex.Message}", transient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SignatureServiceException("Таймаут сервиса подписи", transient: true);
        }

        if (resp.IsSuccessStatusCode) return resp;

        var transient = (int)resp.StatusCode >= 500 || resp.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        var text = await resp.Content.ReadAsStringAsync(ct);
        throw new SignatureServiceException($"HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}", transient);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
