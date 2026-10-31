using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Handlers;

public class GetOwnerGuestSessionIdHandlerTests
{
    private readonly Mock<IGuestSessionService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetOwnerGuestSessionIdHandlerTests() =>
        _context.Setup(context => context.Logger).Returns(_logger.Object);

    [Fact]
    public async Task HandleRequest_ReturnsExactJsonContractAndForwardsAuthenticatedOwner()
    {
        _service
            .Setup(service => service.GetOwnerGuestSessionIdAsync("owner-1", "reservation-1"))
            .ReturnsAsync("owner-session-123");

        var response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"guestSessionId\":\"owner-session-123\"}", response.Body);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync("owner-1", "reservation-1"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleRequest_MissingOrBlankIdentityReturnsUnauthorizedWithoutServiceCall(string? userId)
    {
        var response = await Handler().HandleRequest(Request(userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_MissingRequestContextReturnsUnauthorizedWithoutServiceCall()
    {
        var request = Request("owner-1");
        request.RequestContext = null!;

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_MissingClaimsReturnsUnauthorizedWithoutServiceCall()
    {
        var request = Request("owner-1");
        request.RequestContext.Authorizer.Claims = null;

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_NullSubReturnsUnauthorizedWithoutServiceCall()
    {
        var request = Request("owner-1");
        request.RequestContext.Authorizer.Claims["sub"] = null!;

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_NonOwnerReturnsForbiddenWithoutLeakingSessionId()
    {
        _service
            .Setup(service => service.GetOwnerGuestSessionIdAsync("other-user", "reservation-1"))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can access the owner guest session."));

        var response = await Handler().HandleRequest(Request("other-user"), _context.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.DoesNotContain("guestSessionId", response.Body);
    }

    public static TheoryData<Exception> NotFoundExceptions => new()
    {
        new ReservationNotFoundException("Reservation not found."),
        new GroupReservationNotFoundException(),
        new GuestSessionNotFoundException("Owner guest session was not found.")
    };

    [Theory]
    [MemberData(nameof(NotFoundExceptions))]
    public async Task HandleRequest_MissingReservationGroupOrSessionReturnsNotFound(Exception exception)
    {
        _service
            .Setup(service => service.GetOwnerGuestSessionIdAsync("owner-1", "reservation-1"))
            .ThrowsAsync(exception);

        var response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.DoesNotContain("guestSessionId", response.Body);
    }

    private GetOwnerGuestSessionIdHandler Handler() => new(_service.Object);

    private static APIGatewayProxyRequest Request(string? userId) => new()
    {
        PathParameters = new Dictionary<string, string>
        {
            ["reservationId"] = "reservation-1"
        },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = userId is null
                    ? []
                    : new Dictionary<string, string> { ["sub"] = userId }
            }
        }
    };
}
