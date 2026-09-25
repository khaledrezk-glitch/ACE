# Transactions, failures and safety

- mode `auto`: your whole script is one transaction, which is the right choice for 95% of edits.
- mode `manual`: you open transactions yourself (`ctx.Transact("step", () => {...})` or `new Transaction`).
  Use it when a step must commit before the next can read results, e.g. create a view, commit, then
  place it on a sheet. It's all still merged into ONE undo step, and `dry_run` still undoes everything.
- mode `readonly`: always rolled back. Use it for analysis. Changing the active view or exporting needs
  NO open transaction: do that in `manual` mode outside `ctx.Transact`.
- Warnings are deleted and returned in `revitWarnings`; errors roll the run back and appear in `revitErrors`.
  Common: "Highlighted walls overlap", "Room is not in a properly enclosed region", "Duplicate mark value".
- Throw to abort: `throw new Exception("Expected ~40 rooms but found 400, stopping")`. Nothing is changed.
- Elements owned by another user (workshared) can't be edited: check
  `WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByOtherUser` and skip with a reason.
- Pinned elements move only if you unpin them (`e.Pinned = false`); ask before doing that.
- Elements in groups: many edits fail inside groups; detect with `e.GroupId != ElementId.InvalidElementId`.
