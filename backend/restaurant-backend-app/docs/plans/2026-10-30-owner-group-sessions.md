# Owner group session list API plan

## Requested behavior

Add `GET /guest/groups/{groupReservationId}/sessions` for the authenticated owner of the underlying reservation. Return one participant per stored `GuestSession` in the group, including the owner session when present. Each participant's `id` must equal that record's `GuestSession.Id`; the remaining fields are `type` (`Owner` or `Guest`), `name`, `items` from `GuestSession.Dishes`, `total`, and `isSubmitted`. Each item has a dish with `id`, `name`, and `price`, plus `quantity`. The top-level `total` is the sum of participant totals. Empty carts have `items: []` and total `0`.

## Scope and affected code

- Add a handler under `dnapp/lambdas/api-handler/Handlers/GuestSessions` that reads `groupReservationId` and the Cognito `sub` claim, calls the service, and maps missing group/reservation to 404 and non-owner to 403.
- Add `GetAllSessionsAsync(userId, groupReservationId)` to `IGuestSessionService` and `GuestSessionService`. Reuse existing reservation-owner validation, `IGuestSessionRepository.GetAllByGroupReservationIdAsync`, bulk dish lookup, and owner-name lookup. Calculate prices with `decimal`. If any `GuestSession.Dishes` item references a dish absent from the catalog, fail the entire request with HTTP 404. The list is derived from stored sessions; no session or order is mutated by GET.
- Add a response DTO in `Models/GuestSessions` for the exact requested JSON fields, using the existing `ParticipantType` string enum behavior. Keep existing delete-session response behavior intact.
- Register the route in `Routing/RouteRegistry.cs`. Correct the existing singular `/guest/group/{groupReservationId}/sessions` resource to the requested plural `/guest/groups/{groupReservationId}/sessions` in `deployment_resources.json`, retaining Cognito authorization.
- No new DynamoDB index or repository method is needed; the group-reservation ID index and list query already exist.

## Implementation and agent assignments

1. **`restaurant_lambda_engineer` — production code:** Implement the DTO, service method, handler, route, and Syndicate path above. Preserve existing unrelated work. Map every participant `id` directly from its `GuestSession.Id`. Use `IsOwnerSession` for participant type, `GuestName` for guests, and the owner's Cognito given name as the owner display name, matching `GetGuestSessionInfoAsync`. Include all persisted sessions regardless of submission state. Raise a validation error for any referenced dish absent from the catalog, and return HTTP 400 from the handler. Build the solution and report exact result.
2. **`restaurant_backend_test_engineer` — tests:** Add focused xUnit service and handler tests asserting that owner and guest participant IDs exactly match their respective `GuestSession.Id` values, as well as quantities and totals, empty carts, submitted flags, a missing group/reservation, missing identity, and a different authenticated user. Assert at service and handler levels that a session referencing a deleted catalog dish fails the whole request with HTTP 400 and no partial participant list. Run the full `backend/restaurant-backend-app/restaurant-backend-app.sln` test suite and report exact result. Route and deployment configuration must be checked as part of verification.
3. **`restaurant_backend_reviewer` — read-only review:** Review the final diff against this plan for access control, JSON contract, aggregation, and regression risks. Route any findings to the relevant engineer, repeat full tests, and review again.

## Acceptance criteria

- Only the reservation's `CustomerId` may retrieve its group session list; unauthenticated requests get 401 and other users get 403.
- The requested plural URL is available through both route registration and Syndicate API configuration.
- Existing owner and guest `GuestSession` records appear once each; each participant `id` equals its corresponding `GuestSession.Id`, with correct type, name, dish ID/name/price, quantity, per-participant total, submission state, and grand total.
- A group with no sessions returns `participants: []` and `total: 0`.
- If any session item refers to a catalog dish that has been deleted, the endpoint returns HTTP 400 for the entire request, without a partial list or total.
- Production build, full solution tests, and independent review pass.

## Material risks and decisions

- `CreateGroupOwnerSessionAsync` currently has no caller in this repository. This endpoint reports an owner row only when an owner `GuestSession` has been created by the existing workflow or data. Creating or populating that session is outside this read endpoint's scope; if live owner sessions are absent, that needs a separate production change.
- Dish prices and names come from the current dish catalog because `GuestSession.Dishes` stores only dish IDs and quantities. A deleted catalog dish cannot be faithfully priced from the stored session; the agreed response is HTTP 404 for the entire request.
