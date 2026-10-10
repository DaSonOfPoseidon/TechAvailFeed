import { httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { FeedApi, Query, errorDetail } from '../api/feed-api';
import { Filters } from '../api/models';
import { FilterState } from '../filters/filter-state';
import { today } from '../shared/time';

type Report = 'arrivals' | 'jeopardy';

// Date, time and VP region for the arrival and jeopardy workbooks.
@Component({
  selector: 'app-report-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  template: `
    <h2 mat-dialog-title>
      {{ report === 'arrivals' ? 'On-time arrivals' : 'Jobs in jeopardy update' }}
    </h2>
    <mat-dialog-content class="fields">
      <mat-form-field appearance="outline">
        <mat-label>Date</mat-label>
        <input matInput type="date" [max]="maxDate" [(ngModel)]="date" />
      </mat-form-field>
      @if (report === 'jeopardy') {
        <mat-form-field appearance="outline">
          <mat-label>Run</mat-label>
          <mat-select [(ngModel)]="at">
            <mat-option [value]="null">Latest of the day</mat-option>
            @for (time of runs; track time) {
              <mat-option [value]="time">{{ time }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      <mat-form-field appearance="outline">
        <mat-label>{{ report === 'jeopardy' ? 'VP region' : 'Region' }}</mat-label>
        <mat-select [(ngModel)]="region">
          <mat-option [value]="null">All regions</mat-option>
          @for (name of regions(); track name) {
            <mat-option [value]="name">{{ name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="null">Cancel</button>
      <button mat-flat-button [mat-dialog-close]="query()" [disabled]="!date()">Download</button>
    </mat-dialog-actions>
  `,
  styles: `
    .fields {
      display: grid;
      gap: 4px;
      min-width: min(320px, 80vw);
    }
  `,
})
export class ReportDialog {
  protected readonly report: Report = inject(MAT_DIALOG_DATA);
  private readonly filters = httpResource<Filters>(() => '/api/v1/filters');
  protected readonly maxDate = today();
  protected readonly runs = ['10:00', '13:00', '15:00', '17:00'];
  protected readonly date = signal(today());
  protected readonly at = signal<string | null>(null);
  protected readonly region = signal<string | null>(null);

  protected regions(): string[] {
    const filters = this.filters.hasValue() ? this.filters.value() : undefined;
    return (this.report === 'jeopardy' ? filters?.vp_regions : filters?.regions) ?? [];
  }

  protected query(): Query {
    return {
      date: this.date(),
      at: this.report === 'jeopardy' ? this.at() : null,
      region: this.region(),
    };
  }
}

// The Excel exports. The capacity and diagnostics ones take the current view's filters.
@Component({
  selector: 'app-export-menu',
  imports: [MatButtonModule, MatIconModule, MatMenuModule],
  template: `
    <button mat-button [matMenuTriggerFor]="menu" [disabled]="busy()">
      <mat-icon>download</mat-icon>
      {{ busy() ? 'Downloading…' : 'Excel' }}
    </button>
    <mat-menu #menu="matMenu" xPosition="before">
      <button mat-menu-item (click)="save('capacity.xlsx', state.capacity())">
        Capacity, with current filters
      </button>
      <button mat-menu-item (click)="save('outcomes.xlsx', outcomeQuery())">Outcome history</button>
      <button mat-menu-item (click)="save('diagnostics.xlsx', { group: state.get('group') })">
        Data-quality checks
      </button>
      <button mat-menu-item (click)="ask('arrivals')">On-time arrivals…</button>
      <button mat-menu-item (click)="ask('jeopardy')">Jobs in jeopardy update…</button>
    </mat-menu>
  `,
})
export class ExportMenu {
  protected readonly state = inject(FilterState);
  private readonly api = inject(FeedApi);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  protected readonly busy = signal(false);

  protected outcomeQuery(): Query {
    return {
      start: this.state.get('from'),
      end: this.state.get('to'),
      region: this.state.region(),
      tech: this.state.get('tech'),
    };
  }

  protected ask(report: Report): void {
    this.dialog
      .open(ReportDialog, { data: report })
      .afterClosed()
      .subscribe((query?: Query) => {
        if (query) this.save(`${report}.xlsx`, query);
      });
  }

  protected async save(path: string, query: Query): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.download(path, query);
    } catch (error) {
      this.snack.open(await errorDetail(error), 'Dismiss', { duration: 8000 });
    } finally {
      this.busy.set(false);
    }
  }
}
