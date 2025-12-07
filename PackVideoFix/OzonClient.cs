// File: /mnt/data/OzonClient.cs
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        public async Task<(bool Success, string Message, string PostingNumber, string? PrimaryImageUrl)>
            TryGetPostingAndImageByBarcodeAsync(string barcode, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return (false, "Пустой штрихкод.", "", null);

            try
            {
                barcode = barcode.Trim();
                string? postingNumber = null;
                long? firstSku = null;

                // 1) Прямой метод (если доступен)
                try
                {
                    var req1 = new { barcodes = new[] { barcode } };
                    var json1 = await SendAsync("/v2/postings/barcode", req1, ct);
                    var resp1 = JsonConvert.DeserializeObject<OzonPostingBarcodeResponse>(json1);
                    var posting = resp1?.Result?.FirstOrDefault(x =>
                        string.Equals(x?.PostingBarcode, barcode, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x?.PackageBarcode, barcode, StringComparison.OrdinalIgnoreCase));
                    if (posting != null && !string.IsNullOrWhiteSpace(posting.PostingNumber))
                        postingNumber = posting.PostingNumber;
                }
                catch { /* у части продавцов метода нет — продолжаем */ }

                // 2) Точный поиск: сканим packages[].barcodes[...] и прочие варианты
                if (string.IsNullOrWhiteSpace(postingNumber))
                {
                    var now = DateTime.UtcNow;
                    var since = now.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                    var to = now.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

                    int limit = 100, offset = 0, pages = 0;
                    while (true)
                    {
                        var req = new
                        {
                            filter = new { since, to },
                            with = new
                            {
                                analytics_data = false,
                                financial_data = false,
                                barcodes = true,
                                packages = true,
                                products = true
                            },
                            limit,
                            offset
                        };

                        var json = await SendAsync("/v3/posting/fbs/list", req, ct);
                        var resp = JsonConvert.DeserializeObject<FbsPostingListResponse>(json);

                        var hit = resp?.Result?.Postings?.FirstOrDefault(p => HasBarcode(p, barcode));
                        if (hit != null)
                        {
                            postingNumber = hit.PostingNumber;
                            firstSku = hit.Products?.FirstOrDefault()?.Sku;
                            break;
                        }

                        if (resp?.Result?.HasNext != true) break;
                        offset += limit;
                        if (++pages > 15) break; // защита
                    }
                }

                if (string.IsNullOrWhiteSpace(postingNumber))
                    return (false, $"Отправление со штрихкодом {barcode} не найдено за последние 7 дней.", "", null);

                // 3) Картинка по SKU
                string? primaryImage = null;
                if (firstSku.HasValue)
                    primaryImage = await TryGetPrimaryImageBySkusAsync(new[] { firstSku.Value }, ct);

                return (true, "OK", postingNumber!, primaryImage);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка при запросе к Ozon: " + ex.Message, "", null);
            }
        }

        private static bool HasBarcode(FbsPostingShort p, string barcode)
        {
            if (p == null) return false;

            // packages → barcodes (объект или массив)
            if (p.Packages != null)
            {
                foreach (var pack in p.Packages)
                {
                    if (pack?.Barcodes == null) continue;
                    if (EnumerateBarcodeStrings(pack.Barcodes).Any(b => StringEquals(b, barcode)))
                        return true;
                }
            }

            // barcodes на уровне отправления (объект или массив)
            if (p.Barcodes != null &&
                EnumerateBarcodeStrings(p.Barcodes).Any(b => StringEquals(b, barcode)))
                return true;

            // иногда кладут в products[].barcode
            if (p.Products != null)
            {
                foreach (var pr in p.Products)
                {
                    if (!string.IsNullOrWhiteSpace(pr?.Barcode) && StringEquals(pr.Barcode, barcode))
                        return true;
                }
            }

            return false;

            static bool StringEquals(string? a, string b) =>
                !string.IsNullOrWhiteSpace(a) &&
                string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
        }

        // Рекурсивно вытаскивает все строки из JToken (массивы, объекты, скаляры)
        private static IEnumerable<string> EnumerateBarcodeStrings(JToken token)
        {
            if (token == null) yield break;

            switch (token.Type)
            {
                case JTokenType.String:
                    yield return token.Value<string>()!;
                    yield break;
                case JTokenType.Array:
                    foreach (var t in token.Children())
                        foreach (var s in EnumerateBarcodeStrings(t))
                            yield return s;
                    yield break;
                case JTokenType.Object:
                    foreach (var prop in token.Children<JProperty>())
                        foreach (var s in EnumerateBarcodeStrings(prop.Value))
                            yield return s;
                    yield break;
                default:
                    yield break;
            }
        }

        private async Task<string?> TryGetPrimaryImageBySkusAsync(IEnumerable<long> skus, CancellationToken ct)
        {
            var ids = skus?.Distinct().ToArray() ?? Array.Empty<long>();
            if (ids.Length == 0) return null;

            var req = new { product_id = ids };
            try
            {
                var json = await SendAsync("/v2/product/info/list", req, ct);
                var resp = JsonConvert.DeserializeObject<ProductInfoListResponse>(json);
                var item = resp?.Result?.FirstOrDefault();
                if (item == null) return null;

                if (!string.IsNullOrWhiteSpace(item.PrimaryImage))
                    return item.PrimaryImage;

                if (item.Images != null && item.Images.Count > 0)
                    return item.Images[0];

                return null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Success, string Message, string PostingNumber)> TryGetPostingByBarcodeAsync(
            string barcode, CancellationToken ct)
        {
            var (ok, msg, posting, _) = await TryGetPostingAndImageByBarcodeAsync(barcode, ct);
            return (ok, msg, posting);
        }

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

    // ================== DTO ==================
    public sealed class OzonPostingBarcodeResponse
    {
        [JsonProperty("result")] public List<OzonPostingBarcodeItem>? Result { get; set; }
    }

    public sealed class OzonPostingBarcodeItem
    {
        [JsonProperty("posting_number")] public string PostingNumber { get; set; } = "";
        [JsonProperty("order_id")] public long OrderId { get; set; }
        [JsonProperty("posting_barcode")] public string? PostingBarcode { get; set; }
        [JsonProperty("package_barcode")] public string? PackageBarcode { get; set; }
    }

    public sealed class FbsPostingListResponse
    {
        [JsonProperty("result")] public FbsPostingListResult? Result { get; set; }
    }

    public sealed class FbsPostingListResult
    {
        [JsonProperty("postings")] public List<FbsPostingShort>? Postings { get; set; }
        [JsonProperty("has_next")] public bool HasNext { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
    }

    public sealed class FbsPostingShort
    {
        [JsonProperty("posting_number")] public string PostingNumber { get; set; } = "";
        [JsonProperty("status")] public string? Status { get; set; }
        [JsonProperty("warehouse_name")] public string? WarehouseName { get; set; }

        // Может быть объектом или массивом; читаем как JToken
        [JsonProperty("barcodes")] public JToken? Barcodes { get; set; }

        [JsonProperty("packages")] public List<FbsPackage>? Packages { get; set; }
        [JsonProperty("products")] public List<FbsProductShort>? Products { get; set; }
    }

    public sealed class FbsPackage
    {
        [JsonProperty("package_id")] public string? PackageId { get; set; }
        [JsonProperty("barcodes")] public JToken? Barcodes { get; set; } // объект/массив/строка
    }

    public sealed class FbsProductShort
    {
        [JsonProperty("sku")] public long Sku { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("quantity")] public int Quantity { get; set; }
        [JsonProperty("barcode")] public string? Barcode { get; set; }
    }

    public sealed class ProductInfoListResponse
    {
        [JsonProperty("result")] public List<ProductInfoItem>? Result { get; set; }
    }

    public sealed class ProductInfoItem
    {
        [JsonProperty("product_id")] public long ProductId { get; set; }
        [JsonProperty("primary_image")] public string? PrimaryImage { get; set; }
        [JsonProperty("images")] public List<string> Images { get; set; } = new();
    }
}
