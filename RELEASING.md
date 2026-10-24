# Releasing PQAX

A release publishes the four NuGet packages, the two npm packages, and the PyPI package of one version, from one tag, with [.github/workflows/publish.yml](.github/workflows/publish.yml). Nothing is published from a working copy. The versioning policy is in section 7.1 of [docs/contracts.md](docs/contracts.md).

## Prerequisites

Once, before the first release:

| Where | What |
|---|---|
| GitHub | The repository is public. npm provenance needs it, and so do the Source Link and the release notes link of the NuGet packages. |
| GitHub | The environments `nuget`, `npm`, and `pypi`, each with a required reviewer. A secret of an environment reaches its job only after the approval. |
| nuget.org | A trusted publishing policy, owned by the account that will own the packages: repository owner `pqax-org`, repository `pqax`, workflow file `publish.yml`, environment `nuget`, allowed to push new packages and new versions of `Pqax*`. The secret `NUGET_USER` holds the nuget.org user name, not the email, of the account that created the policy. |
| npmjs.com | A trusted publisher on `@pqax/pqax`: owner `pqax-org`, repository `pqax`, workflow `publish.yml`, environment `npm`, allowed to publish. npm validates a new publisher with the first publish that goes through it, within 48 hours of its creation. No token. |
| npmjs.com | `@pqax/views` until its first publish: see the next section. After it, the same trusted publisher as `@pqax/pqax`. |
| pypi.org | A pending trusted publisher for the project `pqax`: owner `pqax-org`, repository `pqax`, workflow `publish.yml`, environment `pypi`. |

How each job signs in:

| Job | The secret is set | The secret is not set |
|---|---|---|
| `nuget` | `NUGET_API_KEY` is the key | `NuGet/login` trades the OIDC token of the job for a key valid one hour, for the user in `NUGET_USER` |
| `npm` | `NPM_TOKEN_VIEWS` goes to npm for `@pqax/views` alone, which tries trusted publishing first and then the token. `@pqax/pqax` reads no secret. | Trusted publishing for both packages |
| `pypi` | No secret is read | Trusted publishing |

## The first publish of `@pqax/views`

Done: 0.2.0 went out with a token on 21 October 2026, the trusted publisher was set and the token step removed before 0.2.1, and 0.2.1 published the views through the publisher on 24 October 2026. The steps stay for the record, for the next package that joins. npm sets a trusted publisher only on a package that exists, so the first version of such a package goes out with a token, as the first version of `@pqax/pqax` did:

1. On npmjs.com, create a granular access token that can publish packages of the scope `@pqax`, with a short expiry.
2. In the repository settings, add it as the secret `NPM_TOKEN_VIEWS` of the environment `npm`.
3. Release as the steps below say. The job publishes `@pqax/pqax` through trusted publishing, then `@pqax/views` with the token.
4. On npmjs.com, set the trusted publisher of `@pqax/views`: owner `pqax-org`, repository `pqax`, workflow `publish.yml`, environment `npm`. Revoke the token and delete the secret.
5. Remove the step "Publish the views with the token, first publish only" from `publish.yml`, and the condition and the `env` of the next step, so that the job reads no secret. The next release validates the trusted publisher, as 0.1.2 did for `@pqax/pqax`.

## Steps

1. Set the version in `src/dotnet/Directory.Build.props` (`<Version>`), `src/typescript/pqax/package.json`, `src/typescript/views/package.json`, `src/python/pyproject.toml`, and `src/python/pqax/__init__.py` (`__version__`). In `src/typescript/views/package.json`, the dependency on `@pqax/pqax` is `^` and the same version. Run `npm install` at the root, so that `package-lock.json` has the new versions. The Python README shows it too, and a test of the Python suite checks that they agree.
2. Complete the section of the version in `CHANGELOG.md`, with the date of the release. The NuGet packages link to the file as their release notes.
3. Commit, push to `main`, and wait for CI to pass.
4. Tag the commit and push the tag: `git tag v0.1.0`, then `git push origin v0.1.0`.
5. Create the GitHub release of the tag, with the section of `CHANGELOG.md` as its text, and publish it. Publish starts.
6. In the run of Publish, approve the deployments to `nuget`, `npm`, and `pypi`.
7. Check the version on nuget.org, npmjs.com, and pypi.org. nuget.org lists a version after its validation, which takes some minutes.

To start again from the beginning without a new release, run Publish by hand with the tag as its input.

## What the workflow checks

The job `verify` checks out the tag and fails unless the five versions are equal to each other and to the tag without its `v`, and unless the views depend on `^` that version of the reader. Then it runs the suites of CI on Windows: .NET in Release, Python, the two TypeScript packages, and the cross-implementation check. It packs the NuGet packages and their symbol packages, the two npm tarballs, and the Python sdist and wheel, checks the Python metadata with `twine check`, and keeps the three sets as artifacts of the run. The jobs `nuget`, `npm`, and `pypi` start when `verify` passes and their environment is approved.

## A partial publish

A version on a registry cannot be replaced. A failed job is run again, and the files of the tag do not change.

| What failed | What to do |
|---|---|
| `verify` | Nothing is published. Re-run the job if the cause was transient. Otherwise fix it, delete the release and the tag, and start again from step 1. |
| `nuget`, after some packages | Re-run the job. `--skip-duplicate` skips what nuget.org already has. The symbol packages go in a push of their own, so a missing one goes too. |
| `pypi`, after one file | Re-run the job. `skip-existing` skips the file that PyPI already has. |
| `npm` | Re-run the job. npm uploads one tarball per package, so a version is there whole or not at all, and the job skips a package whose version npm already has: a re-run after the reader went out publishes only the views. |
| A published package is wrong | Unlist it on nuget.org, deprecate it on npm, yank it on PyPI, and release the next patch version. |

Use Re-run failed jobs, which leaves alone the jobs that passed. A re-run asks for the approval of the environment again.
