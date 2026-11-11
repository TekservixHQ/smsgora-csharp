# SMS Gora .NET client

[![NuGet](https://img.shields.io/nuget/v/SmsGora.svg)](https://www.nuget.org/packages/SmsGora)

**SMS Gora SMS Gateway** - official C#/.NET client: send and receive SMS through your own Android phones. By Tekservix.

C# / .NET client for the [SMS Gora SMS Gateway](https://tekservix.com/smsgora/) API.

SMS Gora turns Android phones with ordinary SIM cards into an SMS gateway. Your server calls the API, a linked phone sends the text, and replies and delivery reports come back to you. There's no sender ID registration and no per-message fee from us: your mobile operator bills the texts like any SMS.

- .NET Standard 2.0 and .NET 8: works on .NET Framework 4.6.1+, .NET Core, .NET 5-9
- Async everywhere, `CancellationToken` support, works with `IHttpClientFactory`
- Webhook signature check built in

## Install

```bash
dotnet add package SmsGora
```

## Quick start

Create an API key in the SMS Gora dashboard (SMS Gora -> API keys) and keep it on your server.

```csharp
using Tekservix.SmsGora;

var sms = new SmsGoraClient(Environment.GetEnvironmentVariable("SMSGORA_KEY"));

var result = await sms.SendSmsAsync("+15551234567", "Your verification code is 482913");
Console.WriteLine(result.GetProperty("message_ids")); // [1873]
```

## Sending

```csharp
// Several recipients (up to 1,000), from phone 42, SIM 2, scheduled for later
await sms.SendSmsAsync(
    new[] { "+15551234567", "+447700900123" },
    "Reminder: your appointment is tomorrow at 10:00",
    deviceId: 42,
    simSlot: 1, // 0 = SIM 1, 1 = SIM 2
    scheduleAt: new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));

// Different texts for different people in one call (up to 5,000 recipients)
await sms.SendBulkSmsAsync(42, new[]
{
    new SmsGoraClient.BulkMessage { Recipients = new[] { "+15551234567" }, Message = "Hi Aline, parcel #1042 is ready." },
    new SmsGoraClient.BulkMessage { Recipients = new[] { "+15557654321" } },
}, messageTemplate: "Hello, your parcel is ready for pickup.");
```

## Delivery status and incoming SMS

```csharp
var msg = await sms.GetMessageAsync(1873);
Console.WriteLine(msg.GetProperty("status")); // queued -> dispatched -> sent -> delivered (or failed)

await sms.CancelMessageAsync(1874); // works while queued or scheduled

var inbox = await sms.ListReceivedSmsAsync(filters: new Dictionary<string, object?> { ["since"] = "2026-11-01", ["limit"] = 50 });
var phones = await sms.ListDevicesAsync();
var account = await sms.GetAccountAsync();
```

Anything else in the [API reference](https://tekservix.com/smsgora/docs/) is one call away:

```csharp
var batch = await sms.RequestAsync(HttpMethod.Get, "/batches/311");
```

## ASP.NET Core

Register one client and inject it:

```csharp
builder.Services.AddHttpClient<SmsGoraClient>((http, sp) =>
    new SmsGoraClient(builder.Configuration["SmsGora:ApiKey"], httpClient: http));
```

## Webhooks

Add your HTTPS URL in SMS Gora -> Webhooks (or with `CreateWebhookAsync`) and keep the secret. Each request carries `X-SmsGora-Signature: sha256=...`, an HMAC-SHA256 of the raw body. Minimal API example:

```csharp
app.MapPost("/sms/webhook", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var raw = ms.ToArray();

    if (!SmsGoraClient.VerifyWebhook(raw, req.Headers["X-SmsGora-Signature"], builder.Configuration["SmsGora:WebhookSecret"]))
        return Results.Unauthorized();

    var evt = JsonDocument.Parse(raw).RootElement;
    if (evt.GetProperty("event").GetString() == "sms.received")
        Console.WriteLine($"{evt.GetProperty("data").GetProperty("from")}: {evt.GetProperty("data").GetProperty("message")}");

    return Results.Ok(); // answer within 10 seconds; queue slow work
});
```

## Errors

Failed calls throw `SmsGoraException` with `Status`, `Message`, `Errors` and, on 429, `RetryAfter`.

```csharp
try
{
    await sms.SendSmsAsync("+15551234567", "Hi");
}
catch (SmsGoraException e) when (e.Status == 429)
{
    // rate limit (300 requests/minute per key) or plan limit reached
    await Task.Delay(e.RetryAfter ?? TimeSpan.FromSeconds(5));
}
```

Never ship the key inside a desktop, MAUI or Blazor WebAssembly app: anyone could read it. Call the API from your server.

## Migrating from TextBee

SMS Gora accepts TextBee-style requests, so most integrations switch by changing the base URL and key. See the [compatibility notes](https://tekservix.com/smsgora/docs/).

## More from Tekservix

- [WAservix WhatsApp API Gateway](https://tekservix.com/waservix/): send WhatsApp messages from your own number (`dotnet add package WAservix`)
- [AI Sales Agent](https://tekservix.com/ai-sales-agent): answers customers on WhatsApp and SMS using your website's content
- [Website to App](https://tekservix.com/mobile-apps): turn your website into Android and iOS apps
- SMS Gora is also available for Node.js, Python, PHP, Go and Java/Kotlin: see [github.com/TekservixHQ](https://github.com/TekservixHQ)

## License

MIT
