using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tekservix.SmsGora
{
    /// <summary>Thrown when the SMS Gora API answers with an error.</summary>
    public class SmsGoraException : Exception
    {
        /// <summary>HTTP status code.</summary>
        public int Status { get; }
        /// <summary>Field errors, if any.</summary>
        public JsonElement? Errors { get; }
        /// <summary>Raw response body.</summary>
        public string? Body { get; }
        /// <summary>Time to wait, sent with HTTP 429.</summary>
        public TimeSpan? RetryAfter { get; }

        public SmsGoraException(string message, int status, JsonElement? errors, string? body, TimeSpan? retryAfter)
            : base(message)
        {
            Status = status;
            Errors = errors;
            Body = body;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Client for the SMS Gora SMS Gateway API. Docs: https://tekservix.com/smsgora/docs/
    /// </summary>
    /// <example>
    /// var sms = new SmsGoraClient(Environment.GetEnvironmentVariable("SMSGORA_KEY"));
    /// await sms.SendSmsAsync("+15551234567", "Your verification code is 482913");
    /// </example>
    public class SmsGoraClient
    {
        public const string Version = "1.0.0";
        public const string DefaultBaseUrl = "https://tekservix.com/smsgora/api/v1";

        private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _baseUrl;

        /// <param name="apiKey">Your SMS Gora API key (starts with sgk_). Keep it on your server.</param>
        /// <param name="baseUrl">Optional API base URL.</param>
        /// <param name="httpClient">Optional HttpClient (for example from IHttpClientFactory).</param>
        public SmsGoraClient(string? apiKey, string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("SmsGora: apiKey is required (create one in SMS Gora -> API keys)", nameof(apiKey));
            _apiKey = apiKey!;
            _baseUrl = baseUrl.TrimEnd('/');
            _http = httpClient ?? SharedHttp;
        }

        /// <summary>Call any documented endpoint, e.g. RequestAsync(HttpMethod.Get, "/devices"). Returns the <c>data</c> field.</summary>
        public async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(method, _baseUrl + path);
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "smsgora-dotnet/" + Version);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            var status = (int)res.StatusCode;

            JsonElement? root = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { using var doc = JsonDocument.Parse(text); root = doc.RootElement.Clone(); }
                catch (JsonException) { /* not JSON */ }
            }
            var isObj = root.HasValue && root.Value.ValueKind == JsonValueKind.Object;
            var failed = status >= 400 || (isObj && root!.Value.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False);
            if (failed)
            {
                string message = "HTTP " + status;
                JsonElement? errors = null;
                if (isObj)
                {
                    if (root!.Value.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
                    if (root.Value.TryGetProperty("errors", out var e)) errors = e;
                }
                TimeSpan? retry = null;
                if (res.Headers.RetryAfter?.Delta is TimeSpan delta) retry = delta;
                throw new SmsGoraException(message, status, errors, text, retry);
            }
            if (isObj && root!.Value.TryGetProperty("data", out var data)) return data;
            return root ?? default;
        }

        /// <summary>Send an SMS to one number from your best online phone.</summary>
        public Task<JsonElement> SendSmsAsync(string to, string message, CancellationToken ct = default)
            => SendSmsAsync(new[] { to }, message, null, null, null, ct);

        /// <summary>Send an SMS to up to 1,000 numbers.</summary>
        /// <param name="deviceId">Send from this phone, or null for your best online phone.</param>
        /// <param name="simSlot">0 = SIM 1, 1 = SIM 2, or null.</param>
        /// <param name="scheduleAt">Send later, or null to send now.</param>
        public Task<JsonElement> SendSmsAsync(IEnumerable<string> to, string message, long? deviceId = null, int? simSlot = null,
            DateTimeOffset? scheduleAt = null, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object?> { ["recipients"] = to.ToArray(), ["message"] = message };
            if (simSlot.HasValue) body["sim_slot"] = simSlot.Value;
            if (scheduleAt.HasValue) body["schedule_at"] = scheduleAt.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var path = deviceId.HasValue ? $"/devices/{deviceId.Value}/send-sms" : "/send-sms";
            return RequestAsync(HttpMethod.Post, path, body, ct);
        }

        /// <summary>One entry of <see cref="SendBulkSmsAsync"/>.</summary>
        public class BulkMessage
        {
            [System.Text.Json.Serialization.JsonPropertyName("recipients")]
            public string[] Recipients { get; set; } = Array.Empty<string>();

            [System.Text.Json.Serialization.JsonPropertyName("message")]
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public string? Message { get; set; }
        }

        /// <summary>Different texts for different people in one call (up to 5,000 recipients).</summary>
        public Task<JsonElement> SendBulkSmsAsync(long deviceId, IEnumerable<BulkMessage> messages, string? messageTemplate = null, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object?> { ["messages"] = messages.ToArray() };
            if (messageTemplate != null) body["messageTemplate"] = messageTemplate;
            return RequestAsync(HttpMethod.Post, $"/devices/{deviceId}/send-bulk-sms", body, ct);
        }

        /// <summary>Status is queued, dispatched, sent, delivered, failed or cancelled.</summary>
        public Task<JsonElement> GetMessageAsync(long id, CancellationToken ct = default) => RequestAsync(HttpMethod.Get, $"/messages/{id}", null, ct);

        /// <summary>Cancel a queued or scheduled message.</summary>
        public Task<JsonElement> CancelMessageAsync(long id, CancellationToken ct = default) => RequestAsync(HttpMethod.Post, $"/messages/{id}/cancel", null, ct);

        /// <summary>Filters: direction (in/out), status, device_id, phone, since, after_id, page, limit.</summary>
        public Task<JsonElement> ListMessagesAsync(IDictionary<string, object?>? filters = null, CancellationToken ct = default)
            => RequestAsync(HttpMethod.Get, "/messages" + Query(filters), null, ct);

        /// <summary>Incoming SMS for all phones (deviceId null) or one phone.</summary>
        public Task<JsonElement> ListReceivedSmsAsync(long? deviceId = null, IDictionary<string, object?>? filters = null, CancellationToken ct = default)
        {
            var path = deviceId.HasValue ? $"/devices/{deviceId.Value}/get-received-sms" : "/received-sms";
            return RequestAsync(HttpMethod.Get, path + Query(filters), null, ct);
        }

        public Task<JsonElement> GetBatchAsync(long id, CancellationToken ct = default) => RequestAsync(HttpMethod.Get, $"/batches/{id}", null, ct);

        /// <summary>Your linked Android phones.</summary>
        public Task<JsonElement> ListDevicesAsync(CancellationToken ct = default) => RequestAsync(HttpMethod.Get, "/devices", null, ct);

        public Task<JsonElement> GetDeviceAsync(long id, CancellationToken ct = default) => RequestAsync(HttpMethod.Get, $"/devices/{id}", null, ct);

        /// <summary>Account, plan and usage.</summary>
        public Task<JsonElement> GetAccountAsync(CancellationToken ct = default) => RequestAsync(HttpMethod.Get, "/account", null, ct);

        public Task<JsonElement> ListWebhooksAsync(CancellationToken ct = default) => RequestAsync(HttpMethod.Get, "/webhooks", null, ct);

        /// <summary>Register a webhook. The answer includes the secret (only returned once).</summary>
        public Task<JsonElement> CreateWebhookAsync(string url, IEnumerable<string>? events = null, long? deviceId = null, CancellationToken ct = default)
        {
            var body = new Dictionary<string, object?>
            {
                ["url"] = url,
                ["events"] = (events ?? new[] { "sms.received" }).ToArray(),
                ["device_id"] = deviceId,
            };
            return RequestAsync(HttpMethod.Post, "/webhooks", body, ct);
        }

        public Task<JsonElement> DeleteWebhookAsync(long id, CancellationToken ct = default) => RequestAsync(HttpMethod.Delete, $"/webhooks/{id}", null, ct);

        /// <summary>Check the X-SmsGora-Signature header (sha256=&lt;hex HMAC-SHA256 of the raw body&gt;).</summary>
        /// <param name="rawBody">The raw request body, before JSON parsing.</param>
        public static bool VerifyWebhook(byte[]? rawBody, string? signature, string? secret)
        {
            if (rawBody == null || rawBody.Length == 0 || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
            var hash = hmac.ComputeHash(rawBody);
            var expected = "sha256=" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature!));
        }

        /// <summary>Same as the byte[] overload, for a body you already read as a string.</summary>
        public static bool VerifyWebhook(string? rawBody, string? signature, string? secret)
            => rawBody != null && VerifyWebhook(Encoding.UTF8.GetBytes(rawBody), signature, secret);

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string Query(IDictionary<string, object?>? p)
        {
            if (p == null) return "";
            var parts = p.Where(kv => kv.Value != null && kv.Value.ToString() != "")
                .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(Convert.ToString(kv.Value, CultureInfo.InvariantCulture)!))
                .ToArray();
            return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
        }
    }
}
