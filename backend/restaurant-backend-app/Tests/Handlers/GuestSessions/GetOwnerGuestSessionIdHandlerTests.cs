using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class GetOwnerGuestSessionIdHandlerTests
{
    private const string ReservationId = "reservation-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IGuestSessionService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();

    public GetOwnerGuestSessionIdHandlerTests()
    {
        _context.SetupGet(context => context.Logger).Returns(new Mock<ILambdaLogger>().Object);
    }

    private static APIGatewayProxyRequest Request(
        string? reservationId = ReservationId,
        string? userId = OwnerUserId) => new()
    {
        PathParameters = reservationId is null
            ? null
            : new Dictionary<string, string> { ["reservationId"] = reservationId },
        RequestContext = userId is null
            ? null
            : new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["sub"] = userId }
                }
            }
    };

    [Fact]
    public async Task HandleRequest_ReturnsExactCamelCaseOwnerSessionIdResponse()
    {
        _service
            .Setup(service => service.GetOwnerGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ReturnsAsync(new GetOwnerGuestSessionIdResponse("owner-session-123"));

        var response = await new GetOwnerGuestSessionIdHandler(_service.Object)
            .HandleRequest(Request(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(["guestSessionId"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("owner-session-123", json.RootElement.GetProperty("guestSessionId").GetString());
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(OwnerUserId, ReservationId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankReservationIdReturns400WithoutServiceCall(string? reservationId)
    {
        var response = await new GetOwnerGuestSessionIdHandler(_service.Object)
            .HandleRequest(Request(reservationId: reservationId), _context.Object);

        Assert.Equal(400, response.StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankCognitoSubReturns401WithoutServiceCall(string? userId)
    {
        var response = await new GetOwnerGuestSessionIdHandler(_service.Object)
            .HandleRequest(Request(userId: userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(ValidationException), 400)]
    [InlineData(typeof(ActionForbiddenException), 403)]
    [InlineData(typeof(ReservationNotFoundException), 404)]
    [InlineData(typeof(GroupReservationNotFoundException), 404)]
    public async Task HandleRequest_MapsKnownServiceErrors(Type exceptionType, int expectedStatus)
    {
        Exception exception = exceptionType == typeof(ValidationException)
            ? new ValidationException("The reservation does not have an owner guest session.")
            : exceptionType == typeof(ActionForbiddenException)
                ? new ActionForbiddenException("Only the reservation owner can perform this action.")
                : exceptionType == typeof(ReservationNotFoundException)
                    ? new ReservationNotFoundException("Reservation not found.")
                    : new GroupReservationNotFoundException();

        _service
            .Setup(service => service.GetOwnerGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(exception);

        var response = await new GetOwnerGuestSessionIdHandler(_service.Object)
            .HandleRequest(Request(), _context.Object);

        Assert.Equal(expectedStatus, response.StatusCode);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(OwnerUserId, ReservationId), Times.Once);
    }
}
