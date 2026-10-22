export function techLabels(techs: { tech_id: string; tech_name: string }[]): Map<string, string> {
  const counts = new Map<string, number>();
  for (const t of techs) counts.set(t.tech_name, (counts.get(t.tech_name) ?? 0) + 1);
  return new Map(
    techs.map((t) => [
      t.tech_id,
      counts.get(t.tech_name)! > 1 ? `${t.tech_name} (${t.tech_id})` : t.tech_name,
    ]),
  );
}
