# Feed format

The scheduling system emails one CSV attachment per scheduled run. Headers are matched case-insensitively,
because some exporters upper-case them. A header with a `kind` column selects the **blocks** format below; otherwise the parser
expects the legacy **slots** (pre-computed gaps) format. `tests/fixtures/sample_feed.csv` is a fake example.

## Blocks format

Required columns (`feed/parse.py` `BLOCK_COLUMNS`):

| Column | Meaning |
|---|---|
| `generated_at` | When the query ran (ISO 8601 with offset); used for delivery latency |
| `kind` | `shift`, `shift_tc`, `job`, `ticket`, `time_off`, `job_moved`, `ticket_moved`, `job_unassigned`, `ticket_unassigned` |
| `work_date` | The schedule date |
| `tech_id`, `tech_name` | Assigned tech (blank for `*_unassigned`) |
| `starts_at`, `ends_at` | Local timestamps |
| `ref_id` | Job or ticket ID |
| `status` | Task/ticket status code |
| `region` | Shift: the tech's region. Work: the region the service address geocodes to |
| `skills` | Comma-separated skill codes (shifts) |

Optional columns (`OPTIONAL_BLOCK_COLUMNS`), read as blank when absent so older exports still parse:
`department`, `task_type`, `modified_at`, `modified_by`, `enroute_at`, `inprogress_at`, `Pre-Reqs Status`,
`address_issue`, `latitude`, `longitude`, `gps_precision`.

The feed never carries address text or customer names: only an `address_issue` flag and coordinates, which
are rounded before being served (see the README's **Coordinates** section).

## Slots format (legacy)

`generated_at`, `work_date`, `tech_id`, `tech_name`, `open_from`, `open_until`, `open_minutes`, `region`, `skills`.
