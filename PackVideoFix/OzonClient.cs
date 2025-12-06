using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

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

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl)
            };
            _httpClient.DefaultRequestHeaders.Add("Client-Id", ClientId);
            _httpClient.DefaultRequestHeaders.Add("Api-Key", ApiKey);
        }

        /// <summary>
        /// ВРЕМЕННО: получить номер отправления.
        /// Сейчас: просто берёт первый товар и первый posting за последние ~сутки.
        /// Потом сюда добавим сопоставление со штрихкодом и фильтр по складу.
        /// </summary>
        public async Task<(bool Success, string Message, string PostingNumber)> TryGetPostingByBarcodeAsync(
    string barcode,
    CancellationToken ct)
        {
            try
            {
                var now = DateTime.UtcNow;
                var from = now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                var to = now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

                var request = new
                {
                    filter = new
                    {
                        cutoff_from = from,
                        cutoff_to = to
                        // delivery_method_id УБРАЛИ, чтобы не ломать proto
                    },
                    dir = "ASC",
                    limit = 50,
                    offset = 0
                };

                var json = await SendAsync("/v1/assembly/fbs/product/list", request, ct);
                var resp = JsonConvert.DeserializeObject<FbsProductListResponse>(json);

                if (resp == null || resp.Products == null || resp.Products.Count == 0)
                    return (false, "Список товаров в отправлениях пуст.", "");

                var firstProduct = resp.Products[0];
                var firstPosting = firstProduct.Postings.FirstOrDefault();

                if (firstPosting == null || string.IsNullOrWhiteSpace(firstPosting.PostingNumber))
                    return (false, "В ответе нет posting_number.", "");

                return (true, "", firstPosting.PostingNumber);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка запроса к Ozon: " + ex.Message, "");
            }
        }

        /// <summary>
        /// Проверка подключения к Ozon (используется в SettingsForm).
        /// </summary>
        public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                // как в рабочем проекте: пробуем получить список складов
                var json = await SendAsync("/v1/warehouse/list", new { }, ct);

                return (true,
                    "Подключение к Ozon успешно. Удалось получить список складов.\nОтвет: " + json);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка при обращении к /v1/warehouse/list: " + ex.Message);
            }
        }

        private async Task<string> SendAsync(string endpoint, object payload, CancellationToken ct)
        {
            var url = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? endpoint
                : endpoint.StartsWith("/")
                    ? endpoint
                    : "/" + endpoint;

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

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    // ===== МОДЕЛИ =====

    // (старый класс на /v2/postings/barcode пока оставляем, вдруг пригодится)
    public class OzonPostingResponse
    {
        [JsonProperty("posting_number")]
        public string PostingNumber { get; set; } = "";
    }

    public sealed class FbsProductListResponse
    {
        [JsonProperty("has_next")]
        public bool HasNext { get; set; }

        [JsonProperty("products")]
        public System.Collections.Generic.List<FbsProductItem> Products { get; set; }
            = new System.Collections.Generic.List<FbsProductItem>();

        [JsonProperty("products_count")]
        public int ProductsCount { get; set; }
    }

    public sealed class FbsProductItem
    {
        [JsonProperty("picture_url")]
        public string PictureUrl { get; set; } = "";

        [JsonProperty("postings")]
        public System.Collections.Generic.List<FbsProductPosting> Postings { get; set; }
            = new System.Collections.Generic.List<FbsProductPosting>();

        [JsonProperty("product_name")]
        public string ProductName { get; set; } = "";

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("sku")]
        public long Sku { get; set; }
    }

    public sealed class FbsProductPosting
    {
        [JsonProperty("posting_number")]
        public string PostingNumber { get; set; } = "";

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }
}