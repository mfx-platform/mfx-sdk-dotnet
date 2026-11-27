## What this changes and why

<!-- One or two sentences. Link the issue it fixes: "Fixes #123". -->

## How it was tested

<!-- The test that failed before this change and passes after it. -->

## Checklist

- [ ] Every commit is signed off (`git commit -s`), per [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ ] `dotnet build -c Release` passes with no warnings, and `dotnet test -c Release` passes
- [ ] The models and `docs/sdkref.json` are regenerated if the API description or a public signature changed
- [ ] A line under *Unreleased* in CHANGELOG.md
- [ ] No API keys, broker logins, passwords or account details anywhere in the change
