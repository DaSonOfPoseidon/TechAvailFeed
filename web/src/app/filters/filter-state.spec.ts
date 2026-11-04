import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { FilterState, noRegion } from './filter-state';

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
      regions: null,
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
    expect(state.unit()).toBe('hours');
    expect(state.measure()).toBe('rate');
  });

  it('reads and drops the display choices', async () => {
    const { state } = await at('/calendar?unit=jobs&measure=count');
    expect(state.unit()).toBe('jobs');
    expect(state.measure()).toBe('count');
    await state.set({ unit: 'hours', measure: 'rate' });
    expect(TestBed.inject(Router).url).toBe('/calendar');
  });

  it('writes changes back, dropping default values', async () => {
    const { state } = await at('/calendar?region=North&calendar=tc');
    await state.set({ region: null, calendar: 'install', skill: 'INS' });
    expect(TestBed.inject(Router).url).toBe('/calendar?skill=INS');
    expect(state.skill()).toBe('INS');
    expect(state.region()).toBeNull();
    expect(state.calendar()).toBe('install');
  });

  it('reads the grouped regions and sends them to the API', async () => {
    const { state } = await at('/calendar?regions=grouped');
    expect(state.regions()).toBe('grouped');
    expect(state.capacity().regions).toBe('grouped');
    expect(state.regionsParam()).toBe('grouped');
  });

  it('treats any other regions value as the MBS list', async () => {
    const { state } = await at('/calendar?regions=bogus');
    expect(state.regions()).toBe('mbs');
    expect(state.capacity().regions).toBeNull();
  });

  it('drops the default regions mode from the URL', async () => {
    const { state } = await at('/calendar?regions=grouped&skill=INS');
    await state.set({ regions: 'mbs' });
    expect(TestBed.inject(Router).url).toBe('/calendar?skill=INS');
    expect(state.regions()).toBe('mbs');
  });

  it('clears a region the new list lacks when switching', async () => {
    const { state } = await at('/calendar?region=Columbia%20Core');
    await state.setRegions('grouped', ['Central MO', 'North']);
    expect(TestBed.inject(Router).url).toBe('/calendar?regions=grouped');
    expect(state.region()).toBeNull();
  });

  it('keeps a region the new list has, the no-region choice, and any region before the lists load', async () => {
    const { state } = await at('/calendar?region=North');
    await state.setRegions('grouped', ['Central MO', 'North']);
    expect(state.region()).toBe('North');
    await state.set({ region: noRegion });
    await state.setRegions('mbs', ['Columbia Core']);
    expect(state.region()).toBe(noRegion);
    await state.set({ region: 'North' });
    await state.setRegions('grouped', undefined);
    expect(state.region()).toBe('North');
    expect(state.regions()).toBe('grouped');
  });
});
