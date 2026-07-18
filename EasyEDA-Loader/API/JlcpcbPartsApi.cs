using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Client for JLCPCB's SMT parts catalogue API
    /// (https://jlcpcb.com/api/overseas-pcb-order/v1/shoppingCart/smtGood/selectSmtComponentList/v2).
    /// Used by the BOM Builder to look up a part by LCSC number or by value+package,
    /// prefer Basic parts, and capture package/stock/price/datasheet.
    /// </summary>
    public class JlcpcbPartsApi
    {
        private const string SearchUrl =
            "https://jlcpcb.com/api/overseas-pcb-order/v1/shoppingCart/smtGood/selectSmtComponentList/v2";
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;

        public JlcpcbPartsApi()
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://jlcpcb.com");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://jlcpcb.com/parts");
        }

        public class JlcpcbPart
        {
            public string Lcsc { get; set; }
            public string Package { get; set; }
            public string LibraryType { get; set; }
            public bool IsBasic => string.Equals(LibraryType, "base", StringComparison.OrdinalIgnoreCase);
            public string Description { get; set; }
            public string Model { get; set; }
            public string Brand { get; set; }
            public string Category { get; set; }
            public int Stock { get; set; }
            public double UnitPrice { get; set; }
            public string DatasheetUrl { get; set; }
            public string Mpn { get; set; }
        }

        private static JlcpcbPart MapRow(JObject row)
        {
            if (row == null)
                return null;

            string lcsc = SafeString(row, "componentCode");
            if (string.IsNullOrWhiteSpace(lcsc))
                lcsc = SafeString(row, "lcscPart");

            double unitPrice = 0;
            var prices = row["componentPrices"] as JArray;
            if (prices != null && prices.Count > 0)
            {
                try
                {
                    var lastTier = (JObject)prices[prices.Count - 1];
                    unitPrice = SafeDouble(lastTier, "productPrice");
                }
                catch { }
            }

            string datasheet = SafeString(row, "dataManualUrl");
            if (string.IsNullOrWhiteSpace(datasheet))
                datasheet = SafeString(row, "dataManualOfficialLink");
            if (string.IsNullOrWhiteSpace(datasheet))
                datasheet = SafeString(row, "dataManualFileAccessIdUrl");

            return new JlcpcbPart
            {
                Lcsc = lcsc,
                Package = SafeString(row, "componentSpecificationEn"),
                LibraryType = SafeString(row, "componentLibraryType"),
                Description = SafeString(row, "describe"),
                Model = SafeString(row, "componentModelEn"),
                Brand = SafeString(row, "componentBrandEn"),
                Category = SafeString(row, "secondSortName"),
                Stock = Math.Max(SafeInt(row, "stockCount"), SafeInt(row, "overseasStockCount")),
                UnitPrice = unitPrice,
                DatasheetUrl = datasheet,
                Mpn = SafeString(row, "componentModelEn"),
            };
        }

        private static string SafeString(JObject o, string key)
        {
            var v = o[key];
            return v == null ? null : v.ToString();
        }

        private static int SafeInt(JObject o, string key)
        {
            var v = o[key];
            if (v == null) return 0;
            if (v.Type == JTokenType.Integer) return (int)v;
            int parsed;
            int.TryParse(v.ToString(), out parsed);
            return parsed;
        }

        private static double SafeDouble(JObject o, string key)
        {
            var v = o[key];
            if (v == null) return 0;
            double parsed;
            double.TryParse(v.ToString(), out parsed);
            return parsed;
        }

        /// <summary>
        /// Look up a single JLCPCB part by its LCSC part number (e.g. C2040).
        /// Uses the component-detail endpoint (reliable stockCount), then falls back to search.
        /// </summary>
        public async Task<JlcpcbPart> LookupByLcscAsync(string lcsc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(lcsc))
                return null;

            var normalized = lcsc.Trim().ToUpperInvariant();
            if (!normalized.StartsWith("C"))
                normalized = "C" + normalized;

            var detail = await FetchComponentDetailAsync(normalized, cancellationToken);
            if (detail != null)
                return detail;

            var results = await SearchAsync(keyword: normalized, cancellationToken: cancellationToken);
            if (results == null || results.Count == 0)
                return null;

            var exact = results.FirstOrDefault(p => string.Equals(p.Lcsc, normalized, StringComparison.OrdinalIgnoreCase));
            return exact ?? results[0];
        }

        /// <summary>
        /// JLCPCB detail API — returns stockCount / overseasStockCount / Basic|Extended.
        /// This is the reliable source; the list-search endpoint often returns empty lists.
        /// </summary>
        public async Task<JlcpcbPart> FetchComponentDetailAsync(string lcsc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(lcsc))
                return null;

            var urls = new[]
            {
                "https://cart.jlcpcb.com/shoppingCart/smtGood/getComponentDetail?componentCode=" + Uri.EscapeDataString(lcsc),
                "https://jlcpcb.com/api/overseas-pcb-order/v1/shoppingCart/smtGood/getComponentDetail?componentCode=" + Uri.EscapeDataString(lcsc),
            };

            foreach (var url in urls)
            {
                try
                {
                    using (var response = await _httpClient.GetAsync(url, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                            continue;
                        var text = await response.Content.ReadAsStringAsync();
                        var obj = JObject.Parse(text);
                        var data = obj["data"] as JObject;
                        if (data == null)
                            continue;
                        var mapped = MapRow(data);
                        if (mapped != null && mapped.Stock <= 0)
                        {
                            // Prefer overseas stock when domestic stockCount is missing.
                            var overseas = SafeInt(data, "overseasStockCount");
                            if (overseas > 0)
                                mapped.Stock = overseas;
                        }
                        if (mapped != null && !string.IsNullOrWhiteSpace(mapped.Lcsc))
                            return mapped;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[JLCPCB] Detail {url}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Search by keyword (typically the component value, e.g. "10k 0402", or an LCSC number).
        /// When a desiredPackage is provided, results are ordered so that Basic parts in the
        /// matching package come first, then Basic parts in any package, then Extended parts.
        /// </summary>
        public async Task<List<JlcpcbPart>> SearchAsync(
            string keyword,
            string desiredPackage = null,
            bool basicOnly = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var rows = await FetchPageAsync(keyword, basicOnly: basicOnly, cancellationToken: cancellationToken);
            if (rows == null || rows.Count == 0)
                return new List<JlcpcbPart>();

            var parts = rows.Select(MapRow).Where(p => p != null).ToList();

            if (!string.IsNullOrWhiteSpace(desiredPackage))
            {
                var pkg = desiredPackage.Trim();
                parts = parts
                    .OrderByDescending(p => string.Equals(p.Package, pkg, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    .ThenByDescending(p => p.IsBasic ? 1 : 0)
                    .ThenByDescending(p => p.Stock)
                    .ToList();
            }
            else
            {
                parts = parts
                    .OrderByDescending(p => p.IsBasic ? 1 : 0)
                    .ThenByDescending(p => p.Stock)
                    .ToList();
            }

            return parts;
        }

        /// <summary>
        /// Resolve a part: if it already carries an LCSC number, look it up directly;
        /// otherwise search by keyword (value + desired package) and prefer a Basic match.
        /// </summary>
        public async Task<JlcpcbPart> ResolveAsync(
            string lcsc,
            string keyword,
            string desiredPackage,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(lcsc))
            {
                var byLcsc = await LookupByLcscAsync(lcsc, cancellationToken);
                if (byLcsc != null)
                    return byLcsc;
            }

            if (string.IsNullOrWhiteSpace(keyword))
                return null;

            var results = await SearchAsync(keyword, desiredPackage: desiredPackage, cancellationToken: cancellationToken);
            return results.Count > 0 ? results[0] : null;
        }

        private async Task<List<JObject>> FetchPageAsync(
            string keyword,
            int pageSize = 50,
            bool basicOnly = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new
            {
                currentPage = 1,
                pageSize = pageSize,
                searchType = 2,
                keyword = keyword ?? string.Empty,
                componentLibraryType = basicOnly ? "base" : "all",
                presaleType = "",
                preferredComponentFlag = true,
                stockFlag = false,
                stockSort = (string)null,
                firstSortName = (string)null,
                secondSortName = (string)null,
                componentBrand = (string)null,
                componentSpecification = (string)null,
                componentAttributes = new object[0],
                searchSource = "search",
            };

            string jsonPayload = JsonConvert.SerializeObject(body);
            Debug.WriteLine($"[JLCPCB] POST {SearchUrl} payload={jsonPayload}");
            Console.WriteLine($"[JLCPCB] POST {SearchUrl} payload={jsonPayload}");

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(SearchUrl, content, cancellationToken);
                Debug.WriteLine($"[JLCPCB] Response Status: {(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine($"[JLCPCB] Response Status: {(int)response.StatusCode} {response.StatusCode}");

                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();

                JObject obj = JObject.Parse(result);
                var data = obj["data"];
                if (data == null)
                    return new List<JObject>();

                // Newer responses put rows under componentPageInfo.list; older under componentList.
                var rows = data["componentList"] as JArray;
                if (rows == null || rows.Count == 0)
                    rows = data["componentPageInfo"]?["list"] as JArray;
                return rows?.OfType<JObject>().ToList() ?? new List<JObject>();
            }
            catch (OperationCanceledException)
            {
                return new List<JObject>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[JLCPCB] Error: {ex.Message}");
                Console.WriteLine($"[JLCPCB] Error: {ex.Message}");
                return new List<JObject>();
            }
        }
    }
}
