# Pitfalls

Classes of bug that reviews have caught here. Check every change against them, and add an entry when a review finds a new class.

- **Classifying a rounded double.** `Math.Round(share * 100)` on a ratio like 29/200 sees 14.499…, not 14.5, so a
  cell shown as 15% can be classified below 15%. When a rule must match a displayed rounded value, round the exact
  integers (`(200 * free + bookable) / (2 * bookable)`), not the double.
