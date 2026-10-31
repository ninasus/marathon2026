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

public class DeleteGuestSessionServiceTests
{
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";
    private const string OwnerId = "owner-1";
    private const string TargetId = "guest-to-delete";

    private readonly Mock<IGroupReservationRepository> _groups = new();
    private readonly Mock<IGuestSessionRepository> _sessions = new();
    private readonly Mock<IDishRepository> _dishes = new();
    private readonly Mock<IReservationRepository> _reservations = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly GuestSessionService _service;

    public DeleteGuestSessionServiceTests()
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
    public async Task DeleteGuestSessionAsync_DeletesBeforeRequeryAndReturnsCanonicalRemainingSnapshot()
    {
        var calls = new List<string>();
        var target = Session(TargetId, "Deleted guest", false, false,
            new OrderItem { DishId = "deleted-dish", Quantity = 4 });
        var owner = Session("owner-session", "Owner", true, true,
            new OrderItem { DishId = "salad", Quantity = 2 });
        var remainingGuest = Session("remaining-guest", "Olena", false, false,
            new OrderItem { DishId = "soup", Quantity = 1 });

        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);
        _sessions.Setup(repository => repository.DeleteAsync(target))
            .Callback(() => calls.Add("delete"))
            .Returns(Task.CompletedTask);
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .Callback(() => calls.Add("query"))
            .ReturnsAsync([target, owner, remainingGuest]);
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

        GetGroupSessionsResponse response = await _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId);

        Assert.Equal(["delete", "query"], calls);
        GroupSessionParticipant[] participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.DoesNotContain(participants, participant => participant.Id == TargetId);

        GroupSessionParticipant ownerParticipant = participants[0];
        Assert.Equal("owner-session", ownerParticipant.Id);
        Assert.Equal(ParticipantType.Owner, ownerParticipant.Type);
        Assert.Equal("Andrii", ownerParticipant.Name);
        Assert.True(ownerParticipant.IsSubmitted);
        Assert.Equal(360m, ownerParticipant.Total);
        GroupSessionItem ownerItem = Assert.Single(ownerParticipant.Items);
        Assert.Equal("salad", ownerItem.Dish.Id);
        Assert.Equal("Caesar Salad", ownerItem.Dish.Name);
        Assert.Equal(180m, ownerItem.Dish.Price);
        Assert.Equal(2, ownerItem.Quantity);

        GroupSessionParticipant guestParticipant = participants[1];
        Assert.Equal("remaining-guest", guestParticipant.Id);
        Assert.Equal(ParticipantType.Guest, guestParticipant.Type);
        Assert.Equal("Olena", guestParticipant.Name);
        Assert.False(guestParticipant.IsSubmitted);
        Assert.Equal(220m, guestParticipant.Total);
        Assert.Equal(580m, response.Total);

        _dishes.Verify(repository => repository.GetDishesByIdsAsync(
            It.Is<IEnumerable<string>>(ids => ids.Count() == 2 && ids.Contains("salad") && ids.Contains("soup"))), Times.Once);
        _sessions.Verify(repository => repository.DeleteAsync(target), Times.Once);
        _sessions.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_LastParticipantReturnsEmptySnapshotWithoutCatalogLookup()
    {
        var target = Session(TargetId, "Only guest", false, false);
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([target]);

        GetGroupSessionsResponse response = await _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId);

        Assert.Empty(response.Participants);
        Assert.Equal(0m, response.Total);
        _sessions.Verify(repository => repository.DeleteAsync(target), Times.Once);
        _dishes.Verify(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_NonOwnerFailsBeforeTargetLookupOrMutation()
    {
        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.DeleteGuestSessionAsync("other-user", GroupId, TargetId));

        _sessions.Verify(repository => repository.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
        VerifyNoMutationOrRequery();
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_MissingTargetReturnsNotFoundWithoutMutation()
    {
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId));

        VerifyNoMutationOrRequery();
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_TargetFromAnotherGroupReturnsValidationErrorWithoutMutation()
    {
        var target = Session(TargetId, "Guest", false, false);
        target.GroupReservationId = "other-group";
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);

        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId));

        Assert.Contains("does not belong", exception.Message);
        VerifyNoMutationOrRequery();
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerCanDeleteOwnerSessionAndReturnsUpdatedSnapshot()
    {
        var target = Session(TargetId, "Owner", true, false);
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([target]);

        GetGroupSessionsResponse response = await _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId);

        Assert.Empty(response.Participants);
        Assert.Equal(0m, response.Total);
        _sessions.Verify(repository => repository.DeleteAsync(target), Times.Once);
        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(GroupId), Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_PostDeleteProjectionFailureLeavesDeletionCommittedWithoutSaveCompensation()
    {
        var target = Session(TargetId, "Deleted guest", false, false);
        var remaining = Session("remaining-guest", "Olena", false, false,
            new OrderItem { DishId = "missing-dish", Quantity = 1 });
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([remaining]);
        _dishes.Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        DishNotFoundException exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId));

        Assert.Contains("missing-dish", exception.Message);
        _sessions.Verify(repository => repository.DeleteAsync(target), Times.Once);
        _sessions.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_PostDeleteQueryFailurePropagatesWithoutSaveCompensation()
    {
        var target = Session(TargetId, "Deleted guest", false, false);
        var failure = new InvalidOperationException("query failed");
        _sessions.Setup(repository => repository.GetByGuestSessionId(TargetId)).ReturnsAsync(target);
        _sessions.Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupId))
            .ThrowsAsync(failure);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteGuestSessionAsync(OwnerId, GroupId, TargetId));

        Assert.Same(failure, exception);
        _sessions.Verify(repository => repository.DeleteAsync(target), Times.Once);
        _sessions.Verify(repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    private void VerifyNoMutationOrRequery()
    {
        _sessions.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _sessions.Verify(repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
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
