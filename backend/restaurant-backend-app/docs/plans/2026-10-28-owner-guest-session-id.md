# Owner Guest Session ID API Implementation Plan

**Goal:** Add `GET /reservations/{reservationId}/guest-session-id` so the authenticated customer who owns a reservation can retrieve the ID of that reservation's persisted owner guest session.

**Architecture:** Add a Cognito-protected Lambda route and handler backed by `GuestSessionService`. The service will authorize against `Reservation.CustomerId` before querying group/session data, resolve the group reservation by `reservationId`, then select the session explicitly marked as the owner session and linked to the authenticated owner. The endpoint is read-only: it will not create an owner session during a GET request.

**Tech stack:** .NET Lambda, API Gateway, DynamoDB repositories, AWS Syndicate configuration, xUnit/Moq.

## Requested behavior and scope

- Accept `GET /reservations/{reservationId}/guest-session-id` with Cognito authentication.
- Return HTTP 200 with exactly:

  ```json
  {
    "guestSessionId": "owner-session-123"
  }
  ```

- Treat the caller as the reservation owner only when the authenticated Cognito `sub` equals `Reservation.CustomerId`.
- Reject a missing authenticated user with 401 and a non-owner caller with 403.
- Return 400 for a missing/blank path parameter or when the reservation has no persisted owner session. Return 404 when the reservation or its group reservation does not exist.
- Resolve only a session whose `IsOwnerSession` flag is true and whose `OwnerUserId` matches the authenticated reservation owner. Ordinary guest sessions must never be returned by name or position.
- Perform the reservation ownership check before reading group-reservation or guest-session data, so a non-owner cannot use the endpoint to probe session existence.
- Keep the endpoint read-only. Creating or backfilling owner sessions is outside this change.

## Scope and affected code paths

1. **Production — `restaurant_lambda_engineer`:** Own the new handler and response DTO plus the service, routing, API Gateway, and API contract changes:
   - `dnapp/lambdas/api-handler/Handlers/GuestSessions/GetOwnerGuestSessionIdHandler.cs` (new)
   - `dnapp/lambdas/api-handler/Models/GuestSessions/GetOwnerGuestSessionIdResponse.cs` (new)
   - `dnapp/lambdas/api-handler/Services/GuestSessions/IGuestSessionService.cs`
   - `dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`
   - `dnapp/lambdas/api-handler/Routing/RouteRegistry.cs`
   - `deployment_resources.json`
   - `swagger_src/swagger-ui/swagger-endpoints.yaml`
   - `docs/ApiRoutes.md`
   Existing repository methods (`IReservationRepository.GetReservationByIdAsync`, `IGroupReservationRepository.GetByReservationIdAsync`, and `IGuestSessionRepository.GetAllByGroupReservationIdAsync`) are sufficient; do not add a table or index unless implementation evidence proves otherwise.
2. **Tests — `restaurant_backend_test_engineer`:** Own focused xUnit tests under `Tests/Services/` and `Tests/Handlers/GuestSessions/`, including service authorization/read-order assertions, owner-session selection, absence cases, response serialization, and handler status mapping. Run the full `restaurant-backend-app.sln` suite.
3. **Review — `restaurant_backend_reviewer`:** Perform a read-only review of the completed production and test diff against this plan. Route production findings back to the Lambda engineer and test findings to the test engineer, then rerun the full suite and re-review as needed.

## Concrete implementation steps

- [x] Add `GetOwnerGuestSessionIdResponse` with a `GuestSessionId` property/record parameter so existing JSON settings serialize it as `guestSessionId`.
- [x] Add `GetOwnerGuestSessionIdAsync(string userId, string reservationId)` to `IGuestSessionService` and `GuestSessionService`.
- [x] In the service, load the reservation by the requested ID; throw `ReservationNotFoundException` if absent. Compare `Reservation.CustomerId` with `userId`; throw `ActionForbiddenException` before any group/session query if they differ.
- [x] Resolve the group with `IGroupReservationRepository.GetByReservationIdAsync`; throw `GroupReservationNotFoundException` if absent. Read sessions for that group and select a session only when `IsOwnerSession == true` and `OwnerUserId == userId`. Throw a controlled `ValidationException`, mapped to HTTP 400, if none exists. Return its ID in the response DTO. If duplicate owner sessions are detected for the same owner/group, fail with a controlled validation error rather than returning an arbitrary record.
- [x] Add `GetOwnerGuestSessionIdHandler`: validate `reservationId`, read Cognito `sub`, call the service, return camel-case JSON on 200, and map known failures to 400/401/403/404.
- [x] Register the route in `RouteRegistry` and add a Cognito-authorized GET method for the path in root `deployment_resources.json`.
- [x] Align the existing Swagger/docs stub with the implemented authorization and error behavior, including the endpoint's actual error response bodies.
- [x] Build `dnapp/lambdas/api-handler/APIHandler.csproj`; verified with 0 errors (existing dependency warnings remain).
- [x] Add focused service tests for successful strict owner lookup, non-owner read ordering, missing reservation/group/session, wrong-session exclusion, and duplicate owner-session handling.
- [x] Add focused handler tests for exact `{ "guestSessionId": "..." }` JSON and the approved 400/401/403/404 mappings.
- [x] Run the focused tests (17 passed) and full solution tests (180 passed, 0 failed, 0 skipped), resolve the Swagger review finding, rerun the full suite, and obtain a clean follow-up review.

## Test strategy

- Use Moq to prove authorization occurs before group/session reads for a non-owner.
- Exercise mixed session lists to prove selection is based on both `IsOwnerSession` and `OwnerUserId`, not guest name, ordering, or merely the owner flag.
- Parse the handler's JSON response to assert the property name and value exactly.
- Run the API project build first, then the full solution test suite after focused tests are added and after any review fixes.

## Acceptance criteria

- The authenticated reservation owner receives HTTP 200 and exactly the persisted owner session ID for the requested reservation.
- A caller who does not own the reservation receives 403 and triggers no group/session data reads.
- Missing authentication returns 401; missing/blank path input or a missing owner session returns 400; missing reservation or group reservation returns 404.
- An ordinary guest session, an owner session for another user, or an arbitrary duplicate is never returned as the caller's session.
- The route is registered in Lambda routing and represented as a Cognito-protected method in AWS Syndicate configuration and Swagger.
- The API project builds, the full solution test suite passes, and the reviewer has no unresolved actionable findings.

## Material risks and open questions

- `GuestSessionService.CreateGroupOwnerSessionAsync` currently exists but is not declared on `IGuestSessionService` and has no production caller. Reservation/group creation therefore does not guarantee that an owner guest session exists. Per the approved API behavior, the new GET returns 400 when it is absent. Automatically creating/backfilling an owner session remains outside scope.
- Existing persisted data must have both `IsOwnerSession = true` and the correct `OwnerUserId`. This strict match prevents leaking a mislabeled session but may expose legacy/incomplete records as 400.
- The previously incomplete Swagger/docs stub, Lambda route, and API Gateway resource were aligned during implementation. No live AWS deployment or invocation was performed.
