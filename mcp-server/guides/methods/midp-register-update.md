# Office method: MIDP register update (version 1)

Scope: general method in the tool, template per project in the vault.

For each submission: copy the discipline's tab once, named for the submission; put the submission date in the stage's
columns (for example the stage's planned and actual dates) on every delivered row; clear the dates later than the
submission in the stage date columns; never edit the original register. The project's layout (the register file, the
tab per discipline, header row, id column, id pattern, the stages and their columns) comes from the project vault's
rules/register.json (example: bep/register.example.json), so the call needs only vault, discipline, stage,
submission_date, label, the delivered files and output.

## With ACE's tools

- register_update without confirm: the check. Read the user the matched rows, "probably the same, confirm" pairs,
  delivered but not in the register, not delivered, cleared dates, headings skipped, and what came from the template.
- accept_near only for pairs the user confirmed; force_id_column only when the user confirms "the id column looks wrong"
  is a false alarm.
- The identical call with confirm: true writes the new workbook; the report CSV (every item; the reply shows counts and the first 10) goes to report_dir, the trusted vault's 04 Audits/registers or ACE's reports folder, never beside the output. The register itself is never written.
