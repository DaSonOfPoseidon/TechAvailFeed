import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { FilterState } from './filter-state';

@Component({ template: '' })
class Blank {}

describe('FilterState', () => {
  async function at(url: string) {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '**', component: Blank }])],
    });
    await RouterTestingHarness.create(url);
    return { state: TestBed.inject(FilterState) };
  }

  it('reads the filters from the query string', async () => {
    const { state } = await at('/calendar?region=North&calendar=tc&days=7');
    expect(state.region()).toBe('North');
    expect(state.calendar()).toBe('tc');
    expect(state.capacity()).toEqual({
      region: 'North',
      skill: null,
      calendar: 'tc',
      start: null,
      days: 7,
    });
  });

  it('falls back to the defaults for missing or bad values', async () => {
    const { state } = await at('/calendar?days=500');
    expect(state.days()).toBe(30);
    expect(state.calendar()).toBe('install');
    expect(state.capacity().days).toBeNull();
  });

  it('writes changes back, dropping default values', async () => {
    const { state } = await at('/calendar?region=North&calendar=tc');
    await state.set({ region: null, calendar: 'install', skill: 'INS' });
    expect(TestBed.inject(Router).url).toBe('/calendar?skill=INS');
    expect(state.skill()).toBe('INS');
    expect(state.region()).toBeNull();
    expect(state.calendar()).toBe('install');
  });
});
