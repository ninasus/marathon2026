import { CurrencyPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { GROUP_ORDER_PREVIEW } from '@app/shared/mocks/group-orders/group-order.preview';
import { ParticipantCardComponent } from '@app/components/group-orders/participant-card/participant-card.component';

@Component({
  selector: 'app-group-order-page',
  imports: [CurrencyPipe, DatePipe, ParticipantCardComponent],
  templateUrl: './group-order-page.component.html',
  styleUrl: './group-order-page.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupOrderPageComponent {
  readonly invite = GROUP_ORDER_PREVIEW.invite;
  readonly participants = GROUP_ORDER_PREVIEW.sessions.participants;
  readonly grandTotal = GROUP_ORDER_PREVIEW.sessions.total;
  readonly pendingParticipantCount = this.participants.filter(
    (participant) => !participant.isSubmitted
  ).length;
}
