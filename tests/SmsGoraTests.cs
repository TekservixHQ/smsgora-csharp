using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tekservix.SmsGora;
using Xunit;

public class FakeHandler : HttpMessageHandler
{
    public HttpRequestMessage? Last;
    public JsonElement? LastBody;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Reply = "{\"success\":true,\"data\":{\"message_ids\":[1873]}}";
    public TimeSpan? RetryAfter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Last = request;
        LastBody = null;
        if (request.Content != null)
        {
            var s = await request.Content.ReadAsStringAsync();
            LastBody = JsonDocument.Parse(s).RootElement.Clone();
        }
        var res = new HttpResponseMessage(Status) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
        if (RetryAfter.HasValue) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter.Value);
        return res;
    }
}

public class SmsGoraTests
{
    private static (SmsGoraClient, FakeHandler) Make()
    {
        var h = new FakeHandler();
        return (new SmsGoraClient("sgk_test", "https://api.test/", new HttpClient(h)), h);
    }

    [Fact]
    public async Task SendSms()
    {
        var (c, h) = Make();
        var res = await c.SendSmsAsync("+1555", "hi");
        Assert.Equal(1873, res.GetProperty("message_ids")[0].GetInt32());
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        Assert.Equal("https://api.test/send-sms", h.Last.RequestUri!.ToString());
        Assert.Equal("sgk_test", string.Join("", h.Last.Headers.GetValues("x-api-key")));
        Assert.Equal("+1555", h.LastBody!.Value.GetProperty("recipients")[0].GetString());
        Assert.Equal("hi", h.LastBody.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendFromDevice()
    {
        var (c, h) = Make();
        await c.SendSmsAsync(new[] { "+1", "+2" }, "x", 42, 1, new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
        Assert.Equal("https://api.test/devices/42/send-sms", h.Last!.RequestUri!.ToString());
        Assert.Equal(1, h.LastBody!.Value.GetProperty("sim_slot").GetInt32());
        Assert.Equal("2026-12-01T08:00:00Z", h.LastBody.Value.GetProperty("schedule_at").GetString());
    }

    [Fact]
    public async Task PathsAndQueries()
    {
        var (c, h) = Make();
        await c.ListReceivedSmsAsync(3, new Dictionary<string, object?> { ["limit"] = 5, ["since"] = null });
        Assert.Equal("https://api.test/devices/3/get-received-sms?limit=5", h.Last!.RequestUri!.ToString());
        await c.ListMessagesAsync(new Dictionary<string, object?> { ["status"] = "sent" });
        Assert.Equal("https://api.test/messages?status=sent", h.Last.RequestUri!.ToString());
        await c.DeleteWebhookAsync(9);
        Assert.Equal(HttpMethod.Delete, h.Last.Method);
        await c.SendBulkSmsAsync(42, new[] { new SmsGoraClient.BulkMessage { Recipients = new[] { "+1" } } }, "t");
        Assert.Equal("t", h.LastBody!.Value.GetProperty("messageTemplate").GetString());
        Assert.False(h.LastBody.Value.GetProperty("messages")[0].TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Errors()
    {
        var (c, h) = Make();
        h.Status = (HttpStatusCode)429;
        h.RetryAfter = TimeSpan.FromSeconds(7);
        h.Reply = "{\"success\":false,\"message\":\"Too many requests\"}";
        var e = await Assert.ThrowsAsync<SmsGoraException>(() => c.GetAccountAsync());
        Assert.Equal(429, e.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
        Assert.Equal("Too many requests", e.Message);
    }

    [Fact]
    public void RequiresKey() => Assert.Throws<ArgumentException>(() => new SmsGoraClient(""));

    [Fact]
    public void Webhook()
    {
        var raw = "{\"event\":\"sms.received\"}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("s3cret"));
        var sig = "sha256=" + BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").ToLowerInvariant();
        Assert.True(SmsGoraClient.VerifyWebhook(raw, sig, "s3cret"));
        Assert.False(SmsGoraClient.VerifyWebhook(raw, sig, "nope"));
        Assert.False(SmsGoraClient.VerifyWebhook(raw, null, "s3cret"));
    }
}
