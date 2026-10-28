using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public partial class GuestSessionServiceTests
{
    private const string OwnerSessionReservationId = "reservation-owner-session";
    private const string OwnerSessionGroupId = "group-owner-session";
    private const string OwnerSessionUserId = "owner-user";

    private void ArrangeOwnerSessionReservation()
    {
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(OwnerSessionReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = OwnerSessionReservationId,
                CustomerId = OwnerSessionUserId,
                TableId = "table-1",
                WaiterId = "waiter-1"
            });

        _mockGroupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(OwnerSessionReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = OwnerSessionGroupId,
                ReservationId = OwnerSessionReservationId,
                ReservationToken = "invite-token"
            });
    }

    private static GuestSession OwnerSessionCandidate(
        string id,
        bool isOwnerSession,
        string? ownerUserId) => new()
    {
        Id = id,
        GroupReservationId = OwnerSessionGroupId,
        GuestName = "Guest",
        Dishes = [],
        IsOwnerSession = isOwnerSession,
        OwnerUserId = ownerUserId
    };

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReturnsOnlySessionMatchingOwnerFlagAndOwnerUserId()
    {
        ArrangeOwnerSessionReservation();
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(OwnerSessionGroupId))
            .ReturnsAsync([
                OwnerSessionCandidate("ordinary-guest", false, OwnerSessionUserId),
                OwnerSessionCandidate("other-owner", true, "different-user"),
                OwnerSessionCandidate("expected-owner-session", true, OwnerSessionUserId)
            ]);

        GetOwnerGuestSessionIdResponse response = await _guestSessionService
            .GetOwnerGuestSessionIdAsync(OwnerSessionUserId, OwnerSessionReservationId);

        Assert.Equal("expected-owner-session", response.GuestSessionId);
        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(OwnerSessionReservationId), Times.Once);
        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(OwnerSessionReservationId), Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(OwnerSessionGroupId), Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_WrongCallerIsRejectedBeforeGroupOrSessionReads()
    {
        ArrangeOwnerSessionReservation();

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync("different-user", OwnerSessionReservationId));

        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(OwnerSessionReservationId), Times.Once);
        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MissingReservationStopsBeforeGroupOrSessionReads()
    {
        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerSessionUserId, OwnerSessionReservationId));

        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(OwnerSessionReservationId), Times.Once);
        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MissingGroupStopsBeforeSessionRead()
    {
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(OwnerSessionReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = OwnerSessionReservationId,
                CustomerId = OwnerSessionUserId,
                TableId = "table-1",
                WaiterId = "waiter-1"
            });

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerSessionUserId, OwnerSessionReservationId));

        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(OwnerSessionReservationId), Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NoMatchingOwnerSessionThrowsValidationException()
    {
        ArrangeOwnerSessionReservation();
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(OwnerSessionGroupId))
            .ReturnsAsync([
                OwnerSessionCandidate("ordinary-guest", false, OwnerSessionUserId),
                OwnerSessionCandidate("other-owner", true, "different-user")
            ]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerSessionUserId, OwnerSessionReservationId));

        Assert.Contains("does not have an owner guest session", exception.Message);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_DuplicateMatchingOwnerSessionsThrowsValidationException()
    {
        ArrangeOwnerSessionReservation();
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(OwnerSessionGroupId))
            .ReturnsAsync([
                OwnerSessionCandidate("owner-session-1", true, OwnerSessionUserId),
                OwnerSessionCandidate("owner-session-2", true, OwnerSessionUserId)
            ]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerSessionUserId, OwnerSessionReservationId));

        Assert.Contains("multiple owner guest sessions", exception.Message);
    }
}
