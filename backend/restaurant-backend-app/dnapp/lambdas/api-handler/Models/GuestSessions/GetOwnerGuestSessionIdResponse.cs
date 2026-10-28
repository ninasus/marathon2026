using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GetOwnerGuestSessionIdResponse(
    [property: JsonPropertyName("guestSessionId")] string GuestSessionId);
