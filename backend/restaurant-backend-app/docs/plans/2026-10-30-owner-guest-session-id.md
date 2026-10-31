# Owner guest-session ID API plan

## Requested behavior

Add `GET /reservations/{reservationId}/guest-session-id` for an authenticated reservation owner. For a valid owner request, return `200 OK` with the exact JSON contract:

```json
{
  "guestSessionId": "owner-session-123"
}
```

The caller must be the reservation's `CustomerId`. Return `404 Not Found` when the reservation, its group reservation, or the owner's guest session does not exist. Return `401 Unauthorized` when the Cognito `sub` claim is absent and `403 Forbidden` when an authenticated caller is not the reservation owner.

## Scope and affected code paths

- Add a response model in `dnapp/lambdas/api-handler/Models/GuestSessions` for the exact camel-case `guestSessionId` response.
- Add a handler in `dnapp/lambdas/api-handler/Handlers/GuestSessions` that reads `reservationId` and the authenticated Cognito `sub`, calls the guest-session service, and maps the expected authentication, authorization, and not-found outcomes.
- Extend `IGuestSessionService` and `GuestSessionService` with a read-only owner-session lookup:
  1. Load the reservation by `reservationId`; throw `ReservationNotFoundException` if absent.
  2. Compare `Reservation.CustomerId` with the authenticated user ID; throw `ActionForbiddenException` when they differ.
  3. Load the group reservation through `IGroupReservationRepository.GetByReservationIdAsync`; throw `GroupReservationNotFoundException` if absent.
  4. Load the group's sessions through `IGuestSessionRepository.GetAllByGroupReservationIdAsync` and select the record where `IsOwnerSession` is true and `OwnerUserId` matches the authenticated owner; throw `GuestSessionNotFoundException` if absent.
  5. Return that record's `GuestSession.Id` without mutating any data.
- Register the route in `Routing/RouteRegistry.cs` and add the Cognito-authorized resource to the root `deployment_resources.json` API Gateway configuration.
- Keep the existing `docs/ApiRoutes.md` response contract and expand it only if needed to document authorization and not-found behavior.
- No table, entity, AWS IAM, or DynamoDB index change is required; the existing group-reservation-by-reservation index and guest-session-by-group index support the lookup.

## Implementation steps and agent assignments

1. **`restaurant_lambda_engineer` — production code and configuration**
   - Implement the response model, service contract and logic, handler, route registration, and root Syndicate API resource.
   - Preserve existing endpoint behavior and unrelated work.
   - Use the existing domain exceptions and return the owner session's persisted `Id` exactly.
   - Ensure the API Gateway resource uses `cognito_authorizer`.
   - Build `backend/restaurant-backend-app/restaurant-backend-app.sln` and report the exact result.
2. **`restaurant_backend_test_engineer` — focused and full verification**
   - Add focused xUnit service tests for the success path, non-owner rejection, missing reservation, missing group reservation, and missing owner session.
   - Add handler tests for the exact JSON contract, missing identity, forbidden caller, and all three `404` exception mappings.
   - Verify route registration and deployment configuration where practical without coupling tests to unrelated resources.
   - Run the full `backend/restaurant-backend-app/restaurant-backend-app.sln` test suite and report exact totals and failures.
3. **`restaurant_backend_reviewer` — read-only review**
   - Review the final production and test diff against this plan, focusing on authorization order, cross-reservation/session leakage, exact response casing, exception mapping, route exposure, and regressions.
   - Route production findings back to `restaurant_lambda_engineer` and test findings to `restaurant_backend_test_engineer`; rerun the full suite and request another review until actionable findings are resolved or a blocker is reported.

## Test strategy

- Service success: the authenticated `CustomerId` receives the `Id` of the matching owner session, not a guest session in the same group.
- Authorization: a different authenticated user receives `403` and no session ID; a missing Cognito identity receives `401` before service invocation.
- Not-found behavior: independently cover absent reservation, absent group reservation, and a group with no matching owner session, each producing `404` at the API boundary.
- Contract/configuration: assert the body serializes as `{ "guestSessionId": "..." }`; confirm the route exists in both the in-process registry and Cognito-authorized Syndicate API configuration.
- Regression: build the solution, run focused tests during development, then run the complete solution test suite after all changes and after any review fixes.

## Acceptance criteria

- `GET /reservations/{reservationId}/guest-session-id` is registered in the Lambda router and API Gateway/Syndicate configuration with Cognito authorization.
- A reservation owner receives `200` and exactly one `guestSessionId` equal to the persisted owner `GuestSession.Id` for that reservation's group.
- An unauthenticated request returns `401`; an authenticated non-owner returns `403` and cannot learn a session ID.
- A missing reservation, group reservation, or matching owner session returns `404`.
- The endpoint performs no writes and introduces no new DynamoDB schema or index.
- The solution builds, all focused and full xUnit tests pass, and the independent backend review has no unresolved actionable findings.

## Material risks and implementation decisions

- `IGuestSessionRepository.GetByGroupReservationId` returns an arbitrary first session and cannot safely identify the owner. The implementation will query the existing session list and explicitly filter by both `IsOwnerSession` and `OwnerUserId`.
- Ownership is checked immediately after loading the reservation, before returning group/session details, so an authenticated non-owner cannot use the endpoint to discover downstream records.
- Existing owner-session creation stores both `IsOwnerSession = true` and `OwnerUserId`; inconsistent legacy data that lacks either marker will be treated as a missing owner session and return `404`, which matches the requested missing-session behavior.
- The repository already documents this response shape in `docs/ApiRoutes.md`, but the executable route is not yet present. This change makes the documented endpoint real rather than introducing a second URL.
