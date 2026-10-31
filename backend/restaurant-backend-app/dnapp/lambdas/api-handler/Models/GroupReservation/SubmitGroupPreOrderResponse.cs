using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public record SubmitGroupPreOrderResponse(
    [property: JsonPropertyName("dishId")] string DishId,
    [property: JsonPropertyName("quantity")] int Quantity);
