import { Component, Injectable, computed, input, output } from '@angular/core';
import {
  DateAdapter,
  MAT_DATE_FORMATS,
  MAT_NATIVE_DATE_FORMATS,
  NativeDateAdapter,
} from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

const isoDay = /^(\d{4})-(\d{2})-(\d{2})$/;

// The query string's "2026-10-10" as a local-midnight Date, built from its parts. An impossible
// day (2026-02-31, month 13) is null rather than rolled over into another date. setFullYear keeps
// years below 100 from being read as 19xx.
export function fromDay(day: string | null): Date | null {
  const match = day?.match(isoDay);
  if (!match) return null;
  const [year, month, date] = [Number(match[1]), Number(match[2]) - 1, Number(match[3])];
  const parsed = new Date(2000, 0, 1);
  parsed.setFullYear(year, month, date);
  return parsed.getFullYear() === year && parsed.getMonth() === month && parsed.getDate() === date
    ? parsed
    : null;
}

export function toDay(date: Date | null): string | null {
  if (!date || isNaN(date.getTime())) return null;
  const pad = (n: number) => String(n).padStart(2, '0');
  const year = String(date.getFullYear()).padStart(4, '0');
  return `${year}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

// Material's native adapter reads "2026-10-10" as UTC midnight, which is the previous day west of
// Greenwich. This one reads it as the local day; anything else (3/1/2026) parses as before.
@Injectable()
export class LocalDateAdapter extends NativeDateAdapter {
  override parse(value: unknown, parseFormat: unknown): Date | null {
    // An ISO-shaped value is a real day or invalid; the native parser would roll it over.
    if (typeof value === 'string' && isoDay.test(value.trim()))
      return fromDay(value.trim()) ?? this.invalid();
    return super.parse(value, parseFormat);
  }

  override deserialize(value: unknown): Date | null {
    return (typeof value === 'string' && fromDay(value)) || super.deserialize(value);
  }
}

// A date filter that opens a calendar on click and still takes typing. Emits null when cleared.
@Component({
  selector: 'app-date-field',
  imports: [MatDatepickerModule, MatFormFieldModule, MatInputModule],
  providers: [
    { provide: DateAdapter, useClass: LocalDateAdapter },
    { provide: MAT_DATE_FORMATS, useValue: MAT_NATIVE_DATE_FORMATS },
  ],
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic">
      <mat-label>{{ label() }}</mat-label>
      <input
        matInput
        [matDatepicker]="picker"
        [value]="date()"
        (click)="picker.open()"
        (dateChange)="valueChange.emit(toDay($event.value))"
      />
      <mat-datepicker-toggle matIconSuffix [for]="picker" />
      <mat-datepicker #picker />
    </mat-form-field>
  `,
  styles: `
    mat-form-field {
      width: 170px;
    }
  `,
})
export class DateField {
  readonly label = input.required<string>();
  readonly value = input<string | null>(null);
  readonly valueChange = output<string | null>();
  protected readonly toDay = toDay;

  protected readonly date = computed(() => fromDay(this.value()));
}
