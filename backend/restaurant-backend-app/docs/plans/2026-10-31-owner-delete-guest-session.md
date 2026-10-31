# Owner removes a participant session from a group pre-order

## Requested behavior

Implement `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` for the authenticated reservation owner. A successful request removes the selected participant session, including the owner's own session when selected, and returns the updated group snapshot with the remaining participants, their items and totals, and the recomputed group total.

Required outcomes:

- Only the owner of the reservation associated with the group may perform the operation.
- A missing guest session returns HTTP 404.
- A session that exists but belongs to another group reservation returns HTTP 400 and is not deleted.
- A successful deletion returns HTTP 200 with the same camel-case snapshot contract as `GET /guest/groups/{groupReservationId}/sessions`.
- The authenticated reservation owner may remove either a guest session or the owner's own session.

## Scope and affected code paths

The route and Cognito-authorized DELETE resource already exist in `Routing/RouteRegistry.cs` and `deployment_resources.json`, so they will be verified rather than duplicated. The existing delete implementation will be completed and aligned with the current group-snapshot implementation.

Expected production changes:

- `dnapp/lambdas/api-handler/Services/GuestSessions/IGuestSessionService.cs`
  - Return the canonical `GetGroupSessionsResponse` from `DeleteGuestSessionAsync`.
- `dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`
  - Preserve owner validation before mutation.
  - Distinguish a missing session (404) from a session belonging to another group (400).
  - Permit deletion of the owner's session when requested by the authenticated reservation owner.
  - Share one projection path between GET and DELETE so participant type/name, dish details, submission state, participant totals, and group total stay identical.
  - Delete the selected session first, then query the remaining group state and recalculate the snapshot from that post-delete state.
  - Filter the deleted session id from the post-delete query result defensively because the group lookup uses a DynamoDB global secondary index and can briefly return the deleted record while the index converges.
  - If post-delete recalculation fails, propagate the failure and leave the successful deletion committed; do not restore the deleted session.
- `dnapp/lambdas/api-handler/Handlers/GuestSessions/DeleteGuestSessionHandler.cs`
  - Return the canonical response with the camel-case JSON serializer.
  - Keep 401/403/404 handling and map membership/owner-session validation failures to 400.
- `dnapp/lambdas/api-handler/Models/GuestSessions/GetAllResponse.cs`
  - Remove the obsolete duplicate delete-response model if it has no remaining callers after the canonical response migration.
- `swagger_src/swagger-ui/swagger-endpoints.yaml` and `docs/ApiRoutes.md`
  - Document the updated snapshot response and the relevant 400/401/403/404 outcomes.

Expected test changes:

- Add focused service tests for deletion, authorization, membership validation, owner-session protection, repository mutation, and recomputed snapshots.
- Add focused handler tests for path/identity validation, status mappings, exact camel-case JSON, and service call forwarding.
- Add or extend endpoint configuration coverage to assert the exact route, DELETE method, Cognito authorization, and Lambda integration.

## Implementation steps

1. Change the delete service contract to return `GetGroupSessionsResponse`.
2. Extract/refactor the existing group-session-to-snapshot projection into a private shared service helper that safely handles empty or null dish lists, resolves the owner display name, validates referenced dishes, calculates participant totals, and calculates the group total.
3. In `DeleteGuestSessionAsync`, validate the authenticated reservation owner, load the requested session, return 404 when absent, and return 400 when its group id differs.
4. Delete the selected entity first.
5. Query the group's sessions again after the delete, defensively exclude the deleted id from the query result, build the updated snapshot from the remaining sessions, and return it.
6. If the post-delete query or snapshot projection fails, propagate the original failure without restoring the deleted session. Mapped domain failures retain their mapped status; unexpected failures are returned as HTTP 500 by the existing top-level exception handler.
7. Update the handler to serialize the canonical response with `ResponseBuilder.CreateJsonResponse(200, ...)`, preserving the established exception-to-status mappings.
8. Remove the obsolete duplicate DTO when it is no longer referenced, and update API documentation.
9. Verify the already-present runtime route and Syndicate resource configuration; change them only if tests expose a mismatch.

## Agent assignments

1. `restaurant_lambda_engineer` owns all production code and API documentation listed above. It must preserve unrelated work, make no cloud changes, build `backend/restaurant-backend-app/restaurant-backend-app.sln`, and report the exact result and changed files.
2. `restaurant_backend_test_engineer` owns focused xUnit coverage under `Tests`, including service, handler, and endpoint configuration tests. It must run the focused tests and then the full `backend/restaurant-backend-app/restaurant-backend-app.sln` suite, reporting exact counts and failures.
3. `restaurant_backend_reviewer` performs a read-only review of the final production and test diff against this plan, focusing on authorization order, status-code distinctions, mutation safety, response-contract consistency, and regressions.

## Test strategy

Service tests will prove:

- The reservation owner can delete a guest session or the owner's session in the requested group.
- The repository deletes exactly the selected session before querying the remaining group sessions and building the updated snapshot.
- A stale post-delete GSI query result containing the deleted id is filtered so the removed participant cannot reappear in the response.
- Remaining Owner and Guest participants retain correct ids, types, names, items, quantities, totals, and submission flags; the group total excludes the deleted session.
- Deleting the last guest leaves the owner (when present) and returns the correct total; an otherwise empty group returns `participants: []` and `total: 0`.
- A non-owner receives a forbidden error and no session lookup/delete occurs.
- A missing target session produces 404 and no delete.
- A target from another group produces 400 and no delete.
- An owner-session target produces 400 and no delete.
- A missing dish referenced by a remaining participant follows the existing group-snapshot not-found behavior after the selected session has already been deleted.
- A post-delete query/projection failure propagates the original error, performs no `SaveAsync` compensation, and leaves the deletion committed.

Handler tests will prove:

- Success returns HTTP 200 and exactly the requested camel-case JSON shape.
- Missing path parameters return 400; missing Cognito identity returns 401.
- Non-owner maps to 403, missing group/reservation/session maps to 404, and validation errors map to 400.
- The handler forwards the authenticated `sub`, group id, and session id exactly once.

Configuration tests will inspect `deployment_resources.json` and the runtime registry to confirm the exact DELETE route uses the Cognito authorizer and `api-handler` Lambda.

Finally, run `dotnet build backend/restaurant-backend-app/restaurant-backend-app.sln` and `dotnet test backend/restaurant-backend-app/restaurant-backend-app.sln` (or from that solution directory) and record the exact results.

## Acceptance criteria

- `DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}` is reachable as a Cognito-authorized DELETE endpoint.
- Only the associated reservation owner can remove a participant session; a different authenticated user receives 403 and no mutation occurs.
- A nonexistent session returns 404 and no mutation occurs.
- A session belonging to another group returns 400 and no mutation occurs.
- An owner session may be removed by the authenticated reservation owner.
- Success deletes exactly the requested participant session and returns HTTP 200 with `participants` and `total` in camel case.
- Successful recalculation reads the remaining group sessions only after the delete completes, and defensively excludes the deleted id from an eventually consistent GSI result.
- The returned participants use the same model and semantics as the existing group snapshot: correct Owner/Guest types, names, dish id/name/price, quantities, per-participant totals, and `isSubmitted` values.
- The top-level total is recalculated from remaining participants.
- A post-delete recalculation failure is propagated without rollback; the selected session remains deleted. Unexpected failures are surfaced as HTTP 500 by the existing top-level exception handler.
- Focused tests, full solution build, and full solution test suite pass.
- Final backend review has no unresolved actionable findings.

## Material risks and implementation choices

- The user-selected sequence is delete first, then recalculate from a fresh group query. The current repository exposes `DeleteAsync` and `GetAllByGroupReservationIdAsync`; it has no transaction abstraction. Although the registered AWS client supports DynamoDB transactions, a DynamoDB transaction cannot atomically include the arbitrary reads, dish/user lookups, and in-memory total calculation that produce this response, and there is no second durable aggregate write to combine with the delete. Introducing a low-level transaction adds no atomicity for the post-delete calculation, so this change will use the existing repository operations.
- A delete can succeed while the subsequent group query, dish lookup, owner-name lookup, or projection fails. Per the user's explicit decision, the service will not attempt compensation or rollback: the deletion remains committed and the original failure is propagated. A retry can return 404 because the target is already absent, so clients should refresh the group snapshot after an error that occurred after deletion.
- The group lookup uses a DynamoDB global secondary index, whose reads are eventually consistent. A just-deleted record can temporarily appear in the post-delete query, so the service will always exclude the selected id before projection. Concurrent additions or edits can still be absent from that one query; strong cross-participant snapshot consistency would require a different data model/versioning scheme and is outside this endpoint's scope.
- The existing GET snapshot treats a referenced catalog dish that no longer exists as a not-found failure. DELETE will reuse that behavior for consistency after the selected session has already been deleted.
- The endpoint accepts any participant session in the group, including the owner's session, while still requiring the authenticated caller to own the associated reservation.
- Route and deployment entries already exist. The implementation will avoid unnecessary configuration churn unless verification finds they are incorrect.
