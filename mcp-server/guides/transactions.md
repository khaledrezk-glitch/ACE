# Transactions, failures and safety

- mode `auto`: your whole script is one transaction, which is the right choice for 95% of edits.
- mode `manual`: you open transactions yourself (`ctx.Transact("step", () => {...})` or `new Transaction`).
  Use it when a step must commit before the next can read results, e.g. create a view, commit, then
  place it on a sheet. It's all still merged into ONE undo step, and `dry_run` still undoes everything.
- mode `readonly`: always rolled back. Use it for analysis. Changing the active view or exporting needs
  NO open transaction: do that in `manual` mode outside `ctx.Transact`.
- mode `no_transaction`: no TransactionGroup at all, for APIs that fail with "Operation is not permitted when there is
  any open transaction phase started by API client" (`RevitLinkType.LoadFrom`, `Document.SaveAs`, closing another
  model, `TransmissionData` on other files). Needs `allow_risky` after the user agreed; its dry run only compiles (nothing
  runs); the run is NOT one undo step and nothing is rolled back on an error. Wrap model edits in your own
  `using (var t = new Transaction(doc, "step")) { t.Start(); ...; t.Commit(); }`. Prefer a native tool (save_model
  save_as) when one exists.
- Warnings are deleted and returned in `revitWarnings`; errors roll the run back and appear in `revitErrors`.
  Common: "Highlighted walls overlap", "Room is not in a properly enclosed region", "Duplicate mark value".
- No new warnings: the model's warnings are compared before and after (failure definition GUID + failing ids +
  additional ids; one that only lost elements is not new). An apply that adds one the call did not accept rolls back
  whole (`stage: "new-warnings"`); the dry run reports `newWarnings` (count, items, `refuse`) and cannot be applied as it
  is. Accepting: `accept_warnings` (description part or GUID) or `accept_new_warnings: true`, given to the dry run and
  the identical apply. Avoid the warning instead where you can (join, trim or move so walls do not overlap).
- Throw to abort: `throw new Exception("Expected ~40 rooms but found 400, stopping")`. Nothing is changed.
- Elements owned by another user (workshared) can't be edited: check
  `WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByOtherUser` and skip with a reason.
- Pinned elements move only if you unpin them (`e.Pinned = false`); ask before doing that.
- Elements in groups: many edits fail inside groups; detect with `e.GroupId != ElementId.InvalidElementId`.
