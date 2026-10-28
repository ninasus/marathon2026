using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GetGroupSessionsResponse(
    IEnumerable<GroupSessionParticipant> Participants,
    decimal Total);

public record GroupSessionParticipant(
    string? Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GroupSessionParticipantType>))]
    GroupSessionParticipantType Type,
    string Name,
    IEnumerable<GroupSessionItem> Items,
    decimal Total,
    bool IsSubmitted);

public record GroupSessionItem(GroupSessionDish Dish, int Quantity);

public record GroupSessionDish(string Id, string Name, decimal Price);

public enum GroupSessionParticipantType
{
    Owner,
    Guest
}
