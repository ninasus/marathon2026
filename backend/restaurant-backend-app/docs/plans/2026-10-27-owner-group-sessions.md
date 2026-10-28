# Group Guest Sessions API Implementation Plan

**Goal:** Return all group pre-order participants to the reservation creator, including their own row, with dishes, per-participant totals, submission status, and the group total.

**Architecture:** Use the existing guest-session group query for joined participants and the parent reservation plus active customer pre-order for the always-present reservation creator participant. Set the creator's response type from the presence of a stored `GuestSession` with `IsOwnerSession == true`: `Owner` when present, `Guest` when the row comes only from the reservation. Other sessions remain `Guest`. A dedicated GET response DTO limits each dish to `id`, `name`, and `price`. Register the Lambda route and a Cognito-protected API Gateway method.

**Tech stack:** .NET Lambda, API Gateway, DynamoDB repository interfaces, AWS Syndicate configuration, xUnit/Moq.

## Requested behavior and scope

- Return HTTP 200 with `participants` and `total`, with each participant's `id`, `type`, `name`, `items` (`dish` plus `quantity`), `total`, and `isSubmitted`.
- Calculate each participant's total as the sum of `dish.price * quantity`; calculate the group total from participant totals using `decimal` values.
- The reservation creator is **always** present in `participants`. When a stored `GuestSession` has `IsOwnerSession == true`, the creator row is backed by that session and has `type: "Owner"` and its session `id`, even if an active order supplies the row's dishes and submission state. When no such session exists, the row is derived from the parent reservation and has `type: "Guest"` and `id: null`, including when an active customer order exists. With neither a stored owner session nor an active order, emit empty items, zero total, and `isSubmitted: false`. Resolve the creator's display name from the reservation customer, consistent with `GetGuestSessionInfoAsync`.
- Count an active order as the creator's dish source only when `Order.CustomerId` matches the parent reservation's `CustomerId`. `OrderStatus.SubmittedByCustomer` or `OrderStatus.Submitted` maps to `isSubmitted: true`; `Created` maps to false. `IOrderRepository.GetOrderByReservationIdAsync` already excludes cancelled orders. If both a stored creator session and an active order exist, use the active order's dishes and submission state once, without double counting.
- `CreateGuestSessionHandler` receives only `groupReservationId` and `guestName`, and `CreateGuestSessionAsync` saves sessions with the default `IsOwnerSession == false`. Its sessions are Guest rows. Do not infer that a session belongs to the reservation creator from its name or caller authentication. `Owner` in the response is determined solely by `IsOwnerSession == true`, not by reservation ownership, order ownership, name, or submission state.
- Require service-layer authorization: load the group and parent reservation, then compare the authenticated Cognito `sub`/service `userId` to `Reservation.CustomerId` before any guest-session, order, or dish read. Missing Cognito identity returns 401, a different user returns 403, and a missing group or parent reservation returns 404. Missing path ID returns 400.
- Add a dedicated GET response DTO so each item is exactly `{ "dish": { "id", "name", "price" }, "quantity" }`, with no additional dish fields. Serialize this endpoint in camelCase with enum names as strings. Leave the existing DELETE response contract unchanged.
- Use current dish catalog prices, consistent with the existing guest-session and delete-session responses. A referenced dish missing from the catalog should produce a controlled client error rather than an unhandled lookup exception.
- No new database table or index is needed: `IGuestSessionRepository.GetAllByGroupReservationIdAsync`, `IOrderRepository.GetOrderByReservationIdAsync`, and `IDishRepository.GetDishesByIdsAsync` already exist.

## Affected code paths and agent assignments

1. **Production — `restaurant_lambda_engineer`:** Own the incremental type change in `dnapp/lambdas/api-handler/Models/GuestSessions/GetGroupSessionsResponse.cs` and `Services/GuestSessions/GuestSessionService.cs`. Update `swagger_src/swagger-ui/swagger-endpoints.yaml` and `docs/ApiRoutes.md` to document the conditional `Owner`/`Guest` type. Preserve the existing handler, route, authorization sequence, order-source precedence, minimal dish DTO, and DELETE response contract. Do not modify unrelated code or invoke AWS.
2. **Tests — `restaurant_backend_test_engineer`:** Own focused updates in `Tests/Services/GuestSessionServiceGetAllTests.cs` and `Tests/Handlers/GuestSessions/GetAllGuestSessionsHandlerTests.cs`. Cover the conditional type and run the full solution test suite.
3. **Review — `restaurant_backend_reviewer`:** Read-only review of the completed diff against the pre-change baseline, including production code, tests, config, and docs. Return concrete findings. Production fixes go back to the Lambda engineer and test fixes to the test engineer, followed by another full test run and review.

## Implementation steps

- [ ] Extend `GroupSessionParticipantType` in the existing GET response DTO to include `Owner`, keeping JSON enum serialization as strings and the dish DTO limited to `Id`, `Name`, and `Price`. Do not change the response type used by DELETE.
- [ ] In `GetAllGuestSessionsAsync(string userId, string groupReservationId)`, assign the creator row `Owner` only when the queried `ownerSession` has `IsOwnerSession == true`; otherwise assign `Guest`. Pass the type through the participant projection without changing dish/order selection, totals, ID rules, or service-layer authorization. Sessions with `IsOwnerSession == false` remain `Guest`.
- [ ] Collect distinct dish IDs across the selected creator dish source and other Guest sessions, load dishes once, map minimal item DTOs, and sum decimal totals. Handle a creator with no order/session and guests with empty carts without a dish lookup; handle missing referenced dishes explicitly. Preserve ownership checks even when no guest sessions exist.
- [ ] Add a GET handler that reads `groupReservationId` and Cognito `sub`, calls the service, returns `ResponseBuilder.CreateJsonResponse(200, result)`, and maps known errors to 400/401/403/404 as above.
- [ ] Register `/guest/groups/{groupReservationId}/sessions` in `RouteRegistry` and add its GET method with `cognito_authorizer` to the AWS Syndicate `deployment_resources.json`.
- [ ] Align the Swagger operation and response schema with the actual nested `dish` item shape, add 401/403 responses, and update the stale `docs/ApiRoutes.md` response example.
- [ ] Build `dnapp/lambdas/api-handler/APIHandler.csproj`; report the exact result.
- [ ] Update focused xUnit tests: a stored `IsOwnerSession == true` produces exactly one `Owner` participant whether its dishes come from that session or an active customer order; a reservation-derived creator with no stored owner session remains `Guest` with null ID, even when an active order exists; ordinary created guest sessions remain `Guest` even if their name matches the creator. Assert serialized JSON has literal `"type":"Owner"` or `"type":"Guest"` as appropriate and each dish object still has exactly `id`, `name`, and `price`. Keep the existing wrong-user test proving no participant-data reads. Run `dotnet test backend/restaurant-backend-app/restaurant-backend-app.sln`; report totals and failures exactly.
- [ ] Review the diff; route actionable findings to the owning engineer; repeat the full test suite and review after fixes.

## Acceptance criteria

- The authenticated reservation creator receives one row for themselves plus every joined session and correct decimal totals for only the requested group. The creator row has `type: "Owner"` if and only if backed by a stored `GuestSession` with `IsOwnerSession == true`; otherwise it has `type: "Guest"`. All ordinary joined sessions have `type: "Guest"`. The creator row includes dishes from their active customer pre-order; an otherwise empty group still returns a creator row.
- Every item's `dish` JSON object contains exactly `id`, `name`, and `price`.
- Unauthenticated or non-creator callers receive no participant data, with 401 or 403 respectively. The service must reject a non-creator before reading sessions, orders, or dishes.
- The API route is reachable through Lambda routing and represented as a Cognito-authorized GET in deployment config and Swagger.
- No-guest groups and missing referenced dishes have deliberate behavior; no unhandled index exception is exposed.
- The API project builds, the full solution test suite passes, and the read-only reviewer has no unresolved actionable findings.

## Material risks and open questions

- The latest user correction makes `Owner` a response type for a stored `IsOwnerSession` participant. Reservation ownership still determines authorization. `CreateGuestSessionHandler` calls `CreateGuestSessionAsync`, which creates ordinary guest sessions without a creator identity link. Reservation creation and group creation do not create a creator session; `CreateGroupOwnerSessionAsync` has no caller in this backend. Therefore the creator's identity is guaranteed by the reservation, while a persisted creator session is **not** guaranteed. The current creator dish path is `CreateOrderHandler`/`EditPreOrderHandler` → `OrderService` → `IOrderRepository`, so the active customer order is the creator row's primary dish source.
- An ordinary guest session created anonymously by the reservation creator cannot be identified as theirs from current fields. If the creator separately joins through `CreateGuestSessionHandler`, the API may show that session in addition to the reservation-derived creator row. Preventing this would require changing session creation to link authenticated creator identity, which is outside this GET endpoint's scope.
- If a stored `IsOwnerSession` session and active customer order both contain different dishes, this plan uses the active order as the current creator pre-order source and does not merge the session cart. This avoids double counting; any required merge would need explicit product rules for duplicate dishes and submission state.
- Current catalog prices can differ from prices at the time dishes were added. Historical prices are not stored in `GuestSession.Dishes`; historical pricing is outside this endpoint's scope.
- `docs/ApiRoutes.md` and Swagger currently disagree about this response. Both must be aligned with the implemented shape.
