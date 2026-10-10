import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Filters } from '../api/models';
import { FilterState } from './filter-state';

export type Field = 'region' | 'skill' | 'calendar' | 'start' | 'days';

// One row of filters above a view. Each view lists the fields its endpoint takes.
@Component({
  selector: 'app-filter-bar',
  imports: [MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <div class="bar" role="search">
      @if (has('region')) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Region</mat-label>
          <mat-select [value]="state.region()" (valueChange)="state.set({ region: $event })">
            <mat-option [value]="null">All regions</mat-option>
            @for (region of options.value()?.regions ?? []; track region) {
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
            @for (skill of options.value()?.skills ?? []; track skill) {
              <mat-option [value]="skill">{{ skill }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      @if (has('start')) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="date">
          <mat-label>From</mat-label>
          <input
            matInput
            type="date"
            [value]="state.start() ?? ''"
            (change)="state.set({ start: $any($event.target).value })"
          />
        </mat-form-field>
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
    .date {
      width: 160px;
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
  protected readonly options = httpResource<Filters>(() => '/api/v1/filters');

  protected has(field: Field): boolean {
    return this.fields().includes(field);
  }

  protected readonly dayChoices = computed(() => {
    const current = this.state.days();
    const choices = this.dayOptions();
    return choices.includes(current) ? choices : [...choices, current].sort((a, b) => a - b);
  });
}
