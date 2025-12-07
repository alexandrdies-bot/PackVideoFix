using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace PackVideoFix
{
    internal sealed class OzonClient : IDisposable
    {
        public string BaseUrl { get; }
        public string ClientId { get; }
        public string ApiKey { get; }

        private readonly HttpClient _httpClient;

        public OzonClient(string baseUrl, string clientId, string apiKey)
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api-seller.ozon.ru" : baseUrl.Trim();
            ClientId = clientId ?? string.Empty;
            ApiKey = apiKey ?? string.Empty;

            _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            _httpClient.DefaultRequestHeaders.Add("Client-Id", ClientId);
            _httpClient.DefaultRequestHeaders.Add("Api-Key", ApiKey);
        }

        // ============================================================
        // 🔍 Новый вариант — реальный поиск по штрихкоду
        // ============================================================
        public async Task<(bool Success, string Message, string PostingNumber)> TryGetPostingByBarcodeAsync(
            string barcode,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return (false, "Пустой штрихкод.", "");

            try
            {
                // 1️⃣ Пробуем прямой метод Ozon
                try
                {
                    var req1 = new { barcodes = new[] { barcode } };
                    var json1 = await SendAsync("/v2/postings/barcode", req1, ct);
                    var resp1 = JsonConvert.DeserializeObject<OzonPostingBarcodeResponse>(json1);

                    var posting = resp1?.Result?.FirstOrDefault();
                    if (posting != null && !string.IsNullOrWhiteSpace(posting.PostingNumber))
                        return (true, "OK (v2/barcode)", posting.PostingNumber);
                }
                catch
                {
                    // пропускаем, если метод недоступен (часто не у всех продавцов)
                }

                // 2️⃣ Фолбэк через /v3/posting/fbs/list (ищем по фильтру с баркодом)
                var now = DateTime.UtcNow;
                var request = new
                {
                    filter = new
                    {
                        since = now.AddDays(-3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                        to = now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                        barcode = barcode
                    },
                    limit = 50,
                    offset = 0
                };

                var json2 = await SendAsync("/v3/posting/fbs/list", request, ct);
                var resp2 = JsonConvert.DeserializeObject<FbsPostingListResponse>(json2);

                var posting2 = resp2?.Result?.Postings?.FirstOrDefault();
                if (posting2 == null)
                    return (false, $"Отправление по штрихкоду {barcode} не найдено.", "");

                // фильтр по складу (пример: исключаем Fantasy Craft)
                if (!string.IsNullOrWhiteSpace(posting2.WarehouseName) &&
                    posting2.WarehouseName.Contains("Fantasy", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Склад {posting2.WarehouseName} исключён из поиска.", "");
                }

                return (true, "OK (v3/list)", posting2.PostingNumber);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка при запросе к Ozon: " + ex.Message, "");
            }
        }

        // ============================================================
        // Проверка подключения (оставлено без изменений)
        // ============================================================
        public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                var json = await SendAsync("/v1/warehouse/list", new { }, ct);
                return (true, "Подключение успешно. Ответ: " + json);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка при обращении к /v1/warehouse/list: " + ex.Message);
            }
        }

        // ============================================================
        // Общий метод HTTP POST
        // ============================================================
        private async Task<string> SendAsync(string endpoint, object payload, CancellationToken ct)
        {
            var url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? endpoint
                : (endpoint.StartsWith("/") ? endpoint : "/" + endpoint);

            var json = JsonConvert.SerializeObject(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };

            using var resp = await _httpClient.SendAsync(req, ct);
            var content = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}: {content}");

            return content;
        }

        public void Dispose() => _httpClient.Dispose();
    }

    // ============================================================
    // 🧩 DTO модели под новые API
    // ============================================================

    public sealed class OzonPostingBarcodeResponse
    {
        [JsonProperty("result")]
        public List<OzonPostingBarcodeItem>? Result { get; set; }
    }

    public sealed class OzonPostingBarcodeItem
    {
        [JsonProperty("posting_number")]
        public string PostingNumber { get; set; } = "";

        [JsonProperty("order_id")]
        public long OrderId { get; set; }
    }

    public sealed class FbsPostingListResponse
    {
        [JsonProperty("result")]
        public FbsPostingListResult? Result { get; set; }
    }

    public sealed class FbsPostingListResult
    {
        [JsonProperty("postings")]
        public List<FbsPostingShort>? Postings { get; set; }
    }

    public sealed class FbsPostingShort
    {
        [JsonProperty("posting_number")]
        public string PostingNumber { get; set; } = "";

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("warehouse_name")]
        public string? WarehouseName { get; set; }

        [JsonProperty("products")]
        public List<FbsProductShort>? Products { get; set; }
    }

    public sealed class FbsProductShort
    {
        [JsonProperty("sku")]
        public long Sku { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }
}
