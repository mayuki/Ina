# Releasing Ina

The ina tool (package `Ina`) and `Ina.Sdk` are released together, under the version in
`Directory.Build.props`.

## Setting the version

The documentation shows how a project refers to `Ina.Sdk`, and an SDK reference needs an
exact version: MSBuild resolves neither a range nor a wildcard. The version is therefore
written in the README, which is also the page of both packages on nuget.org, and in
`docs`. `eng/SetVersion.cs` changes it there and in `Directory.Build.props` in one step:

```console
$ dotnet run eng/SetVersion.cs -- 0.2.0
```

A test (`DocumentedVersionTests`) fails when the documentation names another version than
`Directory.Build.props`, and the release workflow checks the same before it packs.

Between the merge of that change and the promotion of the packages, the README on GitHub
names a version that cannot be restored yet, so the tag should follow soon.

## Starting a release

Pushing a tag that names the version starts a release:

```console
$ git tag v0.2.0
$ git push origin v0.2.0
```

`.github/workflows/release.yaml` then packs both packages in the Release configuration;
it does not run the tests again, so tag a commit that has passed the continuous integration.
It uploads the packages to
nuget.org with [staged publishing](https://devblogs.microsoft.com/dotnet/nuget-staged-publishing/),
into the staging group `ina`, and drafts a GitHub release with the packages
attached, whose notes GitHub generates from the changes since the previous release. A tag
that names another version fails before anything is uploaded. A version with a pre-release
label, such as `0.2.0-preview.1`, becomes a pre-release on GitHub.

## Completing a release

nuget.org validates staged packages, but they are neither listed nor restorable until they
are promoted. Two steps by hand complete the release:

1. On nuget.org, in the packages of the account, choose **Promote group** for
   `ina` once both packages have passed validation. This takes an account with
   two-factor authentication. The two packages become restorable independently of each
   other, not necessarily at the same moment.
2. On GitHub, publish the draft release.

Staged packages expire when they are not promoted, so the promotion should not be left for
long. A package that failed validation is fixed by staging a replacement.

## One-time setup

The packages are uploaded with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so the repository holds no API key. It has to be set up once:

- On nuget.org, a Trusted Publishing policy of the account that owns the packages, with
  the repository owner `mayuki`, the repository `Ina`, the workflow file `release.yaml` and
  the environment `nuget`. Restricted to staging, the policy cannot publish a package
  without a promotion, even if the workflow were changed to try.
- In the repository, the secret `NUGET_USER` with the name of that account on nuget.org
  (the profile name, not the e-mail address).

The upload runs in the GitHub environment `nuget`, which GitHub creates on the first run.
Protection rules added to it, such as required reviewers or a restriction to `v*` tags,
hold back the upload and with it the draft release.
