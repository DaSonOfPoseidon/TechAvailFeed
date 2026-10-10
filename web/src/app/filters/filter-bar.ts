import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { lastValue } from '../api/feed-api';
import { Filters } from '../api/models';
import { DateField } from '../shared/date-field';
import { Freshness } from '../shared/freshness';
import { FilterState } from './filter-state';

export type Field = 'region' | 'skill' | 'calendar' | 'start' | 'days';

// One row of filters above a view. Each view lists the fields its endpoint takes.
@Component({
  selector: 'app-filter-bar',
  imports: [DateField, MatButtonToggleModule, MatFormFieldModule, MatSelectModule],
  template: `
    <div class="bar" role="search">
      @if (has('region')) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Region</mat-label>
          <mat-select [value]="state.region()" (valueChange)="state.set({ region: $event })">
            <mat-option [value]="null">All regions</mat-option>
            @for (region of optionList()?.regions ?? []; track region) {
              <mat-option [value]="region">{{ region }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      @if (has('skill')) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Skill</mat-label>
          <mat-select [value]="state.skill()" (valueChange)="state.set({ skill: $event })">
            <mat-option [value]="null">Any skill</mat-option>
            @for (skill of optionList()?.skills ?? []; track skill) {
              <mat-option [value]="skill">{{ skill }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      @if (has('start')) {
        <app-date-field
          label="From"
          [value]="state.start()"
          (valueChange)="state.set({ start: $event })"
        />
      }
      @if (has('days')) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="days">
          <mat-label>Days</mat-label>
          <mat-select [value]="state.days()" (valueChange)="state.set({ days: $event })">
            @for (n of dayChoices(); track n) {
              <mat-option [value]="n">{{ n }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      @if (has('calendar')) {
        <mat-button-toggle-group
          hideSingleSelectionIndicator
          aria-label="Calendar"
          [value]="state.calendar()"
          (change)="state.set({ calendar: $event.value })"
        >
          <mat-button-toggle value="install">Install</mat-button-toggle>
          <mat-button-toggle value="tc">Trouble calls</mat-button-toggle>
        </mat-button-toggle-group>
      }
      <ng-content />
    </div>
  `,
  styles: `
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
    }
    mat-form-field {
      width: 220px;
    }
    .days {
      width: 96px;
    }
  `,
})
export class FilterBar {
  readonly fields = input.required<Field[]>();
  readonly dayOptions = input<number[]>([7, 14, 30, 45, 62]);
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  // Refetched with each new snapshot, so new regions, skills and techs appear.
  protected readonly options = httpResource<Filters>(() => {
    this.freshness.snapshotId();
    return '/api/v1/filters';
  });
  protected readonly optionList = lastValue(this.options);

  protected has(field: Field): boolean {
    return this.fields().includes(field);
  }

  protected readonly dayChoices = computed(() => {
    const current = this.state.days();
    const choices = this.dayOptions();
    return choices.includes(current) ? choices : [...choices, current].sort((a, b) => a - b);
  });
}
