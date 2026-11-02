import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import {
  EMPTY,
  catchError,
  exhaustMap,
  filter,
  finalize,
  forkJoin,
  map,
  merge,
  Observable,
  skip,
  switchMap,
  timer,
} from 'rxjs';
import { ParticipantCardComponent } from '@app/components/group-orders/participant-card/participant-card.component';
import { GroupReservationStatus } from '@app/models/group-orders/group-status.model';
import { Participant } from '@app/models/group-orders/participant.model';
import { GuestInvite } from '@app/models/reservations/guest-invite.model';
import { GroupOrdersService } from '@app/services/group-orders/group-orders.service';
import { ToastService } from '@app/services/toast/toast.service';
import { ModalComponent } from '@app/shared/custom-components/modal/modal.component';
import { consolidatePreOrder } from '@app/shared/helpers/consolidate-pre-order';
import { getServerErrorMessage } from '@app/shared/helpers/http-error-helpers';

const SYNC_INTERVAL_MS = 15_000;

@Component({
  selector: 'app-group-order-page',
  imports: [CurrencyPipe, DatePipe, ModalComponent, ParticipantCardComponent],
  templateUrl: './group-order-page.component.html',
  styleUrl: './group-order-page.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupOrderPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly groupOrdersService = inject(GroupOrdersService);
  private readonly toastService = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly reservationId = this.route.snapshot.paramMap.get('reservationId') ?? '';
  readonly invite = signal<GuestInvite | null>(null);
  readonly participants = signal<Participant[]>([]);
  readonly guestSessionId = signal('');
  readonly status = signal<GroupReservationStatus | null>(null);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly showSubmitDialog = signal(false);
  readonly isSubmitting = signal(false);
  readonly isRefreshing = signal(false);
  readonly updatedAt = signal<Date | null>(null);
  readonly refreshFailed = signal(false);

  private readonly refreshRequest = signal(0);
  private readonly syncStopped = signal(false);
  private readonly refreshRequest$ = toObservable(this.refreshRequest).pipe(skip(1));

  readonly consolidatedOrder = computed(() => consolidatePreOrder(this.participants()));
  readonly pendingParticipantCount = computed(
    () => this.participants().filter((participant) => !participant.isSubmitted).length
  );
  readonly pendingParticipantMessage = computed(() => {
    const count = this.pendingParticipantCount();
    const participantLabel = count === 1 ? 'participant' : 'participants';
    const contraction = count === 1 ? "hasn't" : "haven't";
    return `${count} ${participantLabel} ${contraction} submitted their cart yet — their dishes won't be included in the order.`;
  });
  readonly hasParticipantDishes = computed(() =>
    this.participants().some((participant) => participant.items.length > 0)
  );
  readonly isEmpty = computed(
    () =>
      !this.loading() &&
      this.errorMessage() === null &&
      this.invite() !== null &&
      !this.hasParticipantDishes()
  );
  readonly statusLabel = computed(() => {
    switch (this.status()) {
      case 'Open':
        return 'Collecting pre-orders';
      case 'Submitted':
        return 'Sent to Restaurant';
      case 'Closed':
        return 'Closed';
      default:
        return '';
    }
  });
  readonly canSubmit = computed(
    () =>
      this.status() === 'Open' && this.consolidatedOrder().items.length > 0 && !this.isSubmitting()
  );

  constructor() {
    this.startBackgroundSync();
    this.loadData();
  }

  loadData(): void {
    if (!this.reservationId) {
      this.loading.set(false);
      this.errorMessage.set('Group reservation not found');
      return;
    }

    this.loading.set(true);
    this.errorMessage.set(null);

    this.groupOrdersService
      .getGroupInvite$(this.reservationId)
      .pipe(
        switchMap((invite) =>
          forkJoin({
            sessions: this.groupOrdersService.getSessions$(invite.GroupReservationId),
            ownerSessionId: this.groupOrdersService.getOwnerSessionId$(this.reservationId),
            status: this.groupOrdersService.getStatus$(invite.GroupReservationId),
          }).pipe(map((groupData) => ({ invite, ...groupData })))
        ),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: ({ invite, sessions, ownerSessionId, status }) => {
          this.invite.set(invite);
          this.participants.set(sessions.participants);
          this.guestSessionId.set(ownerSessionId);
          this.status.set(status.status);
          this.updatedAt.set(new Date());
          this.refreshFailed.set(false);
          this.loading.set(false);
        },
        error: (error: unknown) => {
          this.loading.set(false);
          this.errorMessage.set(
            error instanceof HttpErrorResponse && error.status === 404
              ? 'Group reservation not found'
              : 'Unable to load group reservation'
          );
        },
      });
  }

  refresh(): void {
    this.refreshRequest.update((requestNumber) => requestNumber + 1);
  }

  openSubmitDialog(): void {
    if (this.canSubmit()) {
      this.showSubmitDialog.set(true);
    }
  }

  closeSubmitDialog(): void {
    if (!this.isSubmitting()) {
      this.showSubmitDialog.set(false);
    }
  }

  submitPreOrder(): void {
    const groupReservationId = this.invite()?.GroupReservationId;
    if (!groupReservationId || !this.canSubmit()) {
      return;
    }

    this.isSubmitting.set(true);
    this.groupOrdersService
      .submit$(groupReservationId, this.reservationId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.syncStopped.set(true);
          this.status.set('Submitted');
          this.isSubmitting.set(false);
          this.showSubmitDialog.set(false);
          this.toastService.success('Pre-order sent to the restaurant');
        },
        error: (error: unknown) => {
          this.isSubmitting.set(false);
          const message = error instanceof HttpErrorResponse ? getServerErrorMessage(error) : '';
          this.toastService.error(message || 'Unable to submit the group pre-order');
        },
      });
  }

  private startBackgroundSync(): void {
    merge(timer(SYNC_INTERVAL_MS, SYNC_INTERVAL_MS), this.refreshRequest$)
      .pipe(
        filter(() => !this.syncStopped() && this.status() === 'Open' && this.invite() !== null),
        exhaustMap(() => this.syncGroupData()),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe();
  }

  private syncGroupData(): Observable<void> {
    const groupReservationId = this.invite()?.GroupReservationId;
    if (!groupReservationId) {
      return EMPTY;
    }

    this.isRefreshing.set(true);
    return forkJoin({
      sessions: this.groupOrdersService.getSessions$(groupReservationId, { silent: true }),
      status: this.groupOrdersService.getStatus$(groupReservationId, { silent: true }),
    }).pipe(
      map(({ sessions, status }) => {
        if (this.syncStopped()) {
          return;
        }

        this.participants.set(sessions.participants);
        this.status.set(status.status);
        this.updatedAt.set(new Date());
        this.refreshFailed.set(false);
      }),
      catchError(() => {
        this.refreshFailed.set(true);
        return EMPTY;
      }),
      finalize(() => this.isRefreshing.set(false))
    );
  }
}
