using APIHandler.Models.GuestSessions;
using System.Text.Json;

namespace APIHandler.Tests.Models.GuestSessions;

public class GetOwnerGuestSessionIdResponseTests
{
    [Fact]
    public void Serialize_UsesGuestSessionIdContractNameWithDefaultOptions()
    {
        var response = new GetOwnerGuestSessionIdResponse("owner-session-123");

        string json = JsonSerializer.Serialize(response);

        Assert.Equal("{\"guestSessionId\":\"owner-session-123\"}", json);
    }
}
