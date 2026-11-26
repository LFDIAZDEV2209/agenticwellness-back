# Year 365 ERP report

`GET /api/v1/program/erp/patients/{patientId}/year-365?month=YYYY-MM` returns the civil month of the specified patient's Motor 365 program. With no month, the report selects the month of the patient's current day, clamped to the program. If the time zone is unavailable, it selects the last recorded day or the start of the program.

The HTTP response is `{ "report": null }` for an accessible patient without a Motor 365 program. Otherwise, `report` contains the program start and end dates, current local date when available, available month keys, selected month, calendar days, monthly summary, and dated events. The calendar starts on day 1 and ends on day 365. Dates are ISO civil dates. A row carries a day number, a date, a `future` value (possibly null), and optional recorded adherence percentage, weight in kg, and waist circumference in cm. A null adherence means there is no daily record. Zero is a recorded value.

The summary reports the count of recorded days, mean adherence, the latest recorded value and date for each measurement, and the streak as of the earlier of month end and the patient's current program day. The streak can extend into a prior month. It requires continuous records at or above 60 percent. If the month has no records or the date cutoff is unavailable, the streak is null.

The report uses the calendar anchor from KAN-131 to select the active month and cut off future days after a time-zone change. The calendar retains the nominal date recorded for each program ordinal. The response also retains the patient's current civil date as `today`.

Events carry the persisted ID, program day, corresponding nominal date, and event kind. The insertion timestamp can reflect when the event was processed. An uncategorized mode change remains visible as a dated event.

The motor persists an `app.engine_pause_day` row for each effective day evaluated by a revision, with its nominal date, version, and independent flags for scheduled and exercise safety pauses. A date can count toward both. A late check-in reevaluates from its original ordinal through the current ordinal. A profile revision without a changed check-in records the current day. Older dates retain their previous decisions. A month with no evaluations returns null counts, and a covered month with no events of the selected type returns zero. Future dates are excluded.

Antares uses the official `GET /api/v1/people/me/plan` and `PUT /api/v1/people/me/logs/{day}` routes. It can open the check-in form offline using the last verified day of the same account and civil date. It preserves the server ordinal on retry and uses the authenticated 204 response to acknowledge the closure.

An authenticated ERP session with `Program.Profile360View` and patient scope is required. Missing authentication returns 401, a wrong audience or missing permission returns 403, and an inaccessible patient returns 404. An invalid month or a month outside the program returns 400. Inconsistent persisted day numbers and dates return 409. The response is not cacheable. The report uses canonical person, daily-log and event storage. This read route is scoped to the ERP report.

`GET /api/v1/program/erp/patients/{patientId}/year-365/export/{format}?month=YYYY-MM&lang=es` returns the same summary as a CSV or PDF attachment. `format` accepts `csv` or `pdf`. The language may be `es` (default) or `en`. The endpoint also requires `Program.Export` on the same patient. It does not include a patient identifier, clinical notes, or unselected months.

The PDF embeds Year 365 Report Sans, a renamed Latin subset of the ERP's [Plus Jakarta Sans source font](https://github.com/google/fonts/blob/2fe1e8c6b137fb13041a7cfca6fdcd8a910ea4dc/ofl/plusjakartasans/PlusJakartaSans%5Bwght%5D.ttf). The API ships the [SIL Open Font License](../../../src/CoppAddresd.Api/Resources/Year365/OFL.txt) with the font. FontTools instantiated weight 400, retained characters U+0020–U+00FF, and wrote the TrueType resource.
