# Packaging

`.github/workflows/release.yml` runs on a `v*` tag. It builds self-contained binaries for
`win-x64`, `linux-x64`, `osx-arm64` and `osx-x64`, attaches them to a GitHub Release with
`SHA256SUMS.txt`, and renders the templates in this folder into release assets:

| Template | Rendered asset | Goes to |
| --- | --- | --- |
| `homebrew/whyso.rb` | `whyso.rb` | `Formula/whyso.rb` in the tap repo (`<org>/homebrew-whyso`) |
| `scoop/whyso.json` | `whyso.json` | `bucket/whyso.json` in the bucket repo (`<org>/scoop-whyso`) |

One-time setup (maintainer):

1. Create the GitHub org (for example `getwhyso` or `whyso-dev`; check it is still free) and
   the two repos above.
2. Tag a release: `git tag v0.2.0 && git push origin v0.2.0`.
3. Copy the rendered `whyso.rb` and `whyso.json` from the release into the tap and bucket repos.

Users then install with:

```bash
brew install <org>/whyso/whyso
```

```powershell
scoop bucket add whyso https://github.com/<org>/scoop-whyso
scoop install whyso
```

Automating step 3 (pushing to the tap/bucket from the workflow) needs a token with write
access to those repos, so it is left manual for now.
