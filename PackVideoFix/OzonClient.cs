// File: OzonClient.cs  (горячий фикс под v3 product/info/list)
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

                // 1) Прямой метод по штрихкоду (если доступен аккаунту)
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

                // 2) Поиск за последние 7 дней по списку постингов с баркодами
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

                // 3) Картинка по SKU через v3 product/info/list
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

        // >>> ИСПРАВЛЕНО: теперь v3 product/info/list и парсинг result.items
        private async Task<string?> TryGetPrimaryImageBySkusAsync(IEnumerable<long> skus, CancellationToken ct)
        {
            var ids = skus?.Distinct().ToArray() ?? Array.Empty<long>();
            if (ids.Length == 0) return null;

            try
            {
                var json = await SendAsync("/v3/product/info/list", new { sku = ids }, ct);
                var jo = JObject.Parse(json);

                var items = jo["result"]?["items"] as JArray
                            ?? jo["items"] as JArray
                            ?? new JArray();

                // Берём первое попавшееся изображение из primary_image или images[0]
                foreach (var it in items)
                {
                    var pimg = it["primary_image"];
                    if (pimg != null)
                    {
                        if (pimg.Type == JTokenType.String)
                            return pimg.Value<string>();
                        if (pimg is JArray pa && pa.Count > 0)
                            return pa[0]!.Value<string>();
                    }

                    var imgs = it["images"] as JArray;
                    if (imgs != null && imgs.Count > 0)
                    {
                        var x = imgs[0];
                        if (x.Type == JTokenType.String) return x.Value<string>();
                        if (x["url"] != null) return x["url"]!.Value<string>();
                        if (x["default_url"] != null) return x["default_url"]!.Value<string>();
                        if (x["image"] != null) return x["image"]!.Value<string>();
                    }
                }

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

        // >>> Чуть безопаснее: дергаем доступный метод сборки, а не старые /v1 карточки
        public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                var now = DateTime.UtcNow;
                var body = new
                {
                    filter = new
                    {
                        cutoff_from = now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                        cutoff_to = now.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                    },
                    limit = 1,
                    offset = 0,
                    sort_dir = "ASC"
                };
                var json = await SendAsync("/v1/assembly/fbs/product/list", body, ct);
                return (true, "Подключение успешно. Ответ: " + json);
            }
            catch (Exception ex)
            {
                return (false, "Ошибка при обращении к /v1/assembly/fbs/product/list: " + ex.Message);
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

    // ======= DTO (как в вашей рабочей версии) =======
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

        [JsonProperty("barcodes")] public JToken? Barcodes { get; set; } // объект/массив/строка
        [JsonProperty("packages")] public List<FbsPackage>? Packages { get; set; }
        [JsonProperty("products")] public List<FbsProductShort>? Products { get; set; }
    }

    public sealed class FbsPackage
    {
        [JsonProperty("package_id")] public string? PackageId { get; set; }
        [JsonProperty("barcodes")] public JToken? Barcodes { get; set; }
    }

    public sealed class FbsProductShort
    {
        [JsonProperty("sku")] public long Sku { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("quantity")] public int Quantity { get; set; }
        [JsonProperty("barcode")] public string? Barcode { get; set; }
    }

    // (Старые DTO для v2 оставил — не используются, но не мешают)
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
