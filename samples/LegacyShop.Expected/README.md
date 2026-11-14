# Expected output (snapshot)

The files in this folder are what `dotnet-lift upgrade --yes` must produce for `samples/LegacyShop`.
`SnapshotTests` runs the upgrade on a copy of the sample and compares every file here with the result,
so any change to the generated project files shows up as a failing test and a reviewable diff.

Files that the upgrade deletes (`packages.config`) are listed in `SnapshotTests` itself.

To accept a deliberate change, run the tests with `DOTNET_LIFT_UPDATE_SNAPSHOTS=1`; they rewrite these files
from the actual output. Review the diff before committing.

For the full upgraded solution, step by step, see the
[`demo/upgraded-sample`](https://github.com/naimeakgun/dotnet-lift/commits/demo/upgraded-sample) branch.
