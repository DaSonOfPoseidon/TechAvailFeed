// The calendar's utilisation scale (booked / available) in five steps, green (good) to red (bad).
export const utilizationSteps = [
  { below: 0.3, label: 'under 30%' },
  { below: 0.5, label: '30–50%' },
  { below: 0.7, label: '50–70%' },
  { below: 0.85, label: '70–85%' },
  { below: Infinity, label: '85% and over' },
];

export function utilizationStep(rate: number | null): number {
  if (rate === null) return 0;
  return utilizationSteps.findIndex((step) => rate < step.below) + 1;
}
