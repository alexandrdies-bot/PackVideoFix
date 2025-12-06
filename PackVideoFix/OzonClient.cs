using System;
using System.Threading;
using System.Threading.Tasks;

namespace PackVideoFix;

internal sealed class OzonClient : IDisposable
{
    public string BaseUrl { get; }
    public string ClientId { get; }
    public string ApiKey { get; }

    public OzonClient(string baseUrl, string clientId, string apiKey)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api-seller.ozon.ru" : baseUrl.Trim();
        ClientId = clientId ?? "";
        ApiKey = apiKey ?? "";
    }

    // позже тут будет реальный запрос в Ozon Seller API
    public Task<(bool ok, string message, string? postingNumber)> TryGetPostingByBarcodeAsync(string barcode, CancellationToken ct)
    {
        return Task.FromResult((false, "Ozon-запросы ещё не включены в этой сборке", (string?)null));
    }

    public void Dispose()
    {
        // позже тут будет HttpClient.Dispose()
    }
}
