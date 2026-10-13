import { JobStats, KindStats } from '../api/models';
import { statsRow } from './stats-table';

describe('statsRow', () => {
  const ticket: KindStats = {
    planned: 4,
    completed: {},
    completion_rate: {},
    outcome: { completed: 3, canceled: 1 },
    outcome_rate: { completed: 0.75, canceled: 0.25 },
  };
  const job: JobStats = {
    ...ticket,
    pulled_d0: {
      total: 2,
      in_progress: 0,
      en_route: 1,
      not_started: 1,
      unknown: 0,
      prereqs_open: 0,
    },
    pulled_d0_rate: 0.5,
  };

  it('reads rates', () => {
    expect(statsRow('North', 'North', job, 'rate')).toEqual({
      id: 'North',
      name: 'North',
      planned: 4,
      completed: 0.75,
      canceled: 0.25,
      rescheduled: null,
      unscheduled: null,
      pulled: 0.5,
    });
  });

  it('reads counts, with no pulled count for tickets', () => {
    expect(statsRow('all', 'All', job, 'count')).toMatchObject({ completed: 3, pulled: 2 });
    expect(statsRow('all', 'All', ticket, 'count')).toMatchObject({
      completed: 3,
      canceled: 1,
      rescheduled: null,
      pulled: null,
    });
  });
});
