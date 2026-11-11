# Security policy

## Supported versions

Only the latest release on nuget.org gets security fixes. A fix ships as a new version, not as a patch to an old one.

Zorg.Imaging has had internal reviews, fuzzing and tests against reference decoders, not an external security audit.

## Reporting a vulnerability

Report it privately through the repository's **Security** tab, using **Report a vulnerability**, or go straight to [the form](https://github.com/alftore/Zorg.Imaging/security/advisories/new). Don't open a public issue.

Please include the affected version, the input file that triggers it (or how to make one), and the impact you expect. Decoders take untrusted input, so a crash, a hang, unbounded memory or an unhandled exception type from a crafted file is a security bug.

Expect a first answer within 7 days. Confirmed problems are fixed in a new release and published as a GitHub Security Advisory, with a CVE when one is warranted. You'll be credited unless you'd rather not be.

## How releases are protected

From 0.1.0, releases go through `.github/workflows/release.yml`:

- Nobody pushes to `main` directly, administrators included. Every change arrives through a pull request with signed commits and passing checks on Windows, Linux, Native AOT and the reference-tool tests.
- A release runs only from a `v*` tag on a commit that's on `main`, and it waits for approval in the `nuget` environment.
- Packages are published with NuGet trusted publishing. There's no long-lived API key: each release gets a key that lasts one hour.
- The job that publishes doesn't run repository code. The job that builds has no access to publishing credentials.
- Every GitHub Action is pinned to a full commit SHA. Dependabot proposes updates.

The `0.1.0-preview` packages were uploaded by hand, before this workflow existed, so they have no attestation.

## Verifying a package

Each release from the workflow has a build provenance attestation for the packages it built. To check one, download the package from the GitHub Release (or the `packages` artifact of the release's workflow run) and run:

```
gh attestation verify Zorg.Imaging.<version>.nupkg --repo alftore/Zorg.Imaging
```

This doesn't work on a `.nupkg` downloaded from nuget.org. nuget.org adds a repository signature to every package, so the file's digest no longer matches the attestation. Check those with `dotnet nuget verify` instead.
