# Contributing to the MFX Hub .NET SDK

Thank you for helping. Bug reports, fixes, documentation and tests are all welcome.

## Before you start

- **A security problem?** Do not open an issue. Follow [SECURITY.md](SECURITY.md).
- **A question about your account, a terminal or the API itself?** Write to support@mfxhub.com.
  The documentation is at [docs.mfxhub.com](https://docs.mfxhub.com).
- **A bug in the SDK?** Open an issue with the SDK version (`MfxClient.UserAgent` names it), your
  .NET version and operating system, what you did, what you expected and what happened. Leave out
  API keys, broker logins and passwords, and anything else that identifies an account.
- **A larger change?** Open an issue first, so we can agree on the shape before you write it.

## Setting up

You need the .NET 10 SDK (`global.json` pins the feature band). The shared test suite also needs
Python 3.12 and [uv](https://docs.astral.sh/uv/), and the model generator needs PowerShell 7.

```bash
dotnet build -c Release                                   # warnings are errors
dotnet test tests/MfxHub.Sdk.Tests -c Release             # the unit tests, with the surface check

# The shared SDK test suite, against its fake hub
uv venv --python 3.12 .hub
uv pip install --python .hub --require-hashes -r conformance/hub-requirements.txt
MFX_SUITE_PYTHON=$PWD/.hub/bin/python dotnet test conformance -c Release

pwsh tools/generate.ps1                                   # the models, from spec/openapi.yaml
dotnet run --project tools/SdkRef -c Release              # docs/sdkref.json, the SDK reference
python3 tools/check_public.py --tree .                    # nothing internal in the repository
```

The suites need no network and no MFX Hub account: every call is answered by a recorded or
scripted response.

## What a change needs

- **Tests first.** A fix comes with the test that failed before it. Each test's XML summary reads
  as Given / When / Then.
- **Warnings are errors.** Nullable reference types, the recommended analysers and the code style in
  `.editorconfig` all hold; `dotnet build` fails on any warning.
- **XML documentation.** Every public type and member says what it is for. Comments explain *why*,
  not *what*. They are published, in the package and in the SDK reference.
- **The SDK's rules hold.** Every write takes an `idempotencyKey` and none invents one; execution
  methods return a result and never throw for what the platform or the network does; prices,
  volumes and money are `decimal`, never `double`; credentials never reach a log, a `ToString` or an
  exception.
- **The public surface matches the API.** The unit tests compare it with `spec/surface.json`
  through the idiom map in `tools/surface-check/map.json`, and the shared suite in `conformance/`
  pins the behaviour every MFX Hub SDK shares.
- **No hand edits in `src/MfxHub.Sdk/Generated`.** It is generated from `spec/openapi.yaml` by
  `tools/generate.ps1`, and CI regenerates it and fails on any difference. A change to the API
  description comes from MFX Hub, not from a pull request here.
- **`docs/sdkref.json` is regenerated** when a public doc comment or signature changes; CI fails when
  the committed file differs from what `tools/SdkRef` writes.
- **A line in [CHANGELOG.md](CHANGELOG.md)** under *Unreleased*, written for someone upgrading.

## Signing off your commits

Every commit must carry a Developer Certificate of Origin sign-off: a statement that you wrote the
change, or otherwise have the right to submit it under this project's licence. The full text is at
[developercertificate.org](https://developercertificate.org). Add it with `-s`:

```bash
git commit -s -m "fix: a stream reconnect keeps its subscriptions"
```

which appends `Signed-off-by: Your Name <you@example.com>`, using the name and email in your git
configuration. A pull request with an unsigned commit cannot be merged; `git commit --amend -s`
or `git rebase --signoff` fixes it.

## How a pull request lands

The SDK is developed in a private repository alongside the platform it calls, and each release is
published here as a single commit. So a pull request here is not merged directly. Instead:

1. We review it here, in public, as usual.
2. Once it is approved, we apply it to the next release, keeping you as the commit's author and
   your sign-off.
3. When that release is published, we close the pull request with a link to it, and the
   changelog credits you.

The release that carries your change is usually the next one. If we cannot take a change, we say
why in the pull request.

## Licence

By contributing, you agree that your contribution is licensed under the
[Apache License 2.0](LICENSE), the same as the rest of the project.

## Code of conduct

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).
