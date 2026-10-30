using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public class GetGroupSessionsServiceTests
{
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";
    private const string OwnerId = "owner-1";

    private readonly Mock<IGroupReservationRepository> _groups = new();
    private readonly Mock<IGuestSessionRepository> _sessions = new();
    private readonly Mock<IDishRepository> _dishes = new();
    private readonly Mock<IReservationRepository> _reservations = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly GuestSessionService _service;

    public GetGroupSessionsServiceTests()
    {
        _service = new GuestSessionService(
            _groups.Object,
            _sessions.Object,
            _dishes.Object,
            _reservations.Object,
            _users.Object,
            Mock.Of<ITableRepository>());

        _groups.Setup(repository => repository.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = GroupId,
                ReservationId = ReservationId,
                ReservationToken = "token"
            });
        _reservations.Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-1",
                WaiterId = "waiter-1",
                CustomerId = OwnerId
            });
    }

    [Fact]
    public async Task GetAllSessionsAsync_MapsStoredSessionIdsAndCalculatesAllTotals()
    {
        var ownerSession = Session("owner-session", "Owner", true, true,
            new OrderItem { DishId = "salad", Quantity = 2 });
        var guestSession = Session("guest-session", "Olena", false, false,
            new OrderItem { DishId = "soup", Quantity = 1 });
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([ownerSession, guestSession]);
        _dishes.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([
                new Dish { Id = "salad", Name = "Caesar Salad", Price = 180m },
                new Dish { Id = "soup", Name = "Tom Yum", Price = 220m }
            ]);
        _users.Setup(repository => repository.GetUserByIdAsync(OwnerId))
            .ReturnsAsync(new UserType
            {
                Attributes = [new AttributeType { Name = "given_name", Value = "Andrii" }]
            });

        var response = await _service.GetAllSessionsAsync(OwnerId, GroupId);

        var participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal("owner-session", participants[0].Id);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal("Andrii", participants[0].Name);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(360m, participants[0].Total);
        var ownerItem = Assert.Single(participants[0].Items);
        Assert.Equal("salad", ownerItem.Dish.Id);
        Assert.Equal("Caesar Salad", ownerItem.Dish.Name);
        Assert.Equal(180m, ownerItem.Dish.Price);
        Assert.Equal(2, ownerItem.Quantity);

        Assert.Equal("guest-session", participants[1].Id);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal("Olena", participants[1].Name);
        Assert.False(participants[1].IsSubmitted);
        Assert.Equal(220m, participants[1].Total);
        var guestItem = Assert.Single(participants[1].Items);
        Assert.Equal("soup", guestItem.Dish.Id);
        Assert.Equal("Tom Yum", guestItem.Dish.Name);
        Assert.Equal(220m, guestItem.Dish.Price);
        Assert.Equal(1, guestItem.Quantity);
        Assert.Equal(580m, response.Total);
        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(GroupId), Times.Once);
        _dishes.Verify(repository => repository.GetDishesByIdsAsync(
            It.Is<IEnumerable<string>>(ids => ids.Count() == 2 && ids.Contains("salad") && ids.Contains("soup"))), Times.Once);
        _sessions.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSessionsAsync_EmptyGroupReturnsZeroWithoutCatalogLookup()
    {
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([]);

        var response = await _service.GetAllSessionsAsync(OwnerId, GroupId);

        Assert.Empty(response.Participants);
        Assert.Equal(0m, response.Total);
        _dishes.Verify(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSessionsAsync_EmptyCartHasZeroTotalAndNoItems()
    {
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([Session("guest-session", "Olena", false, false)]);
        _dishes.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var response = await _service.GetAllSessionsAsync(OwnerId, GroupId);

        var participant = Assert.Single(response.Participants);
        Assert.Equal("guest-session", participant.Id);
        Assert.Empty(participant.Items);
        Assert.Equal(0m, participant.Total);
        Assert.Equal(0m, response.Total);
    }

    [Fact]
    public async Task GetAllSessionsAsync_PersistedNullCartHasNoItemsAndPreservesSessionId()
    {
        var storedSession = Session("legacy-guest-session", "Olena", false, false);
        storedSession.Dishes = null!;
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([storedSession]);
        _dishes.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var response = await _service.GetAllSessionsAsync(OwnerId, GroupId);

        var participant = Assert.Single(response.Participants);
        Assert.Equal(storedSession.Id, participant.Id);
        Assert.Empty(participant.Items);
        Assert.Equal(0m, participant.Total);
        Assert.Equal(0m, response.Total);
        _dishes.Verify(repository => repository.GetDishesByIdsAsync(
            It.Is<IEnumerable<string>>(ids => !ids.Any())), Times.Once);
    }

    [Fact]
    public async Task GetAllSessionsAsync_RejectsDifferentUserBeforeReadingSessions()
    {
        await Assert.ThrowsAsync<ActionForbiddenException>(() => _service.GetAllSessionsAsync("other-user", GroupId));

        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSessionsAsync_MissingGroupThrowsNotFound()
    {
        _groups.Setup(repository => repository.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetAllSessionsAsync(OwnerId, GroupId));
        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSessionsAsync_MissingReservationThrowsNotFound()
    {
        _reservations.Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetAllSessionsAsync(OwnerId, GroupId));
        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSessionsAsync_MissingCatalogDishFailsWholeRequest()
    {
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([
                Session("guest-1", "Olena", false, false,
                    new OrderItem { DishId = "salad", Quantity = 1 }),
                Session("guest-2", "Petro", false, false,
                    new OrderItem { DishId = "deleted-dish", Quantity = 1 })
            ]);
        _dishes.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([new Dish { Id = "salad", Name = "Caesar Salad", Price = 180m }]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => _service.GetAllSessionsAsync(OwnerId, GroupId));

        Assert.Contains("deleted-dish", exception.Message);
        _sessions.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    private static GuestSession Session(string id, string name, bool isOwner, bool submitted, params OrderItem[] items) => new()
    {
        Id = id,
        GuestName = name,
        GroupReservationId = GroupId,
        Dishes = [.. items],
        IsOwnerSession = isOwner,
        IsSubmitted = submitted
    };
}
