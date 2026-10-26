using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using cms_api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace cms_api.Controllers
{
    [ApiController]
    [Route("AiWebChat")]
    [Route("ai-web-chat-api")]
    [Produces("application/json")]
    public class AiWebChatController : ControllerBase
    {
        private const string DefaultBaseUrl = "https://h-rag.huakati.online";
        private const string DefaultTenantId = "198f9720770b40f6815175096e698dcb";
        private const string DefaultMode = "mix";
        private const int DefaultTopK = 20;
        private const int DefaultChunkTopK = 10;

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AiWebChatController> _logger;

        public AiWebChatController(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<AiWebChatController> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
            _logger = logger;
            _httpClient.Timeout = TimeSpan.FromSeconds(GetConfiguredInt("HRAG_TIMEOUT_SECONDS", "HRag:TimeoutSeconds", 90));
        }

        [HttpPost]
        [HttpPost("chat")]
        [ProducesResponseType(typeof(AiWebChatResponse), 200)]
        public async Task<ActionResult<AiWebChatResponse>> Chat(
            [FromBody] AiWebChatRequest value,
            CancellationToken cancellationToken)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.message))
            {
                return BadRequest(new AiWebChatResponse
                {
                    status = "E",
                    message = "กรุณาระบุคำถาม"
                });
            }

            var apiKey = GetSetting("HRAG_API_KEY", "HRag:ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return StatusCode(500, new AiWebChatResponse
                {
                    status = "E",
                    message = "กรุณาตั้งค่า HRAG_API_KEY ใน environment หรือ HRag:ApiKey ใน appsettings"
                });
            }

            var baseUrl = GetSetting("HRAG_BASE_URL", "HRag:BaseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = DefaultBaseUrl;
            }

            var tenantId = GetSetting("HRAG_TENANT_ID", "HRag:TenantId");
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = DefaultTenantId;
            }

            var endpoint = $"{baseUrl.TrimEnd('/')}/tenants/{tenantId}/query";
            var requestBody = BuildHRagRequest(value);
            var requestJson = JsonSerializer.Serialize(requestBody);

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Add("X-API-Key", apiKey);
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                try
                {
                    using (var response = await _httpClient.SendAsync(request, cancellationToken))
                    {
                        var responseJson = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning(
                                "H-RAG API error {StatusCode}: {Body}",
                                (int)response.StatusCode,
                                responseJson);

                            return StatusCode(502, new AiWebChatResponse
                            {
                                status = "E",
                                message = ExtractHRagError(responseJson)
                            });
                        }

                        return new AiWebChatResponse
                        {
                            status = "S",
                            message = "success",
                            answer = ExtractString(responseJson, "answer") ?? "ระบบไม่ได้รับข้อความคำตอบจากคลังความรู้",
                            model = "H-RAG",
                            sources = ExtractSources(responseJson)
                        };
                    }
                }
                catch (TaskCanceledException exception)
                {
                    _logger.LogWarning(exception, "H-RAG API request timed out");

                    return StatusCode(504, new AiWebChatResponse
                    {
                        status = "E",
                        message = "ระบบคลังความรู้ใช้เวลาตอบนานเกินไป กรุณาลองใหม่อีกครั้ง"
                    });
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogWarning(exception, "H-RAG API request failed");

                    return StatusCode(502, new AiWebChatResponse
                    {
                        status = "E",
                        message = "ไม่สามารถเชื่อมต่อระบบคลังความรู้ได้"
                    });
                }
            }
        }

        [HttpGet("reference")]
        [ProducesResponseType(200)]
        public async Task<IActionResult> Reference(
            [FromQuery] string docId,
            [FromQuery] string referenceId,
            [FromQuery] string url,
            [FromQuery] string filePath,
            [FromQuery] string tenantId,
            CancellationToken cancellationToken)
        {
            var apiKey = GetSetting("HRAG_API_KEY", "HRag:ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return StatusCode(500, "กรุณาตั้งค่า HRAG_API_KEY");
            }

            var baseUrl = GetSetting("HRAG_BASE_URL", "HRag:BaseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = DefaultBaseUrl;
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = GetSetting("HRAG_TENANT_ID", "HRag:TenantId");
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = DefaultTenantId;
            }

            var endpoint = await BuildReferenceEndpoint(
                baseUrl,
                tenantId,
                docId,
                referenceId,
                url,
                filePath,
                apiKey,
                cancellationToken
            );
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return BadRequest("ไม่พบรหัสเอกสารอ้างอิง");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.Add("X-API-Key", apiKey);

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning(
                            "H-RAG reference download error {StatusCode}: {Body}",
                            (int)response.StatusCode,
                            error);

                        return StatusCode(502, "ไม่สามารถเปิดเอกสารอ้างอิงได้");
                    }

                    var fallbackFileName = $"{(docId ?? referenceId ?? "reference").Trim()}.pdf";
                    if (!IsJsonContent(response.Content.Headers.ContentType?.MediaType))
                    {
                        return await CreateInlineFileResponse(response, fallbackFileName);
                    }

                    var metadataJson = await response.Content.ReadAsStringAsync();
                    var signedUrl = ExtractString(metadataJson, "url");
                    var metadataFileName = ExtractString(metadataJson, "filename") ?? fallbackFileName;
                    var signedEndpoint = await BuildReferenceEndpoint(
                        baseUrl,
                        tenantId,
                        docId: null,
                        referenceId: null,
                        url: signedUrl,
                        filePath: null,
                        apiKey: apiKey,
                        cancellationToken: cancellationToken
                    );

                    if (string.IsNullOrWhiteSpace(signedEndpoint))
                    {
                        _logger.LogWarning("H-RAG reference metadata did not contain a valid signed URL");
                        return StatusCode(502, "ไม่พบลิงก์สำหรับเปิดเอกสารอ้างอิง");
                    }

                    using (var downloadRequest = new HttpRequestMessage(HttpMethod.Get, signedEndpoint))
                    using (var downloadResponse = await _httpClient.SendAsync(
                        downloadRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken))
                    {
                        if (!downloadResponse.IsSuccessStatusCode)
                        {
                            _logger.LogWarning(
                                "H-RAG signed reference download error {StatusCode}",
                                (int)downloadResponse.StatusCode);

                            return StatusCode(502, "ไม่สามารถดาวน์โหลดเอกสารอ้างอิงได้");
                        }

                        return await CreateInlineFileResponse(downloadResponse, metadataFileName);
                    }
                }
            }
        }

        [HttpGet("reference/{docId}")]
        [ProducesResponseType(200)]
        public Task<IActionResult> ReferenceByDocumentId(
            [FromRoute] string docId,
            [FromQuery] string tenantId,
            CancellationToken cancellationToken)
        {
            return Reference(
                docId: docId,
                referenceId: null,
                url: null,
                filePath: null,
                tenantId: tenantId,
                cancellationToken: cancellationToken
            );
        }

        private static bool IsJsonContent(string mediaType)
        {
            return !string.IsNullOrWhiteSpace(mediaType) &&
                (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<IActionResult> CreateInlineFileResponse(
            HttpResponseMessage response,
            string fallbackFileName)
        {
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName ??
                fallbackFileName;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = ResolvePreviewContentType(
                response.Content.Headers.ContentType?.MediaType,
                fileName
            );

            Response.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("inline")
            {
                FileNameStar = fileName.Trim('"')
            }.ToString();

            return File(bytes, contentType);
        }

        private static string ResolvePreviewContentType(string contentType, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(contentType) &&
                !string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                return contentType;
            }

            return fileName != null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : "application/octet-stream";
        }

        private async Task<string> BuildReferenceEndpoint(
            string baseUrl,
            string tenantId,
            string docId,
            string referenceId,
            string url,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var normalizedBaseUrl = baseUrl.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(url))
            {
                var trimmedUrl = url.Trim();

                if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absoluteUri))
                {
                    if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var configuredBaseUri))
                    {
                        return null;
                    }

                    var isConfiguredHost = string.Equals(
                        absoluteUri.Host,
                        configuredBaseUri.Host,
                        StringComparison.OrdinalIgnoreCase
                    );
                    var isHRagHost = string.Equals(
                        absoluteUri.Host,
                        "h-rag.huakati.online",
                        StringComparison.OrdinalIgnoreCase
                    );
                    var isSecureHttp = string.Equals(
                        absoluteUri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase
                    );

                    return isSecureHttp && (isConfiguredHost || isHRagHost)
                        ? absoluteUri.ToString()
                        : null;
                }

                return $"{normalizedBaseUrl}/{trimmedUrl.TrimStart('/')}";
            }

            var id = !string.IsNullOrWhiteSpace(docId) ? docId.Trim() : referenceId?.Trim();
            if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(filePath))
            {
                id = await FindDocumentIdByFilePath(
                    normalizedBaseUrl,
                    tenantId,
                    filePath,
                    apiKey,
                    cancellationToken
                );
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return $"{normalizedBaseUrl}/tenants/{tenantId}/documents/{id}/download";
        }

        private async Task<string> FindDocumentIdByFilePath(
            string baseUrl,
            string tenantId,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var endpoint = $"{baseUrl}/tenants/{tenantId}/documents";

            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.Add("X-API-Key", apiKey);

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var responseJson = await response.Content.ReadAsStringAsync();
                    return ExtractDocumentIdByFilePath(responseJson, filePath);
                }
            }
        }

        private static string ExtractDocumentIdByFilePath(string responseJson, string filePath)
        {
            try
            {
                using (var document = JsonDocument.Parse(responseJson))
                {
                    var root = document.RootElement;
                    var documents = root.ValueKind == JsonValueKind.Array
                        ? root
                        : root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                            ? items
                            : default;

                    if (documents.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    foreach (var item in documents.EnumerateArray())
                    {
                        var currentFilePath = GetJsonString(item, "file_path") ??
                            GetJsonString(item, "filePath") ??
                            GetJsonString(item, "filename") ??
                            GetJsonString(item, "name");

                        if (!string.Equals(currentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        return GetJsonString(item, "doc_id") ??
                            GetJsonString(item, "docId") ??
                            GetJsonString(item, "id");
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        private object BuildHRagRequest(AiWebChatRequest value)
        {
            var mode = GetSetting("HRAG_MODE", "HRag:Mode");
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = DefaultMode;
            }

            return new
            {
                question = BuildQuestion(value),
                mode = mode,
                top_k = GetConfiguredInt("HRAG_TOP_K", "HRag:TopK", DefaultTopK),
                chunk_top_k = GetConfiguredInt("HRAG_CHUNK_TOP_K", "HRag:ChunkTopK", DefaultChunkTopK),
                enable_rerank = GetConfiguredBool("HRAG_ENABLE_RERANK", "HRag:EnableRerank", true)
            };
        }

        private static string BuildQuestion(AiWebChatRequest value)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(value.knowledgeGroup))
            {
                parts.Add($"หมวดองค์ความรู้: {value.knowledgeGroup.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(value.province) &&
                !string.Equals(value.province, "ไม่ระบุพื้นที่", StringComparison.OrdinalIgnoreCase))
            {
                var area = value.province.Trim();
                if (!string.IsNullOrWhiteSpace(value.district))
                {
                    area = $"{area} / {value.district.Trim()}";
                }

                parts.Add($"พื้นที่อ้างอิง: {area}");
            }

            parts.Add($"คำถาม: {value.message.Trim()}");
            parts.Add("โปรดตอบเป็นภาษาไทย กระชับ ใช้ข้อมูลจากคลังความรู้ และหากข้อมูลไม่พอให้บอกว่าควรตรวจสอบเอกสารต้นทางเพิ่มเติม");

            return string.Join("\n", parts);
        }

        private string GetSetting(string environmentKey, string configurationKey)
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }

            return _configuration[configurationKey];
        }

        private int GetConfiguredInt(string environmentKey, string configurationKey, int defaultValue)
        {
            var configuredValue = GetSetting(environmentKey, configurationKey);
            if (int.TryParse(configuredValue, out var value) && value > 0)
            {
                return value;
            }

            return defaultValue;
        }

        private bool GetConfiguredBool(string environmentKey, string configurationKey, bool defaultValue)
        {
            var configuredValue = GetSetting(environmentKey, configurationKey);
            if (bool.TryParse(configuredValue, out var value))
            {
                return value;
            }

            return defaultValue;
        }

        private static string ExtractString(string responseJson, string propertyName)
        {
            try
            {
                using (var document = JsonDocument.Parse(responseJson))
                {
                    if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                        property.ValueKind == JsonValueKind.String)
                    {
                        return property.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        private static List<AiWebChatSource> ExtractSources(string responseJson)
        {
            var sources = new List<AiWebChatSource>();

            try
            {
                using (var document = JsonDocument.Parse(responseJson))
                {
                    AddSourceItems(document.RootElement, "citations", sources);
                    AddSourceItems(document.RootElement, "chunks", sources);
                }
            }
            catch (JsonException)
            {
                return sources;
            }

            return sources
                .Where((source) => !string.IsNullOrWhiteSpace(source?.label))
                .GroupBy(
                    (source) => source.docId ?? source.referenceId ?? source.url ?? source.label,
                    StringComparer.OrdinalIgnoreCase
                )
                .Select((group) => group.First())
                .Take(5)
                .ToList();
        }

        private static void AddSourceItems(JsonElement root, string propertyName, List<AiWebChatSource> sources)
        {
            if (!root.TryGetProperty(propertyName, out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in items.EnumerateArray())
            {
                var label = GetJsonString(item, "citation_label");
                var filePath = GetJsonString(item, "file_path");
                var referenceId = GetJsonString(item, "reference_id");
                var docId = GetJsonString(item, "doc_id");
                var downloadUrl = GetJsonString(item, "download_url");

                sources.Add(new AiWebChatSource
                {
                    label = !string.IsNullOrWhiteSpace(label)
                        ? label.Trim()
                        : !string.IsNullOrWhiteSpace(filePath)
                            ? filePath.Trim()
                            : referenceId,
                    url = downloadUrl,
                    docId = docId,
                    referenceId = referenceId,
                    filePath = filePath
                });
            }
        }

        private static string GetJsonString(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            return null;
        }

        private static string ExtractHRagError(string responseJson)
        {
            try
            {
                using (var document = JsonDocument.Parse(responseJson))
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("detail", out var detail))
                    {
                        if (detail.ValueKind == JsonValueKind.String)
                        {
                            return detail.GetString();
                        }

                        return detail.ToString();
                    }

                    if (root.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                return "ไม่สามารถเชื่อมต่อระบบคลังความรู้ได้";
            }

            return "ไม่สามารถเชื่อมต่อระบบคลังความรู้ได้";
        }
    }
}
