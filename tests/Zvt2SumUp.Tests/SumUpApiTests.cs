using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zvt2SumUp.Core;
using Zvt2SumUp.SumUp;

namespace Zvt2SumUp.Tests;

public sealed class SumUpApiTests
{
    [Fact]
    public async Task ReaderCheckoutUsesMinorUnitsAndAffiliateContract()
    {
        RecordingHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v0.1/merchants/M1/readers/rdr_test/checkout", request.RequestUri!.AbsolutePath);
            string body = request.Content!.ReadAsStringAsync().Result; using JsonDocument json = JsonDocument.Parse(body);
            Assert.Equal(1234, json.RootElement.GetProperty("total_amount").GetProperty("value").GetInt64()); Assert.Equal(2, json.RootElement.GetProperty("total_amount").GetProperty("minor_unit").GetInt32());
            Assert.Equal("app", json.RootElement.GetProperty("affiliate").GetProperty("app_id").GetString());
            Assert.Equal("key", json.RootElement.GetProperty("affiliate").GetProperty("key").GetString());
            Assert.Equal("ref", json.RootElement.GetProperty("affiliate").GetProperty("foreign_transaction_id").GetString());
            return Json(HttpStatusCode.Created, "{\"data\":{\"client_transaction_id\":\"client-1\"}}");
        });
        SumUpApiClient client = Client(handler, new GatewayOptions { MerchantCode = "M1", TerminalId = "rdr_test" }, new("token", "key", "app"));
        CheckoutResult result = await client.CreateCheckoutAsync(new(1234, "EUR", "Test", "ref"), CancellationToken.None); Assert.Equal("client-1", result.Id); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransactionLookupUsesCurrentV21Endpoint()
    {
        RecordingHandler handler = new(request =>
        {
            Assert.Equal("/v2.1/merchants/M1/transactions", request.RequestUri!.AbsolutePath); Assert.Contains("client_transaction_id=client-1", request.RequestUri.Query);
            return Json(HttpStatusCode.OK, "{\"id\":\"tx-1\",\"status\":\"SUCCESSFUL\",\"currency\":\"EUR\",\"card_type\":\"VISA\"}");
        });
        SumUpApiClient client = Client(handler, new GatewayOptions { MerchantCode = "M1", TerminalId = "rdr_test" }, new("token"));
        CheckoutResult result = await client.WaitForPaymentAsync("client-1", TimeSpan.FromSeconds(2), CancellationToken.None); Assert.Equal("PAID", result.Status); Assert.Equal("tx-1", result.TransactionId);
    }

    [Fact]
    public async Task ReaderTransactionLookupUnwrapsItemsResponseAndParsesAmount()
    {
        RecordingHandler handler = new(_ => Json(HttpStatusCode.OK,
            "{\"items\":[{\"id\":\"tx-2\",\"client_transaction_id\":\"client-2\",\"status\":\"SUCCESSFUL\",\"amount\":\"0.01\",\"currency\":\"EUR\"}]}"));
        SumUpApiClient client = Client(handler, new GatewayOptions { MerchantCode = "M1", TerminalId = "rdr_test" }, new("token"));

        CheckoutResult result = await client.WaitForPaymentAsync("client-2", TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal("PAID", result.Status);
        Assert.Equal("tx-2", result.TransactionId);
        Assert.Equal(1, result.AmountCents);
    }

    [Fact]
    public async Task RefundUsesCurrentEndpointAndNeverRetriesPost()
    {
        RecordingHandler handler = new(_ => Json(HttpStatusCode.InternalServerError, "{\"message\":\"failed\"}"));
        SumUpApiClient client = Client(handler, new GatewayOptions { MerchantCode = "M1" }, new("token"));
        await Assert.ThrowsAsync<SumUpApiException>(() => client.RefundAsync("tx", 50, CancellationToken.None)); Assert.Single(handler.Requests);
        Assert.Equal("/v1.0/merchants/M1/payments/tx/refunds", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PairingCodeIsValidatedBeforeNetwork()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException()); SumUpApiClient client = Client(handler, new GatewayOptions { MerchantCode = "M1" }, new("token"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PairReaderAsync("short", "test", CancellationToken.None)); Assert.Empty(handler.Requests);
    }

    private static SumUpApiClient Client(HttpMessageHandler handler, GatewayOptions options, GatewaySecrets secrets) =>
        new(new HttpClient(handler) { BaseAddress = SumUpApiClient.DefaultBaseAddress }, secrets, options, NullLogger<SumUpApiClient>.Instance);
    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(response(request)); }
    }
}
