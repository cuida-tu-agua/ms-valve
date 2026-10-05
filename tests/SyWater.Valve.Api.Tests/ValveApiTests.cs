using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SyWater.Valve.Application.Events;
using SyWater.Valve.Application.Ports.In;

namespace SyWater.Valve.Api.Tests;

public class ValveApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _place = Guid.NewGuid();
    private readonly Guid _device = Guid.NewGuid();

    public ValveApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.CreateSchema();
        _factory.Places.Mine.Add(_place);
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.TokenFor(_user));
        return client;
    }

    private async Task Handle(Func<IDeviceEventsHandler, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>());
    }

    private Task LinkAndReport(string state) => Handle(async h =>
    {
        await h.HandleAsync(new DeviceLinkedEvent(_device, "SW-ESP32-000001", _place, _user, DateTime.UtcNow), default);
        await h.HandleAsync(new ValveReportedEvent(_device, _place, state, null, DateTime.UtcNow), default);
    });

    private static async Task<string?> Title(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    [Fact]
    public async Task Full_close_flow_202_then_device_confirms_and_history_shows_it()
    {
        await LinkAndReport("OPEN");
        var client = Client();

        var valve = await client.GetFromJsonAsync<JsonElement>($"/api/places/{_place}/valve");
        Assert.Equal("OPEN", valve.GetProperty("state").GetString());
        Assert.Equal("OK", valve.GetProperty("communication").GetString());

        var close = await client.PostAsJsonAsync($"/api/places/{_place}/valve/close", new { code = "123456" });
        Assert.Equal(HttpStatusCode.Accepted, close.StatusCode);
        var command = await close.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SENT", command.GetProperty("status").GetString());
        Assert.Equal("CLOSE", command.GetProperty("action").GetString());
        Assert.Equal("Juan Ome", command.GetProperty("requestedByName").GetString());
        var id = command.GetProperty("id").GetGuid();
        Assert.Contains(_factory.Devices.Sent, s => s.CommandId == id);
        Assert.EndsWith($"/valve/commands/{id}", close.Headers.Location!.ToString());

        // a second order while the first one waits → 409
        var again = await client.PostAsync($"/api/places/{_place}/valve/open", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("valve.command_in_progress", await Title(again));

        await Handle(h => h.HandleAsync(new ValveReportedEvent(_device, _place, "CLOSED", id, DateTime.UtcNow.AddSeconds(1)), default));

        var done = await client.GetFromJsonAsync<JsonElement>($"/api/places/{_place}/valve/commands/{id}");
        Assert.Equal("ACK_SUCCESS", done.GetProperty("status").GetString());
        valve = await client.GetFromJsonAsync<JsonElement>($"/api/places/{_place}/valve");
        Assert.Equal("CLOSED", valve.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, valve.GetProperty("pendingCommand").ValueKind);

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/places/{_place}/valve/commands");
        Assert.Equal(id, history[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Close_errors_have_stable_codes()
    {
        await LinkAndReport("OPEN");
        var client = Client();

        var noCode = await client.PostAsJsonAsync($"/api/places/{_place}/valve/close", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        Assert.Equal("valve.code_required", await Title(noCode));

        var wrong = await client.PostAsJsonAsync($"/api/places/{_place}/valve/close", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auth.invalid_code", problem.GetProperty("title").GetString());
        Assert.Equal(4, problem.GetProperty("remainingAttempts").GetInt32());

        var already = await client.PostAsync($"/api/places/{_place}/valve/open", null);
        Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
        Assert.Equal("valve.already_in_state", await Title(already));

        var other = await client.GetAsync($"/api/places/{Guid.NewGuid()}/valve");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("place.not_found", await Title(other));
    }

    [Fact]
    public async Task Offline_device_is_409_and_the_order_stays_in_history_as_failed()
    {
        await LinkAndReport("CLOSED");
        _factory.Devices.Offline = true;
        try
        {
            var r = await Client().PostAsync($"/api/places/{_place}/valve/open", null);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal("valve.device_offline", await Title(r));
        }
        finally { _factory.Devices.Offline = false; }

        var history = await Client().GetFromJsonAsync<JsonElement>($"/api/places/{_place}/valve/commands");
        Assert.Equal("FAILED", history[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Without_device_there_is_no_valve_and_without_token_401()
    {
        var r = await Client().GetAsync($"/api/places/{_place}/valve");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("valve.not_found", await Title(r));

        var anonymous = await _factory.CreateClient().GetAsync($"/api/places/{_place}/valve");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
